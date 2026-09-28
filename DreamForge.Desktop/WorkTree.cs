using System.Text.Json.Serialization;

namespace DreamForge.Desktop;

/// <summary>
/// 工作树条目的种类。注意这是**叙事轴**的分类：角色在这里是"他在剧情里是谁"，
/// 能力是"他会什么"，与设定库（外观、服装、空间布局、参考图）不是同一份数据。
/// 枚举值会按数字落盘，新增成员只能追加在末尾，不要插入或调序。
/// </summary>
public enum WorkTreeKind
{
    Project,
    Character,
    /// <summary>角色能力（御剑术、易容术这类剧情内的本领），不是本程序的生成技能。</summary>
    Ability,
    Prop,
    Scene,
    Version,
    /// <summary>章节条目：项目下的剧情切分锚，节点靠它跟随章节更新（新增成员只能追加在末尾）。</summary>
    Chapter
}

public sealed class WorkTreeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public WorkTreeKind Kind { get; set; } = WorkTreeKind.Ability;
    public string Name { get; set; } = string.Empty;

    /// <summary>来源章节，例如「第10章」。能力与设定的剧情进度版本靠它对齐。</summary>
    public string Chapter { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 叙事设定：能力效果、剧情功能、成长阶段这类**剧情向**内容。
    /// 外观、服装、空间布局与参考图属于设定库，不要写在这里。
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    public Guid? SourceEntityId { get; set; }
    public Guid? SourceVariantId { get; set; }
    public Guid? SourceVersionId { get; set; }
    public Guid? SupersedesVersionId { get; set; }

    /// <summary>能力自带的素材（特效或形态参考图、演示视频），属于叙事侧，与设定库参考图不重复。</summary>
    public List<WorkflowAttachment> Attachments { get; set; } = new();

    public static string KindName(WorkTreeKind kind) => kind switch
    {
        WorkTreeKind.Project => "项目",
        WorkTreeKind.Character => "角色",
        WorkTreeKind.Ability => "能力",
        WorkTreeKind.Prop => "道具",
        WorkTreeKind.Scene => "场景",
        WorkTreeKind.Chapter => "章节",
        _ => "版本"
    };

    /// <summary>
    /// 解析 Agent 给的种类文本。旧协议里的 "Skill"/"技能" 指的是角色能力，
    /// 继续映射到 <see cref="WorkTreeKind.Ability"/>；本程序的生成技能不在工作树里。
    /// </summary>
    public static WorkTreeKind ParseKind(string? value) => (value ?? string.Empty).Trim() switch
    {
        "项目" or "Project" => WorkTreeKind.Project,
        "角色" or "Character" => WorkTreeKind.Character,
        "能力" or "Ability" or "技能" or "Skill" => WorkTreeKind.Ability,
        "道具" or "Prop" => WorkTreeKind.Prop,
        "场景" or "Scene" => WorkTreeKind.Scene,
        "章节" or "Chapter" => WorkTreeKind.Chapter,
        "版本" or "Version" => WorkTreeKind.Version,
        _ => WorkTreeKind.Ability
    };
}

