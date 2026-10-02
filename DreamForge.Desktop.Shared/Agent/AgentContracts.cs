// 本文件由 DreamForge.Desktop/AgentPane.cs 抽出：这些类型**不依赖任何界面技术**，
// 旧 WinForms 面板与 Avalonia 工作台共用同一份源码（Avalonia 端通过 Compile Include 链接）。
// 改动这里等于同时改两端，不要再在 AgentPane.cs 里重复定义。

namespace DreamForge.Desktop;

/// <summary>Agent 的授权模式（权限预设）。注意最后一道关（保存画布）始终在用户手里。</summary>
public enum AgentAuthMode
{
    /// <summary>每次都要人工勾选批准。</summary>
    Ask,

    /// <summary>Agent 直接把动作应用到当前画布，并自动保存。</summary>
    AutoStage,

    /// <summary>只提问，不许提改动。</summary>
    ReadOnly
}

/// <summary>Agent 面板的上下文快照：由主窗体提供，用来给模型交代当前在做什么。</summary>
public sealed record AgentContext(
    string NodeTitle,
    string NodeContent,
    string CanvasSummary,
    string LibrarySummary,
    string WorkspaceSummary,
    string PendingSummary,
    string WorkTreeSummary = "")
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(NodeTitle) && string.IsNullOrWhiteSpace(NodeContent)
        && string.IsNullOrWhiteSpace(CanvasSummary) && string.IsNullOrWhiteSpace(LibrarySummary);

    /// <summary>
    /// 上下文说明 + 操作协议。模型据此决定是否需要提议改动。
    /// <paramref name="maxCharacters"/> 是模型上下文窗口换算出的大致字符预算（0 表示未声明、不截断）：
    /// 只截画布上下文，**操作协议永远保留**——协议被截掉比上下文不全严重得多。
    /// </summary>
    public string Describe(int maxCharacters = 0)
    {
        var lines = new List<string>
        {
            "你在 YeeYeeYee 的画布助手里：既要和用户讨论创作，也要把**改动**作为提议交出来（协议见文末）。" +
            "用户要求改画布时，只写正文或只描述方案都不算完成——必须给出可批准的改动块。",
            "以下是当前的创作上下文，回答时请结合它："
        };
        if (!string.IsNullOrWhiteSpace(NodeTitle)) lines.Add($"- 选中节点：{NodeTitle}");
        if (!string.IsNullOrWhiteSpace(NodeContent)) lines.Add($"- 选中节点内容：\n{NodeContent}");
        if (!string.IsNullOrWhiteSpace(CanvasSummary)) lines.Add($"- 画布节点（target 可用短 id 或标题）：\n{CanvasSummary}");
        if (!string.IsNullOrWhiteSpace(LibrarySummary)) lines.Add($"- 资源视觉内容：{LibrarySummary}");
        if (!string.IsNullOrWhiteSpace(WorkTreeSummary)) lines.Add($"- 工作树（**叙事轴**：人物/能力/道具及剧情版本与来源章节）：\n{WorkTreeSummary}");
        if (!string.IsNullOrWhiteSpace(WorkspaceSummary)) lines.Add($"- 工作文件夹：{WorkspaceSummary}");
        lines.Add("- **剧情进入后的固定顺序：先树后节点，分两批提议**：\n" +
            "  1) 确认剧情：先把章节切分、出场人物、能力、关键道具、场景整理成一份清单，**这一步不要改画布、也不要改工作树**，用正文或 ask 请用户确认；用户没点头不进入第 2 步。\n" +
            "  2) 建工作树（叙事轴事实源）：项目根下按章节建 workTreeKind=Chapter（title=第10章）；角色用 workTreeKind=Character 挂在所属章节下；角色的能力用 workTreeKind=Ability 挂在角色下；角色道具用 workTreeKind=Prop 挂在角色下；能力的剧情版本用 workTreeKind=Version 挂在能力下，填 chapter 与 version。不得覆盖旧版本，只能新增版本条目。角色、能力、道具工作树项只要对应资源视觉内容实体，就必须在动作里填写 entityTarget；能力和道具必须用 parentTarget 指向角色。\n" +
            "  3) 建视觉骨架：为第 2 步的角色/场景/道具建或更新资源视觉内容实体（create_entity / update_entity），只写外观、服装、形态、空间布局；剧情里没有的信息先留空，不要编。完成实体后再用 entityTarget 回填对应工作树项，宿主会自动把能力和道具挂到角色引用层。\n" +
            "  4) 建节点（动作层，**只建主线**）：必须先建一个「剧情概括」首节点（nodeCategory=剧情概括，parentTarget 留空，content 写整段故事主线），再按章节/剧情/分镜/成品各建节点，用 parentTarget 挂到上游节点，并**用 workTreeTarget 指向第 2 步建好的章节或能力条目**；content 只写「这一章/这一镜发生了什么」。**角色/场景/道具不建节点**，见下条。\n" +
            "  分批规则：**只有「按剧情搭项目结构」这类从零开始的请求**才按「先树后节点」分两次提议（先让用户在工作树里看清结构）。**用户明确说「生成节点 / 自动生成节点 / 铺开节点 / 建画布节点」时，不要停在树那一步——本次回复必须包含 create_node 批次**（需要的话把对应的 create_work_item 放进同一批一起给）；否则画布上一个节点都不会有，等于没做。同步方向是单向的：工作树变了才更新节点，节点的内容与附件不回写工作树。" +
            "\n- **两套载体不要互相抄**：工作树是叙事轴（这个人物/能力/道具在剧情里是什么、第几章有什么变化），只写剧情向内容；外观、服装、形态、空间布局与参考图属于视觉轴，一律写进资源视觉内容（create_entity / update_entity），不要写在工作树里。能力的素材（特效或形态参考图、演示视频）算叙事侧资料，可以挂在工作树条目上。" +
            "\n- **角色/场景/道具不进画布**：它们只存在于资源视觉内容（create_entity / update_entity），**不要为它们建 create_node**。用到它们的剧情/分镜节点用 entityTargets 引用，例如 \"entityTargets\":[\"林晚\",\"旧录音棚\",\"旧磁带\"]；画布上该卡片会显示 ◆ 引用标签与参考图缩略图，这就替代了原来的资源节点。只有需要锁定某个变体或版本时才改用单条的 entityTarget / variantTarget / variantVersion。不要把外观设定抄进节点 content。");
        lines.Add(
            "- **写实体内容（create_entity / update_entity 的 content）必须按固定小标题分段写全**："
            + "这是**权重顺序**——靠前的词模型权重更高，缺段或顺序乱了，后续每个镜头都要重新猜这个角色长什么样。"
            + "属性一律写具体值，不要写「正常」「普通」（等于没写）；同一件衣服全卡只能有一种措辞，"
            + "前视写「藏青外套」、后视就不能写「深蓝夹克」，模型会当成两件衣服。\n"
            + $"  · 角色：{PromptBaseline.SectionList(NodeCategory.Character)}\n"
            + $"  · 场景：{PromptBaseline.SectionList(NodeCategory.Scene)}\n"
            + $"  · 道具：{PromptBaseline.SectionList(NodeCategory.Prop)}\n"
            + "  正文写完后**另起一段**写「出图提示词」与「负面提示词」："
            + $"角色负面参考：{PromptBaseline.NegativeFor(NodeCategory.Character)}；"
            + $"场景负面参考：{PromptBaseline.NegativeFor(NodeCategory.Scene)}。"
            + "（不支持的模型请把负面词改写成正向描述）");
        if (!string.IsNullOrWhiteSpace(PendingSummary))
            lines.Add($"- **Agent 最近一批改动（已经显示在画布上，尚未保存到画布文件）**：\n{PendingSummary}\n这些改动已经作为当前最新版本提供给你；后续创建节点时可以用它们的标题作为 parentTarget、source 或 target，不要重复创建同名节点。");
        lines.Add("- **可调用的生成技能清单**（本程序提供的生成流程，与工作树里的「角色能力」是两回事）：\n" + BuiltInSkills.DescribeForAgent());
        lines.Add("- 调用**生成技能**时先说明：正在调用技能：技能名称。节点操作必须使用 actions；章节拆解按上面四步执行（默认先工作树条目再节点；用户明确要求生成节点时，节点批次必须在同一次回复里给出），节点批次内部按 章节→剧情→分镜→成品 的顺序输出（**角色/场景/道具不建节点，只写进资源视觉内容并在节点上用 entityTargets 引用**），每个节点写入 nodeCategory、完整 content、parentTarget、workTreeTarget，用到角色/场景/道具时写 entityTargets，并用 create_edge 建立关系；图片和视频必须说明参考图、画幅、时长与当前模型能力。");

        var head = string.Join("\n", lines);
        if (maxCharacters > 0 && head.Length > maxCharacters)
        {
            var omitted = head.Length - maxCharacters;
            head = head[..maxCharacters] + $"\n（以上内容按模型上下文窗口截断，省略了约 {omitted} 个字符；需要细节时请问我）";
        }

        return string.Join("\n", new[] { head, string.Empty, ActionProtocol });
    }

    /// <summary>
    /// 操作协议：模型在回复末尾给出结构化动作，宿主先把动作应用到画布；
    /// 自动模式直接保存，审批模式由用户在审批列表中选择保存或撤销。
    /// </summary>
    public const string ActionProtocol = """
    如果你需要改动画布或写入文件，**不要声称已经完成**，而是在回复的最后附上一个 JSON 代码块：
    ```json
    {"actions":[
      {"kind":"create_node","title":"剧情概括 / 章节 / 分镜 / 成品标题","nodeCategory":"剧情概括|章节|通用|分镜|成品","content":"节点内容","parentTarget":"父节点短id或标题；剧情概括留空作为画布首节点","workTreeTarget":"要关联的工作树章节/能力条目名或短id","entityTargets":["这一镜出现的角色/场景/道具设定名，可多个"],"entityTarget":"单条引用（要指定变体或版本时才用）","variantTarget":"变体名或短id","variantVersion":"v2或版本短id，省略表示跟随当前","reason":"为什么建这个"},
      {"kind":"update_node","target":"节点短id或标题","title":"可选新标题","content":"新的内容","workTreeTarget":"可选：改挂到哪个工作树章节/能力条目","entityTargets":["可选：替换成这组引用"],"reason":"为什么改"},
      {"kind":"delete_node","target":"节点短id或标题","reason":"为什么删"},
      {"kind":"create_edge","source":"起点节点短id或标题","target":"终点节点短id或标题","reason":"为什么要连"},
      {"kind":"delete_edge","source":"起点节点短id或标题","target":"终点节点短id或标题","reason":"为什么断开"},
      {"kind":"create_entity","entityKind":"角色","title":"实体名","content":"核心设定","reason":"为什么建"},
      {"kind":"update_entity","target":"实体名","content":"新的核心设定","reason":"为什么改"},
      {"kind":"delete_entity","target":"实体名","reason":"为什么删"},
      {"kind":"create_work_item","title":"角色名、能力或道具名称","workTreeKind":"Chapter|Character|Ability|Prop|Scene|Version|Project（Ability=角色能力，不是生成技能）","parentTarget":"父工作树条目名或短id，可空；能力/道具必须指向角色","entityTarget":"对应资源视觉内容实体名或短id，可空；角色/能力/道具有视觉实体时必填","variantTarget":"可选变体名或短id","variantVersion":"可选版本","chapter":"第10章","version":"0.2","content":"叙事设定（剧情向；外观与参考图请写进资源视觉内容）","reason":"为什么新增"},
      {"kind":"create_entity_version","entityTarget":"实体名或短id","variantTarget":"变体名或短id","chapter":"第3章","content":"新版本设定内容","versionNote":"本章升级说明","reason":"为什么创建新版本"},
      {"kind":"update_work_item","target":"工作树条目名或短id","title":"可选新名称","workTreeKind":"可选类型（Character|Ability|Prop|Scene|Version）","chapter":"可选来源章节","version":"可选版本","content":"可选新叙事设定（外观请写进资源视觉内容）","entityTarget":"可选实体名或短id","variantTarget":"可选变体名或短id","variantVersion":"可选版本","reason":"为什么更新"},
      {"kind":"delete_work_item","target":"工作树条目名或短id","reason":"为什么删除"},
      {"kind":"write_file","path":"相对工作文件夹的路径.md","content":"文件内容","reason":"为什么写"}
    ]}
    ```
    **JSON 必须合法**：content / title 这类字符串里如果要用引号，请用中文引号「」或转义成 \\"，
    不要直接写英文双引号——那会让整块 JSON 失效，界面解析不了，你的改动就全部作废（用户会说"没加上"）。
    换行请写成 \\n。
    **信息不足时就问，不要猜**：不知道加到哪个画布、节点类型、名称或内容时，
    不要凭空替用户决定，也不要把问题写成一大段正文让用户自己去回——用 ask 块，
    界面会弹窗让用户选择（弹窗里始终带一个"其他（在下面自己写）"）。
    ```json
    {"ask":{"question":"要加哪种节点？","options":["剧情节点","分镜节点","成品节点"]}}
    ```
    这条是硬要求：用户说「新建一个节点」这类**没给全信息**的请求时，
    只回一句文字问句（例如"你想建什么节点？"）**等于没做**——用户看不到弹窗、也没有可选项。
    必须输出上面的 ask 块，正文里不必再重复问题，界面会弹窗。
    options 可以留空数组（纯自由输入）；ask 可以和 actions 同时出现（例如先问清再改），
    但**没问清之前不要猜着改**。
    你的改动会先应用到当前画布。自动模式会直接保存；审批模式会在画布中显示改动，并在审批列表中等待用户选择保存或撤销。
    所以不要虚构执行结果；只说明动作内容，宿主会负责执行和展示审批状态。
    流程规则：默认**先建工作树、再建节点**，分两次提议；但**用户明确要求生成 / 自动生成 / 铺开画布节点时，
    必须在本次回复里就给出 create_node**，不能只给工作树条目。节点用 workTreeTarget 指向
    对应的章节或能力条目，这样它才会跟着项目树更新。
    连线规则：source 是上游、target 是下游，方向不能反；一次提议里**新建的节点**可以在后续
    动作里用它的标题作为 source/target（按顺序执行，前面建好的节点后面就能连上）。
    **重要**：用户要求改动时，不要用文字描述改动方案来代替这个块——只写文字等于什么都没做，
    用户看不到任何可批准的改动。必须输出上面的 JSON 块。
    例如用户说「给第一章加个分镜节点」：你应当输出一个 create_node 提议，
    并把分镜内容写进 content；如果用户选中了分镜节点并要求补上里面的场景，必须读取选中节点内容，
    把由分镜推导出的时间、地点、人物、动作和镜头连续性写进**资源视觉内容实体**（create_entity / update_entity），
    再在那个分镜节点的 entityTargets 里引用它——**不要为场景/角色/道具新建画布节点**。
    不要只在回复正文里描述这些内容。
    规则：只在确实需要改动时才输出这个块；没有改动就不要输出；自然语言的说明写在块之前。
    """;
}

/// <summary>
/// 自动模式落地结果（返工 S1）：动作清单 + 失败说明。
/// <see cref="Failure"/> 为 null 才代表**真的已应用并保存**；有值时必须如实显示，不能报成功。
/// </summary>
public sealed record AgentAutoStageResult(IReadOnlyList<AgentAction> Actions, string? Failure);
