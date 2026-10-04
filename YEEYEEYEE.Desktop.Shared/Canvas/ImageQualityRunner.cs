using System.Net.Http;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 把一张图**量**成 <see cref="ImageFacts"/>。做成接缝是因为**只有界面层能解码图片**
/// （桌面端用 Avalonia 的位图解码器）——规则本身在 <see cref="TechnicalScreening"/> 里、可测，
/// 而「量」的那一半必须由调用方注入，否则这条链的中段在测试里根本跑不起来。
/// </summary>
/// <param name="path">图片文件路径。</param>
/// <param name="expectedShortSide">这次出图要求的短边；0 表示不检查尺寸那一条。</param>
public delegate ImageFacts ImageFactsProbe(string path, int expectedShortSide);

/// <summary>
/// 给一批图判档：**先本地筛客观坏图，再让模型给剩下的逐张打分**。
///
/// 两层分开是有原因的：本地那层免费、确定、能指出具体是哪一条指标命中（读不出来 / 一个颜色 / 尺寸不对），
/// 但它**不判好看**；「这张够不够好」这种判断只有模型能做，而它要花钱、也不确定——
/// 所以它默认关、失败时如实说不成，绝不退回到「那就都算合格」。
///
/// 判的是**绝对分**（照着我们写出去的提示词与负面提示词），所以**一张也能判**——
/// 不需要有第二张来比较。踩中负面提示词的会被标成裂纹卡。
///
/// 为什么这一层在共享层、而不是界面层：它全是「谁该被送出去判、判回来的编号落到哪一格、
/// 失败时说什么」这类规矩，出错的表现是**档位挂错格子**或**悄悄少判一张**——两种都很难在界面上看出来。
/// 把「量图」与「谁来打分」做成注入的接缝，这些规矩才测得动（见 Agent.Tests 的「抽卡判档」一组）。
/// </summary>
public static class ImageQualityRunner
{
    /// <summary>
    /// 判完把结果写回 <paramref name="batch"/> 的格子上，并返回**一行给状态栏的说明**。
    /// 判不成时返回的是原因本身，不是「失败」两个字——用户需要知道是缺模型、还是模型给的分数不规范。
    /// </summary>
    /// <param name="batch">这一批。</param>
    /// <param name="probe">量图的那一环（界面层注入；测试里给假的事实）。</param>
    /// <param name="completerFactory">谁来打分。默认用当前接入的模型；返回 null = 没有可读图的模型。</param>
    /// <param name="onlyIndices">只判指定的那几格（自动重出后补判用）：重出只换了裂纹的那几张，
    /// 把整批再判一遍等于为没变的图白花一次模型调用。</param>
    public static async Task<string> RunAsync(
        NodeImageBatch batch,
        ImageFactsProbe probe,
        Func<IAiImageJsonCompleter?>? completerFactory = null,
        IReadOnlyList<int>? onlyIndices = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(probe);

        var candidates = new List<int>();
        for (var index = 0; index < batch.Count; index++)
        {
            if (onlyIndices is not null && !onlyIndices.Contains(index)) continue;
            if (batch.SlotAt(index) is { Removed: false, Status: BatchSlotStatus.Done, Path.Length: > 0 })
                candidates.Add(index);
        }
        if (candidates.Count == 0) return "没有可判的图。";

        // 「尺寸明显偏小」那一条要有依据才查：依据是这次出图**要求**的尺寸。
        var expected = batch.Request is { Width: > 0, Height: > 0 } request
            ? Math.Min(request.Width, request.Height)
            : 0;

        // 第一层：本地技术筛查。命中就是白档（客观坏图），既不送模型、也没有分数。
        var judgeable = new List<int>();
        foreach (var index in candidates)
        {
            var slot = batch.Slots[index];
            var facts = probe(slot.Path, expected);
            if (TechnicalScreening.Screen(facts) is { } broken) slot.Quality = broken;
            else
            {
                slot.Quality = null;
                judgeable.Add(index);
            }
        }

        var brokenCount = candidates.Count - judgeable.Count;
        var brokenNote = brokenCount > 0 ? $"其中 {brokenCount} 张是坏图（本地判定）" : string.Empty;
        if (judgeable.Count == 0) return $"本地筛出 {brokenCount} 张坏图，没有可判的图了。";

        // 第二层：模型逐张打分。没有可用的看图模型时**如实说不成**，不退回到「都算合格」。
        var completer = (completerFactory ?? AiProviderFactory.CreateImageJsonCompleter)();
        if (completer is null)
            return $"本地筛过了（{brokenNote}），但没法让模型打分：需要一个开了「支持图片输入」的模型（见接入设置的高级配置）。";

        var images = new List<string>();
        foreach (var index in judgeable)
        {
            var loaded = AgentAttachmentLoader.Load(batch.Slots[index].Path);
            if (loaded.Attachment is null) return $"判不成，读图失败：{loaded.Error}";
            images.Add(loaded.Attachment.Payload);
        }

        string reply;
        try
        {
            reply = await completer.CompleteJsonWithImagesAsync(
                ImageQualityJudge.SystemPrompt,
                ImageQualityJudge.BuildUserPrompt(batch.NodeContext, batch.Prompt, batch.Negative, judgeable.Count),
                images,
                cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return $"让模型打分没成：{error.Message}";
        }

        var parsed = QualityJudgement.ParseScores(reply, judgeable.Count);
        if (parsed.Scores is null) return $"模型给的分数用不了：{parsed.Error}";

        foreach (var (index, quality) in QualityJudgement.ToGrades(judgeable, parsed.Scores))
            batch.Slots[index].Quality = quality;

        // 成功时**只说「判好了」**，不报档位分布与裂纹张数：那是开奖里预兆那一拍要用的信息，
        // 提前写在状态栏等于先把结果说了。
        var note = brokenNote.Length > 0 ? $"（{brokenNote}）" : string.Empty;
        return $"档位已判好（模型）{note}。点那排卡开奖。";
    }
}
