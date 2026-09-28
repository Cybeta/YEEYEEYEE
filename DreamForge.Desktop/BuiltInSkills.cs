namespace DreamForge.Desktop;

public sealed record BuiltInSkill(
    string Id,
    string Name,
    string Description,
    string OutputFormat,
    IReadOnlyList<string> Keywords);

public static class BuiltInSkills
{
    public static IReadOnlyList<BuiltInSkill> All { get; } = new[]
    {
        new BuiltInSkill("node-operations", "节点操作", "新建、修改、删除节点，创建或删除节点连线。", "通过 actions 输出 create_node、update_node、delete_node、create_edge 或 delete_edge。", new[] { "新建节点", "创建节点", "修改节点", "更新节点", "删除节点", "移除节点", "连线", "连接节点" }),
        new BuiltInSkill("chapter-decomposition", "章节拆解", "读取选中章节内容，批量拆解为剧情、场景、分镜、角色、道具和对白节点，并建立父子关系与制作顺序。", "按章节→剧情→场景→分镜→角色/道具/对白的层级输出完整 actions；每个节点必须有标题、正文和 parentTarget，节点之间使用 create_edge 连接。", new[] { "章节拆解", "拆解章节", "拆分章节", "建立所有节点", "自动建节点", "整章节点", "一键拆解" }),
        new BuiltInSkill("image-generation", "图片生成", "根据节点、角色、场景或道具设定生成图片，并保持参考图连续性。", "输出提示词、负面提示词、画幅、参考图和生成版本；实际生成由已配置的图片 Provider 执行。", new[] { "生成图片", "出图", "生图", "图片生成", "参考图" }),
        new BuiltInSkill("video-generation", "视频生成", "根据分镜和关键帧生成短视频或图生视频任务。", "输出镜头动作、起止状态、运镜、时长、画幅和参考帧；未配置视频 Provider 时只生成任务规格，不伪造视频结果。", new[] { "生成视频", "视频生成", "图生视频", "文生视频", "让图片动起来" }),
        new BuiltInSkill("story-generation", "剧情生成", "从一句话创意生成选题、冲突、人物目标、反转、分集大纲和结尾悬念。", "输出结构化剧情节点内容，默认优先明确前 3 秒冲突、主角目标和每集钩子。", new[] { "生成剧情", "写剧情", "剧情生成", "故事大纲", "短剧" }),
        new BuiltInSkill("storyboard-generation", "分镜生成", "把剧本拆成镜头，补充景别、动作、镜头运动、时长、台词、音效和生成备注。", "输出镜头编号、场景、人物、动作、景别、运镜、时长、台词、音效和提示词。", new[] { "生成分镜", "分镜生成", "分镜表", "镜头拆分", "镜头脚本" }),
        new BuiltInSkill("character-generation", "角色设定", "生成角色小传、外观锚点、服装、道具、性格、关系和参考图提示词。", "输出角色档案和正面、侧面、四分之三、半身、全身参考组规格。", new[] { "生成角色", "角色设定", "人物设定", "角色档案", "人物小传" }),
        new BuiltInSkill("scene-generation", "场景设定", "生成场景结构、时间天气、光线、色调、空间关系和连续性基准。", "输出场景基准图描述、远中近景构图和镜头连续性约束。", new[] { "生成场景", "场景设定", "地点设定", "场景基准" }),
        new BuiltInSkill("prop-generation", "道具设定", "生成关键道具的外观、材质、尺寸、使用方式、剧情功能和连续性约束。", "输出道具档案、细节特写提示词、使用镜头约束和版本命名。", new[] { "生成道具", "道具设定", "物品设定", "道具档案" })
    };

    public static BuiltInSkill? Resolve(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return All
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

    public static string DescribeForAgent() => string.Join(
        "\n",
        All.Select(skill => $"- {skill.Name}（{skill.Id}）：{skill.Description} 输出：{skill.OutputFormat}"));
}
