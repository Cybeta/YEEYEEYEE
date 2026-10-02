namespace DreamForge.Desktop;

public enum WorkTreeKind
{
    Project,
    Character,
    Ability,
    Prop,
    Scene,
    Version,
    Chapter,
    Resource,
    Appearance
}

public sealed class WorkTreeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public WorkTreeKind Kind { get; set; } = WorkTreeKind.Ability;
    public string Name { get; set; } = string.Empty;
    public string Chapter { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public Guid? SourceEntityId { get; set; }
    public Guid? SourceVariantId { get; set; }
    public Guid? SourceVersionId { get; set; }
    public Guid? SupersedesVersionId { get; set; }
    public Guid? ResourceId { get; set; }
    public Guid? ChapterId { get; set; }
    public bool VersionLocked { get; set; }
    public string LocalState { get; set; } = string.Empty;
    public List<WorkflowAttachment> Attachments { get; set; } = new();

    public static string KindName(WorkTreeKind kind) => kind switch
    {
        WorkTreeKind.Project => "项目",
        WorkTreeKind.Character => "角色",
        WorkTreeKind.Ability => "能力",
        WorkTreeKind.Prop => "道具",
        WorkTreeKind.Scene => "场景",
        WorkTreeKind.Chapter => "章节",
        WorkTreeKind.Resource => "资源",
        WorkTreeKind.Appearance => "出场",
        _ => "版本"
    };

    public static WorkTreeKind ParseKind(string? value) => (value ?? string.Empty).Trim() switch
    {
        "项目" or "Project" => WorkTreeKind.Project,
        "角色" or "Character" => WorkTreeKind.Character,
        "能力" or "Ability" or "技能" or "Skill" => WorkTreeKind.Ability,
        "道具" or "Prop" => WorkTreeKind.Prop,
        "场景" or "Scene" => WorkTreeKind.Scene,
        "章节" or "Chapter" => WorkTreeKind.Chapter,
        "资源" or "Resource" => WorkTreeKind.Resource,
        "出场" or "表现" or "Appearance" => WorkTreeKind.Appearance,
        "版本" or "Version" => WorkTreeKind.Version,
        _ => WorkTreeKind.Ability
    };
}
