namespace DreamForge.Desktop;

public sealed class AiGenerationRequest
{
    public WorkflowNode Node { get; init; } = new();

    /// <summary>用户为该节点填写的生成指令（可选），用于表达“想要什么内容”。</summary>
    public string Instruction { get; init; } = string.Empty;

    /// <summary>节点引用的设定描述（实体核心 + 变体差异 + 场景布局），未引用时为空。</summary>
    public string Reference { get; init; } = string.Empty;

    public string? Answer { get; init; }

    public string NodeTreeContext { get; init; } = string.Empty;
}

// AiNodeProposal（AI 建议创建的下游节点）已迁到 DreamForge.Desktop.Core/AiNodeProposal.cs（共享核心），
// 本项目通过 csproj 里的 Compile Link 引用同一份源码——不要在这里重新定义，
// 否则 Avalonia 端链接本文件时会与核心里的同名类型冲突（CS0436）。

public sealed class AiGenerationResult
{
    public string? Output { get; init; }
    public string? Question { get; init; }
    public string Provider { get; init; } = "LocalAiProvider";
    public string Model { get; init; } = "local-placeholder-v1";
    public bool NeedsUserInput => !string.IsNullOrWhiteSpace(Question);
    public IReadOnlyList<AiNodeProposal> Proposals { get; init; } = Array.Empty<AiNodeProposal>();
}

public interface IAiProvider
{
    Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>规划参考图合成时，描述一条引用（供大模型理解「有哪几张图」）。</summary>
public sealed record ReferenceItemInfo(int Index, string EntityName, string Kind, string VariantName, string Description, bool HasImage)
{
    /// <summary>给模型看的单行描述，索引与 ref:N 对应。</summary>
    public string Describe() =>
        $"ref:{Index} = {Kind}「{EntityName} · {VariantName}」{(HasImage ? "（有参考图）" : "（没有参考图）")}" +
        (string.IsNullOrWhiteSpace(Description) ? string.Empty : $"：{Description}");
}

public sealed class CompositionPlanningRequest
{
    public string NodeTitle { get; init; } = string.Empty;
    public string NodeContent { get; init; } = string.Empty;
    public IReadOnlyList<ReferenceItemInfo> References { get; init; } = Array.Empty<ReferenceItemInfo>();

    /// <summary>执行方一次最多能同时使用几张参考图；分步合成要让每步都不超过它。</summary>
    public int MaxImagesPerStep { get; init; } = 2;

    /// <summary>用户直接输入的要求，可为空。</summary>
    public string Instruction { get; init; } = string.Empty;
}

/// <summary>合成计划里的一步：用一个明确的关系把若干输入并到一张图里。</summary>
public sealed class CompositionStepPlan
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>本步的输入，取值形如 ref:0（第几条引用的参考图）或前置步骤 id。</summary>
    public IReadOnlyList<string> Inputs { get; set; } = Array.Empty<string>();

    public string Prompt { get; set; } = string.Empty;
    public double Denoise { get; set; } = 0.55;
}

/// <summary>参考图合成方案：把多于上限的参考图拆成若干步收敛。</summary>
public sealed class CompositionPlan
{
    /// <summary>方案说明，用于向用户解释为什么这样排。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>信息不足时模型提出的问题；不为空时表示需要用户补充。</summary>
    public string? Question { get; init; }

    public IReadOnlyList<CompositionStepPlan> Steps { get; init; } = Array.Empty<CompositionStepPlan>();

    /// <summary>备选策略的自然语言描述，用户可选中后要求重新规划。</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = Array.Empty<string>();

    public bool IsUsable => Steps.Count > 0;
}

/// <summary>
/// 能规划参考图合成方案的大模型。规划只产出「编排」，不产出内容，
/// 因此它属于技能的规划环节而不是执行环节。
/// </summary>
public interface ICompositionPlanner
{
    Task<CompositionPlan> PlanCompositionAsync(CompositionPlanningRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 离线兜底规划：按引用顺序两两收敛。没有配置大模型时用它，
/// 保证分步合成在离线状态下也能用。
/// </summary>
public static class LocalCompositionPlanner
{
    public static CompositionPlan Plan(CompositionPlanningRequest request)
    {
        var usable = request.References.Where(reference => reference.HasImage).OrderBy(reference => reference.Index).ToList();
        if (usable.Count == 0) return new CompositionPlan { Summary = "没有可用的参考图。" };

        var perStep = Math.Max(2, request.MaxImagesPerStep);
        var steps = new List<CompositionStepPlan>();
        var cursor = new List<string> { $"ref:{usable[0].Index}" };
        var index = 1;
        var stepNumber = 1;
        while (index < usable.Count)
        {
            var batch = new List<string>(cursor);
            while (batch.Count < perStep && index < usable.Count)
                batch.Add($"ref:{usable[index++].Index}");

            var id = $"s{stepNumber}";
            steps.Add(new CompositionStepPlan
            {
                Id = id,
                Name = $"合成第 {stepNumber} 步",
                Inputs = batch,
                Prompt = stepNumber == 1
                    ? $"把图中的多个主体合成到同一画面，保持各自的形态、颜色与比例不变：{DescribeInputs(batch, usable)}。"
                    : $"在上一步画面的基础上并入{DescribeInputs(batch.Skip(1).ToList(), usable)}，保持已有部分不变。",
                Denoise = 0.55
            });
            cursor = new List<string> { id };
            stepNumber++;
        }

        if (steps.Count == 0)
            steps.Add(new CompositionStepPlan
            {
                Id = "s1",
                Name = "单图整理",
                Inputs = cursor,
                Prompt = "整理这张参考图：保持主体特征不变，输出清晰、构图完整的画面。",
                Denoise = 0.35
            });

        return new CompositionPlan
        {
            Summary = $"按引用顺序两两合成，共 {steps.Count} 步（每步最多 {perStep} 张）。",
            Steps = steps,
            Alternatives = new[]
            {
                "优先保证第一个主体的还原度，其余元素只做弱化并入",
                "把所有主体按画面左右分布排列，避免互相遮挡"
            }
        };
    }

    private static string DescribeInputs(IReadOnlyList<string> inputs, IReadOnlyList<ReferenceItemInfo> usable)
    {
        var names = inputs
            .Select(input => usable.FirstOrDefault(reference => $"ref:{reference.Index}" == input)?.EntityName)
            .Where(name => !string.IsNullOrWhiteSpace(name));
        return string.Join("、", names);
    }
}

/// <summary>
/// 离线模拟 Provider：按用户填写的指令与节点内容做关键词启发式，产出可继续编辑的草稿。
/// 输出自带“本地模拟”标识，不会伪装成真实模型结果。
/// </summary>
public sealed class LocalAiProvider : IAiProvider, ICompositionPlanner, IAiChatProvider
{
    /// <summary>
    /// 离线对话：不伪装成模型回答，明确说明当前没有接入大模型，并给出接入入口提示。
    /// </summary>
    public Task<string> ChatAsync(IReadOnlyList<AiChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var last = messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        var echo = last is null || string.IsNullOrWhiteSpace(last.Content)
            ? string.Empty
            : $"\n\n你刚才说的是：「{Trim(last.Content, 80)}」";
        return Task.FromResult(
            "当前是**本地模拟**，没有接入大模型，所以我无法真正回答你的问题。" + echo +
            "\n\n要让我能真正参与创作，请在左侧图标栏点「设」→「接入引导」，选择一个服务商并填入 API 密钥后点「测试连接」。");
    }

    /// <summary>离线规划：退化为按引用顺序两两收敛，并在摘要里说明这是本地兜底。</summary>
    public Task<CompositionPlan> PlanCompositionAsync(CompositionPlanningRequest request, CancellationToken cancellationToken = default)
    {
        var plan = LocalCompositionPlanner.Plan(request);
        var summary = string.IsNullOrWhiteSpace(plan.Summary)
            ? plan
            : new CompositionPlan
            {
                Summary = "（本地兜底规划，未接入大模型）" + plan.Summary,
                Question = plan.Question,
                Steps = plan.Steps,
                Alternatives = plan.Alternatives
            };
        return Task.FromResult(summary);
    }

    public async Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(120, cancellationToken);
        var node = request.Node;
        if (request.Instruction.Contains("下游候选", StringComparison.Ordinal))
        {
            await Task.Delay(120, cancellationToken);
            var context = string.IsNullOrWhiteSpace(request.NodeTreeContext) ? node.Content : request.NodeTreeContext;
            return new AiGenerationResult
            {
                Output = "本地模拟无法进行真实语义推理；以下候选仅用于检查选择流程。",
                Proposals = new List<AiNodeProposal>
                {
                    new() { Title = $"{node.Title} · 后续发展", Content = $"基于节点树上下文继续推进：{Trim(context, 160)}" },
                    new() { Title = $"{node.Title} · 关键要素", Content = $"从节点树上下文提炼人物、场景或制作要素：{Trim(context, 160)}" }
                }
            };
        }
        var input = string.IsNullOrWhiteSpace(request.Answer) ? node.Content : $"{node.Content}\n用户补充：{request.Answer}";
        if (string.IsNullOrWhiteSpace(input))
            return new AiGenerationResult { Question = $"请补充“{node.Title}”的内容、目标或关键约束。" };

        var basis = $"{request.Instruction}\n{request.NodeTreeContext}\n{input}";
        var (output, proposals) = Generate(basis, input);
        return new AiGenerationResult { Output = output, Proposals = proposals };
    }

    private static (string Output, List<AiNodeProposal> Proposals) Generate(string basis, string input) => basis switch
    {
        _ when Has(basis, "分镜", "镜头") => (
            $"分镜草案（本地模拟）\n依据：{Trim(input, 80)}\n镜头 1：建立环境与人物关系\n镜头 2：呈现冲突并推进动作\n镜头 3：聚焦情绪与关键转折",
            new List<AiNodeProposal>
            {
                new() { Title = "人物", Content = $"从分镜提取的角色：{Trim(input, 60)}" },
                new() { Title = "场景", Content = $"从分镜提取的场景：{Trim(input, 60)}" }
            }),
        _ when Has(basis, "人物", "角色") => (
            $"人物设定（本地模拟）\n依据：{Trim(input, 80)}\n身份：故事核心角色\n目标：推动情节并完成变化\n特征：有明确动机与可见阻力",
            new List<AiNodeProposal>
            {
                new() { Title = "人物参考图", Content = $"人物参考图提示词：{Trim(input, 60)}\n风格：电影感、清晰构图、主体突出" }
            }),
        _ when Has(basis, "场景") => (
            $"场景设定（本地模拟）\n依据：{Trim(input, 80)}\n功能：承载冲突与行动\n氛围：服务当前章节情绪\n视觉要素：空间层次、关键道具、光线方向",
            new List<AiNodeProposal>
            {
                new() { Title = "场景参考图", Content = $"场景参考图提示词：{Trim(input, 60)}\n风格：电影感、清晰构图、环境层次分明" }
            }),
        _ when Has(basis, "章节", "正文", "第一章") => (
            $"章节分析（本地模拟）\n主题：{Trim(input, 80)}\n结构：建立冲突 → 推进目标 → 高潮 → 收束",
            new List<AiNodeProposal>
            {
                new() { Title = "分镜", Content = "由本章正文拆分镜头" },
                new() { Title = "人物", Content = "从本章提取的角色设定" },
                new() { Title = "场景", Content = "从本章提取的场景设定" }
            }),
        _ when Has(basis, "企划", "大纲", "故事") => (
            $"故事大纲（本地模拟）\n主题：{Trim(input, 80)}\n目标：整理为可继续编辑的内容\n下一步：检查事实、语气和细节后采纳。",
            new List<AiNodeProposal>
            {
                new() { Title = "分镜", Content = "按大纲拆分镜头" }
            }),
        _ => (
            $"AI 草案（本地模拟）\n依据：{Trim(input, 80)}\n目标：整理为可继续编辑的内容\n下一步：检查事实、语气和细节后采纳。",
            new List<AiNodeProposal>())
    };

    private static bool Has(string text, params string[] keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string Trim(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
}
