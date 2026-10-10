namespace YEEYEEYEE.Desktop;

public sealed record BuiltInSkill(
    string Id,
    string Name,
    string Description,
    string OutputFormat,
    IReadOnlyList<string> Keywords);

public static class BuiltInSkills
{
    private const string ResourceProtocol = "角色、场景、道具使用 create_entity / update_entity 与 create_entity_version 写入资源库实体与版本，分镜通过 entityTargets 引用；默认不各建画布节点，用户明确要求铺开画布时才 create_node。";
    private const string ShotRequirementsProtocol = "每个分镜 create_node / update_node 必须输出 assetRequirements={\"character\":\"required|none|unknown\",\"scene\":\"required|none|unknown\",\"prop\":\"required|none|unknown\"}；required 通过 entityTargets 绑定资产，none 明确无需求，信息不足用 unknown 并 ask 确认，不能把空引用当成无需求。";
    public static IReadOnlyList<BuiltInSkill> All { get; } = new[]
    {
        new BuiltInSkill("node-operations", "节点操作", "新建、修改、删除节点，创建或删除节点连线。", "通过 actions 输出 create_node、update_node、delete_node、create_edge 或 delete_edge。", new[] { "新建节点", "创建节点", "修改节点", "更新节点", "删除节点", "移除节点", "连线", "连接节点" }),
        new BuiltInSkill("chapter-decomposition", "章节拆解", "读取选中章节，拆解剧情、分镜和对白节点；角色、场景、道具写入资源库实体与版本，建立制作顺序。", "按章节与剧情归属输出完整 actions；画布节点有标题、正文和 parentTarget，制作顺序用 create_edge。" + ResourceProtocol + ShotRequirementsProtocol, new[] { "章节拆解", "拆解章节", "拆分章节", "建立所有节点", "自动建节点", "整章节点", "一键拆解" }),
        new BuiltInSkill(
            "image-generation",
            "图片生成",
            "根据节点、角色、场景或道具设定生成图片，并保持参考图连续性。",
            "输出提示词、负面提示词、画幅、参考图和生成版本；实际生成由已配置的图片 Provider 执行。\n"
            + $"参考图纪律（比参考图数量重要）：{PromptBaseline.ConsistencyRule}\n"
            + $"负面提示词用法：{PromptBaseline.NegativeUsageNote}",
            new[] { "生成图片", "出图", "生图", "图片生成", "参考图" }),
        new BuiltInSkill("video-generation", "视频生成", "根据分镜和关键帧生成短视频或图生视频任务。", "输出镜头动作、起止状态、运镜、时长、画幅和参考帧；未配置视频 Provider 时只生成任务规格，不伪造视频结果。", new[] { "生成视频", "视频生成", "图生视频", "文生视频", "让图片动起来" }),
        new BuiltInSkill("story-generation", "剧情生成", "从一句话创意生成选题、冲突、人物目标、反转、分集大纲和结尾悬念。", "输出结构化剧情节点内容，默认优先明确前 3 秒冲突、主角目标和每集钩子。", new[] { "生成剧情", "写剧情", "剧情生成", "故事大纲", "短剧" }),
        new BuiltInSkill(
            "storyboard-generation",
            "分镜生成",
            "把剧本拆成镜头，补充景别、动作、镜头运动、时长、台词、音效和生成备注。",
            "按【镜号与归属】【景别】…的顺序输出镜头脚本，并把这一镜用到的角色 / 场景 / 道具挂成引用（不各占一个画布节点）。\n"
            + ResourceProtocol + ShotRequirementsProtocol
            + "引用纪律：同一角色的外观锚点段、同一场景的光源与陈设措辞，在每个镜头里**逐字复制**，"
            + "只替换动作 / 情绪 / 景别 / 光线——换个说法重述是角色漂移的头号原因。\n"
            + $"分镜字段：{PromptBaseline.SectionGuide(NodeCategory.Storyboard)}。\n"
            + $"提示词骨架（顺序即权重）：{PromptBaseline.PromptSkeleton(NodeCategory.Storyboard)}。\n"
            + $"负面提示词：{PromptBaseline.NegativeFor(NodeCategory.Storyboard)}。",
            new[] { "生成分镜", "分镜生成", "分镜表", "镜头拆分", "镜头脚本" }),
        new BuiltInSkill(
            "character-generation",
            "角色设定",
            "生成角色小传与外观锚点：身份、体型、面部、发型、标志特征、服装、配饰与神态，并给出参考图与出图提示词。",
            ResourceProtocol + "设定卡正文**必须**按下面的小标题分段写全，缺段的角色卡在后续镜头里会越画越不像；"
            + "「标志特征」与「配饰」都要带位置。服装措辞全卡只能有一种说法（前视写「藏青外套」、后视就不能写「深蓝夹克」），"
            + "否则模型会当成两件衣服。写完正文后另附出图提示词。\n"
            + $"角色字段：{PromptBaseline.SectionGuide(NodeCategory.Character)}。\n"
            + $"参考图成套给三样：①{PromptBaseline.TurnaroundSpec}\n"
            + $"②{PromptBaseline.AnchorSpec}\n"
            + $"③{PromptBaseline.ExpressionSpec}\n"
            + $"跨镜头一致性纪律：{PromptBaseline.ConsistencyRule}\n"
            + $"提示词骨架（顺序即权重）：{PromptBaseline.PromptSkeleton(NodeCategory.Character)}。\n"
            + $"负面提示词：{PromptBaseline.NegativeFor(NodeCategory.Character)}。{PromptBaseline.NegativeUsageNote}",
            new[] { "生成角色", "角色设定", "人物设定", "角色档案", "人物小传" }),
        new BuiltInSkill(
            "scene-generation",
            "场景设定",
            "生成场景的空间结构、时代地域、时间天气、光源与色温、材质、陈设、色彩基调与机位，并给出基准图与出图提示词。",
            ResourceProtocol + "「光源来源与色温」**必须**写清来源（例如「天花板暖钨丝灯 3200K 打出几摊光池」「窗外冷月光」），"
            + "禁止「一个舒服的咖啡馆」这类没有信息量的写法——这是场景提示词里最容易被漏、又最影响成片的一项。"
            + "基准图默认不出现主要人物，远中近三档构图成套给出；另出一张只交代光源方向的定调图。"
            + "跨镜头一致性：**光源方向与主要陈设的相对位置**在每个镜头的提示词里逐字复用——"
            + "换个说法（「木柜台」写成「木质吧台」）会让同一个空间看着像两个地方。\n"
            + $"场景字段：{PromptBaseline.SectionGuide(NodeCategory.Scene)}。\n"
            + $"提示词骨架（顺序即权重）：{PromptBaseline.PromptSkeleton(NodeCategory.Scene)}。\n"
            + $"负面提示词：{PromptBaseline.NegativeFor(NodeCategory.Scene)}。{PromptBaseline.NegativeUsageNote}",
            new[] { "生成场景", "场景设定", "地点设定", "场景基准" }),
        new BuiltInSkill(
            "prop-generation",
            "道具设定",
            "生成关键道具的名称用途、尺寸比例、材质工艺、颜色磨损、关键细节、与角色的关系和剧情功能，并给出特写提示词。",
            ResourceProtocol + "尺寸比例与材质决定它看起来真不真，剧情功能决定它为什么在这儿，两项都要写。"
            + "关键细节带位置（例如「盖内压着一枚铜印」）。特写用干净背景、**单一视图**——"
            + "道具特写要把「多个视角、并排、拼图」写进负面（不写的话模型会自作主张排成一排），"
            + "并在画面里放一个尺度参照物（手掌、硬币）交代大小。\n"
            + $"道具字段：{PromptBaseline.SectionGuide(NodeCategory.Prop)}。\n"
            + $"提示词骨架（顺序即权重）：{PromptBaseline.PromptSkeleton(NodeCategory.Prop)}。\n"
            + $"负面提示词：{PromptBaseline.NegativeFor(NodeCategory.Prop)}。{PromptBaseline.NegativeUsageNote}",
            new[] { "生成道具", "道具设定", "物品设定", "道具档案" })
    };

    /// <summary>
    /// 按关键词命中挑一个内置技能。停用名单里的直接跳过——停用只是"别再用它"，
    /// 配置里的名单比改代码表更合理（见 <see cref="AiProviderConfig.DisabledBuiltInSkills"/>）。
    /// </summary>
    public static BuiltInSkill? Resolve(string text, IReadOnlyCollection<string>? disabled = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return All
            .Where(skill => disabled is null || !disabled.Contains(skill.Id))
            .Select(skill => new
            {
                Skill = skill,
                Score = skill.Keywords.Count(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Skill.Id, StringComparer.Ordinal)
            .Select(item => item.Skill)
            .FirstOrDefault();
    }

    /// <summary>
    /// 摊给 Agent 看的技能清单。
    ///
    /// 停用的技能**不列**：只把停用名单用在 <see cref="Resolve"/> 上是漏的——
    /// 提示词里还写着「可调用的生成技能清单：图片生成…」，模型就会照着去调，
    /// 用户看到的将是「我明明停用了它，它还在用」。停用要么彻底，要么别说停用。
    /// </summary>
    public static string DescribeForAgent(IReadOnlyCollection<string>? disabled = null)
    {
        disabled ??= AiProviderSettings.Load().DisabledBuiltInSkills;
        var lines = All
            // 手写 JSON 里给了 null 也不该炸：这里不假设配置里的名单一定存在。
            .Where(skill => disabled is null || !disabled.Contains(skill.Id))
            .Select(skill => $"- {skill.Name}（{skill.Id}）：{skill.Description} 输出：{skill.OutputFormat}")
            .ToList();
        return lines.Count == 0
            ? "（内置生成技能当前全部处于停用状态；需要时请在「设置 → 技能管理」里重新启用。）"
            : string.Join("\n", lines);
    }
}
