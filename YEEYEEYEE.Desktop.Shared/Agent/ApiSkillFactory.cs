using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using YEEYEEYEE.Core;

namespace YEEYEEYEE.Desktop;

/// <summary>一条将要写入技能目录的技能清单，以及它的来源说明（用于界面展示「这条技能是从哪来的」）。</summary>
public sealed record PlannedApiSkill(
    SkillDefinition Definition,
    string FileName,
    string Kind,
    string SourceNote)
{
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;

    /// <summary>是否为子技能（池子技能）；父技能描述整份文档，子技能描述一个「模型 + 尺寸」组合。</summary>
    public bool IsPool => Definition.Id.Contains("-pool-", StringComparison.Ordinal);
}

/// <summary>根据一份接口文档生成的技能清单。</summary>
public sealed record ApiSkillPlan(
    IReadOnlyList<PlannedApiSkill> Skills,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<PlannedApiSkill> ImageSkills =>
        Skills.Where(skill => skill.Kind == ApiSkillFactory.ImageKind).ToList();

    public IReadOnlyList<PlannedApiSkill> VideoSkills =>
        Skills.Where(skill => skill.Kind == ApiSkillFactory.VideoKind).ToList();

    /// <summary>本清单属于哪个来源命名空间（返工 R5）；空清单时为空串。</summary>
    public string SourceId => Skills.Count == 0 ? string.Empty : Skills[0].Definition.SourceId;

    /// <summary>本清单来源的**完整身份**（返工 V5）：覆盖与清理都要按它逐字核对，不能只看 SourceId。</summary>
    public string SourceIdentity => Skills.Count == 0 ? string.Empty : Skills[0].Definition.SourceIdentity;

    /// <summary>计划产出的技能 Id 集合，用于判断哪些旧文件该清理。</summary>
    public IReadOnlySet<string> Ids => Skills.Select(skill => skill.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool HasAnything => Skills.Count > 0;
}

/// <summary>技能落盘结果：逐个文件报告成功与失败，并列出清理掉的旧文件。</summary>
public sealed record ApiSkillWriteResult(IReadOnlyList<string> Written, IReadOnlyList<string> Errors)
{
    /// <summary>本次清理掉的旧文件（只可能是同一来源上次写出的，或改名前遗留的导入文件）。</summary>
    public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();

    /// <summary>给人看的补充说明（迁移、幂等等）。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool AllWritten => Errors.Count == 0 && Written.Count > 0;
}

/// <summary>
/// 把接口文档的解析结果落成可运行的技能（用户要求：给一个接口说明网页就自动建出 api 生图 / api 生视频技能，
/// 并按文档里的模型与尺寸档位派生池子子技能，例如「生图池1 1K」「生图池2 2K」）。
///
/// 返工 R4/R5 后的边界：
/// · **技能自带执行配置**：来源地址、请求路径、HTTP 方法、鉴权方式都写进步骤（<see cref="SkillEndpoint"/>），
///   运行与最小测试都按它发请求，不再一律去打 <c>/images/generations</c> 加 Bearer；
/// · **不支持的协议在创建前就阻断**：只接受 POST/PUT/PATCH；
///   视频技能**不再是规划态**（第 183 轮接上了出视频执行方：提交 → 轮询 → 下载），
///   没配视频接口时由运行侧如实拒绝——比一律标成「不可执行」准确；
///   只有**归属不明**的池子仍然标成规划态（<see cref="SkillDefinition.IsPlannedOnly"/>），不冒充可用；
/// · **来源命名空间**：技能 Id 与文件名都带来源前缀（<c>api-video-example-image-pool-1-1k.json</c>），
///   A、B 两个来源的同名池子不会互相覆盖；重导同一份文档是幂等覆盖；
/// · **只动自己名下的文件**：清理只删「同一来源上次写过、本次不再产出」的文件，手工技能与其它来源一律不碰；
/// · **失败不留半套**：先全部写临时文件，全部就绪后再替换目标文件，任何一条失败就整体放弃并如实报错。
/// </summary>
public static class ApiSkillFactory
{
    public const string ImageKind = "image";
    public const string VideoKind = "video";

    /// <summary>
    /// 每类最多派生多少个池子技能。文档（如聚合站）常有几十个「模型 × 档位」组合，
    /// 一次全建会把技能目录刷满、选择器里翻不到东西；超出部分如实提示，不静默丢弃。
    /// </summary>
    public const int MaxPoolsPerKind = 12;

    /// <summary>改名前（没有来源前缀）的导入技能 Id 形态，用于一次性认领迁移。</summary>
    private static readonly Regex LegacyImportedId = new(@"^api-(image|video)(-pool-\d+.*)?$", RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // 技能名与提示词里有中文，落盘保留原文便于用户直接改。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>根据解析报告生成技能清单。报告为空时返回空清单并说明原因。</summary>
    public static ApiSkillPlan Build(ApiDocReport report) => Build(report, null);

    /// <summary>
    /// 生成技能清单。<paramref name="liveModels"/> 非空时只保留**接口确实提供**的模型
    /// （文档常比线上新或旧，写了但没上线的模型跑起来一定 404；这个过滤只在用户明确要求时启用）。
    /// </summary>
    public static ApiSkillPlan Build(ApiDocReport report, IReadOnlyList<string>? liveModels)
    {
        ArgumentNullException.ThrowIfNull(report);
        var notes = new List<string>(report.Notes);
        var warnings = new List<string>(report.Warnings);
        var skills = new List<PlannedApiSkill>();
        var sourceId = report.SourceId;

        AddKind(report, sourceId, report.ImageOps, ImageKind, "API 生图", "生图池", "出图", liveModels, skills, notes, warnings);
        AddKind(report, sourceId, report.VideoOps, VideoKind, "API 生视频", "生视频池", "出视频", liveModels, skills, notes, warnings);

        if (skills.Count == 0)
            warnings.Add("没有从这份文档里得到可出图或可出视频的接口，因此没有创建技能：请确认地址指向的是接口说明页（含请求路径），或改贴文档里的接口段落。");

        return new ApiSkillPlan(skills, notes, warnings);
    }

    /// <summary>生成某一类（出图 / 出视频）的技能：一条父技能 + 若干「模型 × 尺寸」池子子技能。</summary>
    private static void AddKind(
        ApiDocReport report,
        string sourceId,
        IReadOnlyList<ApiOpCandidate> ops,
        string kind,
        string parentName,
        string poolPrefix,
        string outputName,
        IReadOnlyList<string>? liveModels,
        List<PlannedApiSkill> skills,
        List<string> notes,
        List<string> warnings)
    {
        var usable = new List<ApiOpCandidate>();
        foreach (var op in ops)
        {
            // 返工 R4：不支持的协议在创建前就阻断，不建一条注定跑不通的技能。
            if (op.Method is "POST" or "PUT" or "PATCH") { usable.Add(op); continue; }
            warnings.Add($"{op.Method} {op.Path} 不是可提交的生成协议（当前只支持 POST / PUT / PATCH），已跳过、未创建技能。");
        }

        if (usable.Count == 0 && !HasModels(report, kind))
        {
            notes.Add($"文档里没有可用的{parentName[4..]}接口，未创建{parentName}技能。");
            return;
        }

        // 视频技能**不再标规划态**：第 183 轮把出视频执行方接上了（提交任务 → 轮询 → 下载），
        // 所以它与出图技能同一条规矩——跑的时候问「这条链配好了没有」，没配就如实拒绝并说明。
        // 仍然标规划态的只剩**归属不明**的那种池子（下面 unattributed）：它连该打哪个路径都不确定。

        var parentId = $"api-{sourceId}-{kind}";
        if (usable.Count > 0)
        {
            var parentSteps = usable
                .Select((op, index) => StepFor(op, $"op{index + 1}", LiveOrEmpty(op.Models.FirstOrDefault() ?? string.Empty, liveModels), string.Empty, outputName, report.BaseUrl))
                .ToList();
            var parent = new SkillDefinition
            {
                Id = parentId,
                Name = parentName,
                Description = DescribeParent(report, usable),
                Version = "1.0.0",
                TargetKind = "Any",
                OutputTarget = "variant",
                Steps = parentSteps,
                SourceId = sourceId,
                SourceUrl = report.SourceUrl,
                SourceIdentity = report.SourceIdentity,
                IsPlannedOnly = false,
                PlannedReason = string.Empty
            };
            skills.Add(new PlannedApiSkill(parent, $"{parentId}.json", kind, SourceOf(usable)));
        }

        var specs = PoolSpecs(report, usable, kind, warnings, liveModels);
        // 池子按「模型 × 档位」展开后再截断：上限约束的是**技能条数**，而不是模型个数，
        // 否则一个支持 4 个档位的模型一条就能顶掉四条额度。
        var combos = new List<(int Pool, PoolSpec Spec, string Size)>();
        for (var index = 0; index < specs.Count; index++)
        {
            var sizes = specs[index].Sizes.Count > 0 ? specs[index].Sizes : new List<string> { string.Empty };
            foreach (var size in sizes) combos.Add((index + 1, specs[index], size));
        }

        if (combos.Count > MaxPoolsPerKind)
        {
            warnings.Add($"{parentName}：文档里有 {combos.Count} 个「模型 × 档位」组合，本次只建前 {MaxPoolsPerKind} 条池子技能（按文档顺序）；"
                + "其余组合可以照同样的方式手建，或直接用父技能配合设置里的默认模型。");
            combos = combos.Take(MaxPoolsPerKind).ToList();
        }

        // 返工 V4：归属按「模型 + **档位** + 接口」一起判（逐条组合判，不再按模型池整体判）——
        // 两条接口都声明同一个模型、各自只支持 1K / 4K 时，只看模型名会把两个池子都绑到第一条接口上。
        var ambiguous = 0;
        foreach (var (poolNumber, spec, size) in combos)
        {
            var label = ApiDocAnalyzer.SizeLabel(size);
            var id = $"{parentId}-pool-{poolNumber}" + (size.Length == 0 ? string.Empty : $"-{Slug(size)}");
            var name = $"{poolPrefix}{poolNumber}" + (label.Length == 0 ? string.Empty : $" {label}");
            // 返工 U4：池子没能绑上产出它的接口时，**必须不可执行**——否则它会回退到设置里的默认执行方
            // （甚至被 ComfyUI 接管），用户以为在跑文档里的接口，实际跑的是别的东西。
            var op = spec.Op ?? ResolvePoolOp(usable, spec, size);
            var poolEndpoint = EndpointFor(report, op);
            var unattributed = poolEndpoint is null;
            if (unattributed) ambiguous++;
            var definition = new SkillDefinition
            {
                Id = id,
                Name = name,
                Description = DescribePool(spec, label),
                Version = "1.0.0",
                TargetKind = "Any",
                OutputTarget = "variant",
                Steps = new List<SkillStep> { StepForCapability(spec.Capability, "pool", spec.Model, size, outputName, poolEndpoint) },
                SourceId = sourceId,
                SourceUrl = report.SourceUrl,
                SourceIdentity = report.SourceIdentity,
                IsPlannedOnly = unattributed,
                PlannedReason = unattributed
                    ? "无法确定这条池子属于文档里的哪条接口（文档有多条同能力接口且没写明模型与档位的归属）：请手工指定接口路径后再执行。"
                    : string.Empty
            };
            skills.Add(new PlannedApiSkill(definition, $"{id}.json", kind, $"模型 {spec.DescribeModel}｜{PoolSizeText(label)}"));
        }

        if (ambiguous > 0)
            warnings.Add($"{parentName}：有 {ambiguous} 个池子无法确定属于哪条接口（文档里有多条同能力接口，模型与档位都对不出唯一一条），"
                + "这些池子**不写死接口路径、也不可执行**（返工 U4/V4：无归属不许回退到默认执行方），"
                + "需要精确绑定请手工指定接口路径后再执行。");

        notes.Add($"{parentName}：{(usable.Count > 0 ? "1 条父技能 + " : string.Empty)}{skills.Count(skill => skill.Kind == kind && skill.IsPool)} 条池子子技能"
            + $"（{specs.Count} 个模型池{(HasModels(report, kind) ? "，来自文档的可用模型表" : string.Empty)}）。");
    }

    private static bool HasModels(ApiDocReport report, string kind) =>
        report.ModelTable.Any(entry => (kind == VideoKind) == entry.IsVideo);

    /// <summary>池子规格：模型名、能力、该模型支持的档位、它的来源说明，以及**产出它的那条接口**。</summary>
    private sealed record PoolSpec(string Model, Capability Capability, List<string> Sizes, string Evidence, ApiOpCandidate? Op = null)
    {
        public string DescribeModel => Model.Length == 0 ? "文档未声明模型" : Model;
    }

    /// <summary>
    /// 出池子规格。**优先用文档的「可用模型」表**——表里每个模型自带类型与档位，最准；
    /// 没有表时才按接口段落分组猜（同一段里的模型归一条接口），这条在真实文档上会串味，所以只当兜底。
    /// </summary>
    private static List<PoolSpec> PoolSpecs(
        ApiDocReport report,
        IReadOnlyList<ApiOpCandidate> ops,
        string kind,
        List<string> warnings,
        IReadOnlyList<string>? liveModels)
    {
        var isVideoKind = kind == VideoKind;
        var fromTable = report.ModelTable
            .Where(entry => entry.IsVideo == isVideoKind)
            .Select(entry => new PoolSpec(entry.Name, entry.Capability, entry.Sizes.ToList(), entry.Evidence))
            .ToList();
        if (fromTable.Count > 0) return ApplyLiveFilter(fromTable, liveModels);

        if (ops.Count == 0) return new List<PoolSpec>();
        warnings.Add("文档里没有「可用模型」表，池子技能按接口段落里的模型名分组，请核对每个池子的模型是否属于该接口。");
        return ApplyLiveFilter(GroupByOpModel(ops), liveModels);
    }

    /// <summary>按接口实际提供的模型收敛池子；没有模型名的池子（文档没写）无法判断，一律保留。</summary>
    private static List<PoolSpec> ApplyLiveFilter(List<PoolSpec> specs, IReadOnlyList<string>? liveModels) =>
        liveModels is null || liveModels.Count == 0
            ? specs
            : specs.Where(spec => spec.Model.Length == 0 || liveModels.Contains(spec.Model, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>模型名不在接口上线的列表里就用空串（退回设置里的默认模型），不把查不到的名字写进技能。</summary>
    private static string LiveOrEmpty(string model, IReadOnlyList<string>? liveModels) =>
        liveModels is null || liveModels.Count == 0
        || model.Length == 0
        || liveModels.Contains(model, StringComparer.OrdinalIgnoreCase)
            ? model
            : string.Empty;

    /// <summary>
    /// 兜底做法：按接口段落里的模型名分组。文档里没写模型名时只有一个「未声明模型」池——
    /// 宁可名字朴素，也不编一个模型名出来。
    /// </summary>
    private static List<PoolSpec> GroupByOpModel(IReadOnlyList<ApiOpCandidate> ops)
    {
        var order = new List<string>();
        var grouped = new Dictionary<string, PoolSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops)
        {
            var models = op.Models.Count > 0 ? op.Models : new List<string> { string.Empty };
            foreach (var model in models)
            {
                // 返工 U4：分组键用「接口身份 + 模型」，不是模型名——同一模型出现在两条接口上时，
                // 按模型名合并会把第二条接口的档位发到第一条接口去。
                var key = op.Method + " " + op.Path + "|" + model;
                if (!grouped.TryGetValue(key, out var spec))
                {
                    spec = new PoolSpec(model, op.Capability, new List<string>(), ApiDocAnalyzer.Summarize(op.Evidence, 160), op);
                    grouped[key] = spec;
                    order.Add(key);
                }

                var sizes = op.Sizes.Count > 0 ? op.Sizes : new List<string> { string.Empty };
                foreach (var size in sizes)
                    if (!spec.Sizes.Contains(size, StringComparer.OrdinalIgnoreCase)) spec.Sizes.Add(size);
            }
        }

        return order.Select(key => grouped[key]).ToList();
    }

    /// <summary>
    /// 判断某个池子属于哪条接口（返工 S4/V4）：先看文档里哪条接口明确挂着这个模型；
    /// 挂着的接口不止一条时，再用**档位**消歧（每条接口自己声明的尺寸集合）；
    /// 仍然判不出来就返回 null，**不猜**——猜错会让这个池子打到别的接口路径上，
    /// 比留空（标成不可执行）更糟。
    /// </summary>
    private static ApiOpCandidate? ResolvePoolOp(IReadOnlyList<ApiOpCandidate> ops, PoolSpec spec, string size)
    {
        if (ops.Count == 0) return null;

        // 1) 文档里明确声明了这个模型的所有接口。
        var byModel = ops
            .Where(op => op.Models.Contains(spec.Model, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (byModel.Count == 1) return byModel[0];

        if (byModel.Count > 1)
        {
            // 返工 V4：两条接口都声明同一个模型（例如都支持 flux-x）、各自只支持 1K / 4K 时，
            // 只按模型名取第一条会把 4K 的池子绑到只支持 1K 的路径上。用**这条池子的档位**再筛一次：
            // 只有「声明了这个模型、并且声明了这个档位」的接口才算候选，唯一才敢绑。
            if (size.Length == 0) return null;
            var bySize = byModel
                .Where(op => op.Sizes.Count > 0 && op.Sizes.Contains(size, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return bySize.Count == 1 ? bySize[0] : null;
        }

        // 2) 文档里没有接口声明这个模型（模型来自公共模型表）：只有同类能力唯一时才敢绑。
        var sameKind = ops
            .Where(op => IsImageCapability(op.Capability) == IsImageCapability(spec.Capability))
            .ToList();
        return sameKind.Count == 1 ? sameKind[0] : null;
    }

    /// <summary>该能力是否属于出图（否则是出视频）。</summary>
    private static bool IsImageCapability(Capability capability) =>
        capability is Capability.TextToImage or Capability.ImageToImage;

    /// <summary>把一条接口变成技能的执行配置（返工 R4）：地址、路径、方法、鉴权都随技能保存。</summary>
    private static SkillEndpoint? EndpointFor(ApiDocReport report, ApiOpCandidate? op)
    {
        if (op is null) return null;
        return new SkillEndpoint
        {
            BaseUrl = report.BaseUrl,
            Path = op.Path,
            Method = string.IsNullOrWhiteSpace(op.Method) ? "POST" : op.Method.ToUpperInvariant(),
            AuthStyle = OpenAiCompatibleImageProvider.NormalizeAuthStyle(op.AuthStyle)
        };
    }

    /// <summary>父技能的一步：直接对应文档里的一条接口。</summary>
    private static SkillStep StepFor(ApiOpCandidate op, string stepId, string model, string size, string outputName, string baseUrl) =>
        StepForCapability(op.Capability, stepId, model, size, outputName, new SkillEndpoint
        {
            BaseUrl = baseUrl,
            Path = op.Path,
            Method = string.IsNullOrWhiteSpace(op.Method) ? "POST" : op.Method.ToUpperInvariant(),
            AuthStyle = OpenAiCompatibleImageProvider.NormalizeAuthStyle(op.AuthStyle)
        });

    private static SkillStep StepForCapability(
        Capability capability,
        string stepId,
        string model,
        string size,
        string outputName,
        SkillEndpoint? endpoint)
    {
        var parsed = ApiDocAnalyzer.ParseSize(size);
        var suffix = (model.Length == 0 ? string.Empty : $" · {model}") + (size.Length == 0 ? string.Empty : $" · {ApiDocAnalyzer.SizeLabel(size)}");
        return new SkillStep
        {
            Id = stepId,
            Name = $"{CapabilityName(capability)}{suffix}",
            Capability = capability.ToString(),
            Model = model,
            Prompt = PromptFor(capability),
            NegativePrompt = NegativePromptFor(capability),
            ReferenceFrom = NeedsReference(capability) ? "variant" : string.Empty,
            Width = parsed?.Width,
            Height = parsed?.Height,
            OutputName = outputName,
            Endpoint = endpoint
        };
    }

    private static string CapabilityName(Capability capability) => capability switch
    {
        Capability.TextToImage => "文生图",
        Capability.ImageToImage => "图生图",
        Capability.TextToVideo => "文生视频",
        Capability.ImageToVideo => "图生视频",
        _ => capability.ToString()
    };

    private static bool NeedsReference(Capability capability) =>
        capability is Capability.ImageToImage or Capability.ImageToVideo;

    /// <summary>提示词模板沿用既有技能占位符约定（{kind} {name} {variant} {core} {description} {layout}）。</summary>
    private static string PromptFor(Capability capability) => capability switch
    {
        Capability.ImageToImage =>
            "以参考图为准，保持主体外形、发型、服装与配色一致，按以下设定重绘：\n{kind}「{name} · {variant}」\n核心设定：{core}\n当前表现：{description}\n{layout}",
        Capability.TextToVideo =>
            "按以下设定生成一段短视频，主体与画面保持连贯：\n{kind}「{name} · {variant}」\n核心设定：{core}\n当前表现：{description}\n{layout}",
        Capability.ImageToVideo =>
            "以参考图作为首帧生成短视频，保持主体外形与服装一致，动作自然连贯：\n{kind}「{name} · {variant}」\n核心设定：{core}\n当前表现：{description}\n{layout}",
        Capability.TextToImage =>
            "{kind}「{name} · {variant}」\n核心设定：{core}\n当前表现：{description}\n{layout}\n按以上设定生成画面。",
        _ =>
            "{kind}「{name} · {variant}」\n核心设定：{core}\n当前表现：{description}\n{layout}"
    };

    private static string NegativePromptFor(Capability capability) => capability switch
    {
        Capability.TextToVideo or Capability.ImageToVideo => "画面抖动，主体扭曲，闪烁，文字水印",
        _ => "多人，裁切，模糊，文字水印，多余肢体"
    };

    private static string DescribeParent(ApiDocReport report, IReadOnlyList<ApiOpCandidate> ops)
    {
        var source = string.IsNullOrWhiteSpace(report.SourceUrl) ? "（未记录来源地址）" : report.SourceUrl;
        var list = string.Join("、", ops.Select(op => $"{op.CapabilityName} {op.Method} {op.Path}"));
        var endpoint = ops.Count == 0 ? string.Empty : $" 执行配置：{ops[0].Method} {ops[0].Path}。";
        return $"由接口文档自动创建：{source}。覆盖 {ops.Count} 条接口：{list}。{endpoint}来源标识 {report.SourceId}。";
    }

    private static string DescribePool(PoolSpec spec, string label)
    {
        var sizeText = label.Length == 0 ? "文档未声明尺寸" : $"尺寸 {label}";
        return $"接口文档的池子技能：模型 {spec.DescribeModel}｜{sizeText}｜{CapabilityName(spec.Capability)}。"
            + (spec.Evidence.Length == 0 ? string.Empty : $"判据：{spec.Evidence}");
    }

    private static string SourceOf(IReadOnlyList<ApiOpCandidate> ops) =>
        string.Join("、", ops.Select(op => $"{op.Method} {op.Path}"));

    private static string PoolSizeText(string label) => label.Length == 0 ? "未声明尺寸" : $"尺寸 {label}";

    /// <summary>尺寸文本转成安全的文件名片段，例如 1024x1024 或 720p。</summary>
    private static string Slug(string size)
    {
        var trimmed = size.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed) builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return builder.ToString().Trim('-');
    }

    /// <summary>
    /// 把技能清单写成技能目录下的 JSON 文件（返工 R5）：
    /// 先全部写临时文件，全部就绪后再替换目标文件；随后清理本来源上次写过、本次不再产出的文件，
    /// 并认领改名前遗留的导入文件。手工技能与其它来源的文件一律不动。
    /// </summary>
    public static ApiSkillWriteResult Write(ApiSkillPlan plan, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var target = string.IsNullOrWhiteSpace(directory) ? SkillLibrary.Directory : Path.GetFullPath(directory);
        var written = new List<string>();
        var errors = new List<string>();
        var removed = new List<string>();
        var notes = new List<string>();
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add($"技能目录不可写（{target}）：{error.Message}");
            return new ApiSkillWriteResult(written, errors);
        }

        // 返工 S5/V5：替换前先校验所有权。同名文件若不是本来源写出的（用户手工技能、别的来源），
        // 一律不碰并整体放弃本次写入——宁可这次少写，也不能覆盖不是自己的文件。
        // 返工 V5：「来源标识缺失」不再放行——缺 SourceIdentity 就证明不了归属（旧版本或手工文件），
        // 覆盖它就是把来路不明的东西换掉，因此按「不属于本来源」处理。
        var foreign = new List<string>();
        foreach (var planned in plan.Skills)
        {
            var final = Path.Combine(target, planned.FileName);
            if (!File.Exists(final)) continue;
            var (ownerId, ownerIdentity) = OwnershipInFile(final);
            if (OwnedBy(ownerId, ownerIdentity, planned.Definition.SourceId, planned.Definition.SourceIdentity)) continue;
            foreign.Add($"{planned.FileName}：同名文件{OwnershipDescription(ownerId, ownerIdentity)}，已跳过以免覆盖");
        }

        if (foreign.Count > 0)
        {
            errors.AddRange(foreign);
            errors.Add("本次没有任何文件被替换：疑似不属于本来源的文件需要你手工确认后再处理。");
            return new ApiSkillWriteResult(written, errors);
        }

        // 第一段：全部写到临时文件。任何一条失败就整体放弃，不留半套技能。
        var staged = new List<(string Temp, string Final)>();
        foreach (var planned in plan.Skills)
        {
            var final = Path.Combine(target, planned.FileName);
            var temp = final + ".tmp";
            try
            {
                // 更新清单：记下本技能自己写出的文件，之后清理只动这份清单里的东西。
                planned.Definition.OwnedFiles.Clear();
                planned.Definition.OwnedFiles.Add(planned.FileName);
                File.WriteAllText(temp, Serialize(planned.Definition));
                staged.Add((temp, final));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{planned.FileName}：{error.Message}");
            }
        }

        if (errors.Count > 0)
        {
            foreach (var (temp, _) in staged) TryDelete(temp);
            errors.Add("本次没有任何文件被替换：技能目录保持原样。");
            return new ApiSkillWriteResult(written, errors);
        }

        // 第二段：逐个替换，**每个目标先备份再替换**（返工 U6）。
        // 旧实现直接 Move(overwrite) 逐个换，后面的失败会把前面已换的留在新版本上，
        // 于是出现「父技能新版、池子旧版」这种混合批次；现在任一失败即整批回滚。
        var replaced = new List<(string Final, string Backup)>();
        foreach (var (temp, final) in staged)
        {
            string? backup = null;
            try
            {
                if (File.Exists(final))
                {
                    backup = final + ".bak";
                    TryDelete(backup);
                    File.Move(final, backup);
                }

                File.Move(temp, final, overwrite: true);
                replaced.Add((final, backup ?? string.Empty));
                written.Add(final);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                errors.Add($"{Path.GetFileName(final)}：{error.Message}");
                TryDelete(temp);
                // 把这个文件自己的备份放回去，再回滚已经替换成功的那些，保证不留混合批次。
                // 返工 U6 边界：恢复动作本身也可能失败——结果必须检查并如实报告，
                // 不能一边留下新文件、一边声称「已整批回滚」。
                if (backup is not null && !File.Exists(final) && File.Exists(backup) && !TryMove(backup, final))
                    errors.Add($"{Path.GetFileName(final)} 的备份未能放回（{Path.GetFileName(backup)} 还在）：该文件可能仍是新版本。");

                errors.AddRange(RollbackReplaced(replaced));
                // 返工 U6 边界：剩下的临时文件统一清理（原先只删失败那一条），不留 .tmp 垃圾。
                foreach (var (rest, _) in staged) TryDelete(rest);
                return new ApiSkillWriteResult(Array.Empty<string>(), errors) { Removed = removed, Notes = notes };
            }
        }

        // 全部替换成功：清理备份（备份只是失败时的退路）。
        foreach (var (_, backup) in replaced)
            if (backup.Length > 0) TryDelete(backup);

        // 第三段：清理。只删「同一来源上次写过、本次不再产出」的文件；顺手认领改名前遗留的导入文件。
        if (errors.Count == 0)
        {
            var (existing, _) = SkillLibrary.Load();
            var plannedIds = plan.Ids;
            foreach (var skill in existing)
            {
                // 返工 V5：清理也按**完整身份**核对（原先只看 SourceId）。旧池子保留 SourceId、
                // 只把 SourceIdentity 换成别的来源时，按 SourceId 判就会把它当自己的删掉。
                var sameSource = skill.IsImported
                    && OwnedBy(skill.SourceId, skill.SourceIdentity, plan.SourceId, plan.SourceIdentity);
                if (sameSource)
                {
                    if (plannedIds.Contains(skill.Id)) continue;
                    foreach (var file in OwnedFilesOf(skill))
                    {
                        var full = Path.Combine(target, file);
                        // 删除前再读一次文件里的身份：清单里的文件名不是归属证明，
                        // 身份对不上就保守保留（宁可留一个多余文件，也不删错东西）。
                        if (File.Exists(full))
                        {
                            var (ownerId, ownerIdentity) = OwnershipInFile(full);
                            if (!OwnedBy(ownerId, ownerIdentity, plan.SourceId, plan.SourceIdentity))
                            {
                                notes.Add($"旧文件 {file} 的来源身份与本次导入对不上：已保守保留，未删除。");
                                continue;
                            }
                        }

                        if (TryDelete(full)) removed.Add(file);
                    }

                    continue;
                }

                // 命名空间一样、身份对不上：这属于别的来源（或身份被改过），一律不碰。
                if (skill.IsImported && skill.SourceId.Length > 0
                    && string.Equals(skill.SourceId, plan.SourceId, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"发现命名空间相同但来源身份对不上的技能 {skill.Id}.json：已保守保留，未删除。");
                    continue;
                }

                // 返工 S5：没有来源标识的疑似旧版本文件**保守保留**——文件里证明不了归属，
                // 删错就是把用户或旧版本的东西弄丢，只在说明里列出让人自己决定。
                if (LegacyImportedId.IsMatch(skill.Id))
                    notes.Add($"发现疑似旧版本的导入文件 {skill.Id}.json（没有来源标识，无法证明归属）：已保守保留，如需清理请手动删除。");
            }
        }

        return new ApiSkillWriteResult(written, errors) { Removed = removed, Notes = notes };
    }

    /// <summary>
    /// 读技能文件里的来源标识；读不出来返回空串（返工 S5/U5 的所有权判据）。
    /// 与本次要写的技能**逐字**比对 SourceId 与 SourceIdentity：只有两者都证明属于同一来源，
    /// 才允许替换这个文件。
    /// </summary>
    private static (string SourceId, string Identity) OwnershipInFile(string path)
    {
        try
        {
            var skill = JsonSerializer.Deserialize<SkillDefinition>(File.ReadAllText(path), Options);
            return skill is null ? (string.Empty, string.Empty) : (skill.SourceId, skill.SourceIdentity);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// 文件是否确实属于本来源（返工 V5）：**完整身份必须逐字对上**——
    /// SourceId 与 SourceIdentity **都**要有值且相等。缺任何一项都证明不了归属：
    /// 只看 SourceId 时，把 SourceIdentity 改成另一来源的旧池子仍会被当成自己的删掉。
    /// </summary>
    private static bool OwnedBy(string ownerId, string ownerIdentity, string sourceId, string sourceIdentity) =>
        ownerId.Length > 0
        && ownerIdentity.Length > 0
        && sourceId.Length > 0
        && sourceIdentity.Length > 0
        && string.Equals(ownerId, sourceId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ownerIdentity, sourceIdentity, StringComparison.OrdinalIgnoreCase);

    /// <summary>把「为什么不算自己的」写清楚，便于用户判断该不该手工处理。</summary>
    private static string OwnershipDescription(string ownerId, string ownerIdentity)
    {
        if (ownerId.Length == 0) return "没有来源标识（像是手工技能或旧版本文件）";
        if (ownerIdentity.Length == 0) return $"只有来源命名空间 {ownerId}、没有完整来源身份（无法证明归属）";
        return $"属于来源 {ownerId}（身份 {ownerIdentity}）";
    }

    /// <summary>技能自己写出的文件清单；老技能没有这份清单时按命名约定推断。</summary>
    private static IReadOnlyList<string> OwnedFilesOf(SkillDefinition skill) =>
        skill.OwnedFiles.Count > 0 ? skill.OwnedFiles : new List<string> { skill.Id + ".json" };

    /// <summary>
    /// 回滚已经替换过的目标：把新文件删掉、把备份放回去（返工 U6）。
    /// 返回未能回滚的说明；调用方把它拼进错误信息里，让用户知道技能目录可能停在中途状态。
    /// </summary>
    private static IReadOnlyList<string> RollbackReplaced(IReadOnlyList<(string Final, string Backup)> replaced)
    {
        var failures = new List<string>();
        for (var index = replaced.Count - 1; index >= 0; index--)
        {
            var (final, backup) = replaced[index];
            try
            {
                if (backup.Length == 0)
                {
                    if (File.Exists(final)) File.Delete(final);   // 原本不存在：删掉新写入的
                    continue;
                }

                if (File.Exists(final)) File.Delete(final);
                File.Move(backup, final);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Add($"{Path.GetFileName(final)} 未能回滚：{error.Message}");
            }
        }

        failures.Add(failures.Count == 0
            ? "本次替换已整批回滚：技能目录保持替换前的状态。"
            : "回滚没有完全成功：技能目录可能停在中途状态，请检查上面列出的文件。");
        return failures;
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>序列化为技能目录使用的 JSON 形态（camelCase + 缩进，与内置示例技能一致）。</summary>
    public static string Serialize(SkillDefinition definition) =>
        JsonSerializer.Serialize(definition, Options);
}

public enum ApiMinimalTestKind { Unsupported, Image, Video }

/// <summary>最小测试的判定结果：能不能测、测哪条接口、用哪个模型与哪套执行配置、为什么。</summary>
public sealed record ApiMinimalTestPlan(
    ApiMinimalTestKind Kind,
    Capability Capability,
    ApiOpCandidate? Op,
    string Model,
    SkillEndpoint? Endpoint,
    string Reason,
    string Warning)
{
    public bool CanRun => Kind != ApiMinimalTestKind.Unsupported;
}

/// <summary>
/// 挑一条接口做最小测试（用户要求：问「是否最小测试」，是就测试并返图，否就保存完成）。
///
/// 挑选原则：优先「不需要参考图」的接口——文生图最省事，其次是文生视频；
/// 只有图生图 / 图生视频的文档没法凭空测（必须给一张参考图），此时如实说明而不是硬跑一次注定失败的调用。
/// 模型名优先用接口自己声明的，没有就取「可用模型」表里同类模型的第一个（文档里的真实名字）。
/// **执行配置与正式运行共用一份**（返工 R4）：最小测试打的就是这条接口记下来的地址、路径、方法与鉴权，
/// 不偷偷改打固定的 <c>/images/generations</c>，否则它证明不了导入的接口可用。
/// </summary>
public static class ApiMinimalTest
{
    public static ApiMinimalTestPlan Plan(ApiDocReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (FirstOf(report.Ops, Capability.TextToImage) is { } textToImage)
            return Plan(report, ApiMinimalTestKind.Image, textToImage);

        if (FirstOf(report.Ops, Capability.TextToVideo) is { } textToVideo)
            return Plan(report, ApiMinimalTestKind.Video, textToVideo);

        if (FirstOf(report.Ops, Capability.ImageToImage) is { } imageToImage)
            return new ApiMinimalTestPlan(
                ApiMinimalTestKind.Unsupported,
                imageToImage.Capability,
                imageToImage,
                string.Empty,
                null,
                "文档里只有图生图接口：最小测试需要一张参考图，请在画布节点上运行技能。",
                string.Empty);

        if (FirstOf(report.Ops, Capability.ImageToVideo) is { } imageToVideo)
            return new ApiMinimalTestPlan(
                ApiMinimalTestKind.Unsupported,
                imageToVideo.Capability,
                imageToVideo,
                string.Empty,
                null,
                "文档里只有图生视频接口：最小测试需要一张参考图，请在画布节点上运行技能。",
                string.Empty);

        return new ApiMinimalTestPlan(
            ApiMinimalTestKind.Unsupported,
            Capability.Unknown,
            null,
            string.Empty,
            null,
            "文档里没有可用于最小测试的接口，技能已保存，可直接在画布上运行。",
            string.Empty);
    }

    private static ApiMinimalTestPlan Plan(ApiDocReport report, ApiMinimalTestKind kind, ApiOpCandidate op) => new(
        kind,
        op.Capability,
        op,
        ModelFor(report, op, kind),
        new SkillEndpoint
        {
            BaseUrl = report.BaseUrl,
            Path = op.Path,
            Method = string.IsNullOrWhiteSpace(op.Method) ? "POST" : op.Method.ToUpperInvariant(),
            AuthStyle = OpenAiCompatibleImageProvider.NormalizeAuthStyle(op.AuthStyle)
        },
        $"最小测试使用 {op.CapabilityName} 接口：{op.Method} {op.Path}"
            + (op.Models.Count == 0 ? "（模型名取自文档的可用模型表）" : "。"),
        op.IsAsync
            ? "该接口在文档里被描述为异步任务（需要轮询取结果），最小测试可能不成功；失败原因会如实报出。"
            : string.Empty);

    /// <summary>最小测试用的模型名：先用接口自己声明的，没有就用模型表里同类模型的第一个。</summary>
    private static string ModelFor(ApiDocReport report, ApiOpCandidate op, ApiMinimalTestKind kind)
    {
        if (op.Models.FirstOrDefault() is { Length: > 0 } declared) return declared;
        var isVideo = kind == ApiMinimalTestKind.Video;
        return report.ModelTable.FirstOrDefault(entry => entry.IsVideo == isVideo)?.Name ?? string.Empty;
    }

    /// <summary>
    /// 最小测试用的画幅：文档里声明的最小那一档，省额度；文档没写尺寸就按 1024 正方形。
    /// 放在共享层是因为两端都要「挑最小的一档」，各写一份迟早会一份挑最小、一份挑默认。
    /// </summary>
    public static (int Width, int Height) SmallestSize(ApiOpCandidate op)
    {
        ArgumentNullException.ThrowIfNull(op);
        var sizes = op.Sizes
            .Select(ApiDocAnalyzer.ParseSize)
            .Where(size => size is not null)
            .Select(size => size!.Value)
            .OrderBy(size => (long)size.Width * size.Height)
            .ToList();
        return sizes.Count > 0 ? sizes[0] : (1024, 1024);
    }

    private static ApiOpCandidate? FirstOf(IReadOnlyList<ApiOpCandidate> ops, Capability capability) =>
        ops.FirstOrDefault(op => op.Capability == capability);
}

/// <summary>
/// 把「解析报告 + 将要创建的技能清单」渲染成给人核对的文本。
///
/// 放在共享层是刻意的：这段文本是用户按下「创建技能」之前唯一能核对的依据，
/// WinForms 端与 Avalonia 端各写一份的话，同一份文档在两边会显示不同的结论——
/// 那时「到底该信哪个」就成了没人能回答的问题。
/// </summary>
public static class ApiImportSummary
{
    /// <summary>
    /// 渲染核对报告。
    /// <paramref name="plan"/> 为 null 时不渲染「将要创建的技能」那一段——站点模式下技能不是产物，
    /// 产物是站点本身（见 <paramref name="site"/>）。旧端照旧传 plan，输出一字不变。
    /// </summary>
    public static string RenderReport(ApiDocReport report, ApiSkillPlan? plan, SiteProfile? site = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            $"识别形态：{FormatName(report.Format)}",
            $"来源地址：{(report.SourceUrl.Length == 0 ? "（未记录）" : report.SourceUrl)}",
            $"基础地址：{(report.BaseUrl.Length == 0 ? "（未解析到）" : report.BaseUrl)}"
        };
        if (report.Title.Length > 0) lines.Add($"文档标题：{report.Title}");

        lines.Add(string.Empty);
        lines.Add($"—— 解析到的接口（{report.Ops.Count} 条）——");
        if (report.Ops.Count == 0) lines.Add("（没有解析出可用的出图或出视频接口）");
        foreach (var op in report.Ops)
        {
            var model = op.Models.Count == 0 ? "模型未声明" : $"模型 {string.Join("、", op.Models)}";
            var size = op.Sizes.Count == 0 ? "尺寸未声明" : $"尺寸 {string.Join("、", op.Sizes.Select(ApiDocAnalyzer.SizeLabel))}";
            var async = op.IsAsync ? "｜异步任务" : string.Empty;
            lines.Add($"· [{op.CapabilityName}] {op.Method} {op.Path}｜{model}｜{size}{async}");
        }

        if (report.ModelTable.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"—— 文档的可用模型表（{report.ModelTable.Count} 个：图像 {report.ImageModels.Count}、视频 {report.VideoModels.Count}）——");
            foreach (var entry in report.ModelTable.Take(16))
            {
                var sizes = entry.Sizes.Count == 0 ? "尺寸未声明" : string.Join("、", entry.Sizes.Select(ApiDocAnalyzer.SizeLabel));
                lines.Add($"· [{entry.CapabilityName}] {entry.Name}｜{sizes}");
            }
            if (report.ModelTable.Count > 16) lines.Add($"· …（还有 {report.ModelTable.Count - 16} 个模型）");
        }

        if (plan is not null)
        {
            lines.Add(string.Empty);
            lines.Add($"—— 将要创建的技能（{plan.Skills.Count} 条）——");
            if (plan.Skills.Count == 0) lines.Add("（没有可创建的技能）");
            foreach (var skill in plan.Skills)
                lines.Add($"· {skill.Name}（{skill.FileName}）｜步骤 {skill.Definition.Steps.Count}｜{skill.SourceNote}");

            if (plan.Notes.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("—— 说明 ——");
                lines.AddRange(plan.Notes.Select(note => "· " + note));
            }

            if (plan.Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("—— 需要你确认 ——");
                lines.AddRange(plan.Warnings.Select(warning => "· " + warning));
            }
        }

        if (site is not null)
        {
            lines.Add(string.Empty);
            lines.Add($"—— 将要登记的站点「{site.Label}」——");
            lines.Add($"· 基础地址：{(site.BaseUrl.Length == 0 ? "（未解析到）" : site.BaseUrl)}");
            if (site.ImagePath.Length > 0) lines.Add($"· 文生图：{site.Method} {site.ImagePath}");
            if (site.ImageEditPath.Length > 0) lines.Add($"· 图生图：{site.Method} {site.ImageEditPath}");
            if (site.VideoPath.Length > 0) lines.Add($"· 出视频：{site.Method} {site.VideoPath}");
            lines.Add($"· 池子：生图 {site.ImagePools.Count} 个（{site.ImagePools.Select(pool => pool.Model).Distinct().Count()} 个模型）、"
                + $"视频 {site.VideoPools.Count} 个（{site.VideoPools.Select(pool => pool.Model).Distinct().Count()} 个模型）");
            lines.Add(site.ListSource.Length > 0
                ? $"· 池子清单来自：{site.ListSource}"
                : "· 池子清单来自：文档里的「可用模型」表（没探到站点自己的清单接口）");
            lines.Add("· 调用时会在这些池子里让你先选一个（先选模型、再选档位），不会自己挑一个跑掉。");
        }

        if (report.PendingItems.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"—— 待确认（{report.PendingItems.Count} 条，不会建进技能）——");
            lines.AddRange(report.PendingItems.Select(item => "· " + item));
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatName(ApiDocFormat format) => format switch
    {
        ApiDocFormat.OpenApiJson => "OpenAPI / Swagger 结构化描述",
        ApiDocFormat.Html => "HTML 页面（已剥离标签）",
        _ => "纯文本"
    };
}
