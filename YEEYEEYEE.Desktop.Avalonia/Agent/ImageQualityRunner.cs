using System.Net.Http;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 给一批图判档：**先本地筛客观坏图，再让模型给剩下的排名次**。
///
/// 两层分开是有原因的：本地那层免费、确定、能指出具体是哪一条指标命中（读不出来 / 一个颜色 / 尺寸不对），
/// 但它**不判好看**；「哪张更好」这种判断只有模型能做，而它要花钱、也不确定——
/// 所以它默认关、失败时如实说不成，绝不退回到「那就都算合格」。
/// </summary>
internal static class ImageQualityRunner
{
    /// <summary>
    /// 判完把结果写回 <paramref name="batch"/> 的格子上，并返回**一行给状态栏的说明**。
    /// 判不成时返回的是原因本身，不是「失败」两个字——用户需要知道是缺模型、还是名次不规范。
    /// </summary>
    public static async Task<string> RunAsync(NodeImageBatch batch, CancellationToken cancellationToken = default)
    {
        var candidates = new List<int>();
        for (var index = 0; index < batch.Count; index++)
        {
            if (batch.SlotAt(index) is { Removed: false, Status: BatchSlotStatus.Done, Path.Length: > 0 })
                candidates.Add(index);
        }
        if (candidates.Count == 0) return "没有可判的图。";

        // 「尺寸明显偏小」那一条要有依据才查：依据是这次出图**要求**的尺寸。
        var expected = batch.Request is { Width: > 0, Height: > 0 } request
            ? Math.Min(request.Width, request.Height)
            : 0;

        // 第一层：本地技术筛查。命中就是白档（客观坏图），既不送模型、也不参与排名。
        var rankable = new List<int>();
        foreach (var index in candidates)
        {
            var slot = batch.Slots[index];
            var facts = ImageQualityProbe.Measure(slot.Path, expected);
            if (TechnicalScreening.Screen(facts) is { } broken) slot.Quality = broken;
            else
            {
                slot.Quality = null;
                rankable.Add(index);
            }
        }

        var brokenCount = candidates.Count - rankable.Count;
        var brokenNote = brokenCount > 0 ? $"，其中 {brokenCount} 张是坏图（本地判定）" : string.Empty;

        if (rankable.Count < QualityJudgement.MinImagesToRank)
            return $"判完了：本地筛出 {brokenCount} 张坏图{brokenNote}，可比的不足两张，不排名次。";

        // 第二层：模型排名次。没有可用的看图模型时**如实说不成**，不退回到「都算合格」。
        var completer = AiProviderFactory.CreateImageJsonCompleter();
        if (completer is null)
            return $"本地筛过了{brokenNote}，但没法让模型排名次：需要一个开了「支持图片输入」的模型（见接入设置的高级配置）。";

        var images = new List<string>();
        foreach (var index in rankable)
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
                ImageQualityJudge.BuildUserPrompt(batch.NodeTitle, batch.Prompt, rankable.Count),
                images,
                cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return $"让模型排名次没成：{error.Message}";
        }

        var parsed = QualityJudgement.ParseRanking(reply, rankable.Count);
        if (parsed.Ranking is null) return $"模型给的名次用不了：{parsed.Error}";

        foreach (var (index, quality) in QualityJudgement.ToGrades(rankable, parsed.Ranking))
            batch.Slots[index].Quality = quality;

        // 成功时**只说「判好了」**，不报档位分布：那是开奖里预兆那一拍要用的信息，
        // 提前写在状态栏等于先把结果说了。
        return $"档位已判好（模型）{brokenNote}。点那排卡开奖。";
    }
}
