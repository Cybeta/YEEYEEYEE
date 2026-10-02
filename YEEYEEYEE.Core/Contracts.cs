using System.Text.Json;

namespace YEEYEEYEE.Core;

public enum ClientType { Desktop = 1, Web = 2 }
public enum SkillLocation { Local = 1, Cloud = 2, Hybrid = 3 }
public enum SkillScope { Private = 1, Team = 2, Public = 3 }
public enum SkillType { Atomic = 1, Composite = 2, Template = 3 }
public enum Capability { Unknown = 0, TextToImage = 100, TextToVideo = 101, ImageToVideo = 102, ImageToImage = 103, TextToText = 200, TextToNovel = 201, NovelToChapter = 202, ImageUpscale = 300, FileRead = 900, FileWrite = 901 }
public enum OperationSource { UserManual = 1, AiAssistant = 2, SkillExecution = 3 }
public enum MemberRole { None = 0, Member = 1, TeamAdmin = 2, RoomOwner = 3 }
public enum JobState { Queued = 1, Running = 2, Cancelling = 3, Succeeded = 4, Failed = 5, Cancelled = 6 }
public enum ExternalTaskState { Unknown = 0, Queued = 1, Running = 2, Succeeded = 3, Failed = 4, Cancelled = 5 }
public enum ReferenceKind { Skill = 1, Tool = 2, Channel = 3, Asset = 4, LocalResource = 5 }

public sealed record SessionContext
{
    public Guid SessionId { get; init; }
    public Guid UserId { get; init; }
    public ClientType ClientType { get; init; }
    public MemberRole Role { get; init; }
    public IReadOnlySet<string> ServerClaims { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed record AssetRef
{
    public string Role { get; init; } = string.Empty;
    public string Ref { get; init; } = string.Empty;
}

public sealed record Invocation
{
    public Guid InvocationId { get; init; } = Guid.NewGuid();
    public string Tool { get; init; } = string.Empty;
    public Capability Capability { get; init; }
    public string Channel { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, JsonElement> Inputs { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<AssetRef> Assets { get; init; } = Array.Empty<AssetRef>();
}

public sealed record ExecutionOutput
{
    public string? ExternalTaskId { get; init; }
    public bool AwaitExternalCompletion { get; init; }
    public IReadOnlyList<AssetRef> Outputs { get; init; } = Array.Empty<AssetRef>();
}

public sealed record ExternalTaskUpdate
{
    public string ExternalTaskId { get; init; } = string.Empty;
    public ExternalTaskState State { get; init; }
    public int ProgressPercent { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<AssetRef> Outputs { get; init; } = Array.Empty<AssetRef>();
}

public sealed record ExecutionResult
{
    public Guid UserId { get; init; }
    public Guid InvocationId { get; init; }
    public Guid JobId { get; init; }
    public JobState State { get; init; }
    public int ProgressPercent { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ExternalTaskId { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public IReadOnlyList<AssetRef> Outputs { get; init; } = Array.Empty<AssetRef>();

    /// <summary>该次执行的输入参数快照，用于任务详情与问题排查。</summary>
    public IReadOnlyDictionary<string, JsonElement> Inputs { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>该次执行使用的工具、能力与通道，用于任务详情展示与按相同参数重新发起。</summary>
    public string Tool { get; init; } = string.Empty;
    public Capability Capability { get; init; }
    public string Channel { get; init; } = string.Empty;

    /// <summary>第几次尝试：首次为 1，重试逐次递增（目标 5 / 失败重试）。</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>本次尝试所重试的那个 Job；首次尝试为 null。</summary>
    public Guid? RetryOfJobId { get; init; }

    /// <summary>同一次输入的反复尝试共享的根 Job；首次尝试的根就是自己。</summary>
    public Guid RootJobId { get; init; }
}

public sealed record Channel
{
    public Guid ChannelId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public SkillLocation Location { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string CredentialsRef { get; init; } = string.Empty;
    public IReadOnlyList<Capability> Capabilities { get; init; } = Array.Empty<Capability>();
}

public sealed record TypedReference
{
    public ReferenceKind Kind { get; init; }
    public Guid TargetId { get; init; }
    public string VersionConstraint { get; init; } = string.Empty;
    public IReadOnlyList<TypedReference> Dependencies { get; init; } = Array.Empty<TypedReference>();
}

public sealed record Tool
{
    public Guid ToolId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public Capability Capability { get; init; }
    public Guid? ChannelId { get; init; }
    public IReadOnlyList<TypedReference> LocalDependencies { get; init; } = Array.Empty<TypedReference>();
}

public sealed record SkillNode
{
    public string Id { get; init; } = string.Empty;
    public Guid ToolId { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Params { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record SkillEdge
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
}

public sealed record SkillGraph
{
    public IReadOnlyList<SkillNode> Nodes { get; init; } = Array.Empty<SkillNode>();
    public IReadOnlyList<SkillEdge> Edges { get; init; } = Array.Empty<SkillEdge>();
}

public sealed record Skill
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public SkillType Type { get; init; }
    public SkillLocation Location { get; init; }
    public SkillScope Scope { get; init; }
    public string Version { get; init; } = "1.0.0";
    public SkillGraph Graph { get; init; } = new();
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<TypedReference> References { get; init; } = Array.Empty<TypedReference>();
    public Guid? OwnerId { get; init; }
    public Guid? TeamId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record OperationRecord
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public Guid ActorId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public OperationSource Source { get; init; }
    public SkillGraph BeforeSnapshot { get; init; } = new();
    public SkillGraph AfterSnapshot { get; init; } = new();
    public IReadOnlyList<string> AffectedNodeIds { get; init; } = Array.Empty<string>();
}

public sealed record MigrationCheck
{
    public bool Allowed { get; init; }
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyList<MissingChannel> Missing { get; init; } = Array.Empty<MissingChannel>();
}

public sealed record MissingChannel
{
    public Guid ChannelId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;
}
