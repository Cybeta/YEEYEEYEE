// DreamForge.Core.Contracts.cs
// YEEYEEYEE核心契约 · C# 代码骨架
// 定位：通道层（IChannelService）+ 技能层（Skill）的统一类型契约
// 约定：
//   1. 本文件不含业务逻辑，只定义类型与接口，供桌面端 / Web 端 / 房间服务三方共用
//   2. 所有跨进程数据必须可 JSON 序列化，禁止暴露内部类型
//   3. 枚举以 int 显式赋值，便于协议演进与持久化
//   4. 见《YEEYEEYEE-架构文档.md》§3 / §4，《YEEYEEYEE-技能体系.md》§3

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace DreamForge.Core.Contracts;

// ============================================================
//  基础枚举
// ============================================================

/// <summary>客户端类型，决定通道与技能的可见范围。</summary>
/// <remarks>
/// 桌面端可见本地 + 云端 + 团队通道；
/// Web 端仅可见云端 + 团队通道，本地通道对其等同于不存在。
/// 见《YEEYEEYEE-项目企划.md》§5.4。
/// </remarks>
public enum ClientType
{
    Desktop = 1,
    Web     = 2,
}

/// <summary>技能的来源位置，由 <see cref="SkillLocationJudge.Judge"/> 自动判定。</summary>
/// <remarks>
/// 与 <see cref="SkillScope"/> 是两个独立轴：
/// location 回答「依赖在哪」，scope 回答「谁能看」。
/// 一个技能可以是「本地来源、团队可见」。
/// 见《YEEYEEYEE-技能体系.md》§4.1。
/// </remarks>
public enum SkillLocation
{
    /// <summary>仅引用本地 Channel / Tool。</summary>
    Local  = 1,
    /// <summary>仅引用云端 Channel / Tool，或纯组合编排无资源依赖。</summary>
    Cloud  = 2,
    /// <summary>同时引用本地与云端依赖，按本地处理。</summary>
    Hybrid = 3,
}

/// <summary>技能的可见范围（可见性轴）。</summary>
/// <remarks>
/// 可见性 ≠ 调用权。看到入口只代表可见，实际执行仍受配额与审批约束。
/// 见《YEEYEEYEE-技能体系.md》§4.3。
/// </remarks>
public enum SkillScope
{
    Private = 1,   // 仅本人
    Team    = 2,   // 团队成员可读/可执行
    Public  = 3,   // 所有人可读/可引用
}

/// <summary>技能的三层类型。</summary>
/// <remarks>
/// ATOMIC 不可编辑内部；COMPOSITE 可复制派生后修改，可嵌套但需循环检测；
/// TEMPLATE 是 COMPOSITE + 预设参数 + 固定连线，套用即生成副本。
/// 见《YEEYEEYEE-技能体系.md》§2。
/// </remarks>
public enum SkillType
{
    Atomic     = 1,
    Composite  = 2,
    Template   = 3,
}

/// <summary>能力枚举，画布层只依赖此枚举，不感知具体厂商。</summary>
/// <remarks>新增能力只需在此追加值，禁止在调用方写 switch 分支判断厂商。</remarks>
public enum Capability
{
    Unknown          = 0,
    TextToImage      = 100,   // 文本生成图像
    TextToVideo      = 101,   // 文本生成视频
    ImageToVideo     = 102,   // 图像生成视频
    TextToText       = 200,   // 文本生成文本（扩写/改写/翻译）
    TextToNovel      = 201,   // 文本生成小说
    NovelToChapter   = 202,   // 小说拆章节
    ImageUpscale     = 300,   // 图像超分
    FileRead         = 900,   // 文件读取
    FileWrite        = 901,   // 文件写入
}

/// <summary>操作来源，用于撤销栈与审计，明确区分人工与 AI 改动。</summary>
public enum OperationSource
{
    UserManual      = 1,
    AiAssistant     = 2,
    SkillExecution  = 3,
}

/// <summary>服务端会话上下文；角色与客户端类型来自 server claims，不接受请求体覆盖。</summary>
public sealed record SessionContext
{
    public Guid SessionId { get; init; }
    public Guid UserId { get; init; }
    public ClientType ClientType { get; init; }
    public MemberRole Role { get; init; }
    public IReadOnlySet<string> ServerClaims { get; init; } = new HashSet<string>();
}

// ============================================================
//  统一中间格式
// ============================================================

/// <summary>跨层统一的中间格式：画布节点、技能执行器、AI 生成的结构变更共用。</summary>
/// <remarks>
/// 任何跨进程调用（含 AI 助手的画布改动）必须输出此格式，
/// 避免在画布层、技能层、AI 层之间做逐层转换。
/// 见《YEEYEEYEE-架构文档.md》§3.3。
/// </remarks>
public sealed record Invocation
{
    public Guid       InvocationId { get; init; } = Guid.NewGuid();
    public string     Tool         { get; init; } = string.Empty;
    public Capability Capability   { get; init; }
    public string     Channel      { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, JsonElement> Inputs { get; init; }
        = new Dictionary<string, JsonElement>();
    public IReadOnlyList<AssetRef> Assets { get; init; }
        = Array.Empty<AssetRef>();
}

/// <summary>资产引用，只传句柄不传内容，避免大对象跨进程。</summary>
public sealed record AssetRef
{
    public string Role { get; init; } = string.Empty;   // input / output
    public string Ref  { get; init; } = string.Empty;   // asset://local/abc / asset://team/xyz
}

public enum JobState
{
    Queued = 1,
    Running = 2,
    Cancelling = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>执行结果，表达服务端任务状态与产物引用，而非仅表达同步成功/失败。</summary>
public sealed record ExecutionResult
{
    public Guid       InvocationId { get; init; }
    public Guid       JobId        { get; init; }
    public JobState   State        { get; init; }
    public int        ProgressPercent { get; init; }
    public string?    ErrorCode    { get; init; }
    public string?    ErrorMessage { get; init; }
    public string     IdempotencyKey { get; init; } = string.Empty;
    public IReadOnlyList<AssetRef> Outputs { get; init; }
        = Array.Empty<AssetRef>();
}

// ============================================================
//  通道层
// ============================================================

/// <summary>连接通道，封装协议 / 地址 / 认证方式。</summary>
/// <remarks>
/// 凭据不直接存于此对象，仅持凭据库句柄，避免序列化时泄露 Key。
/// 见《YEEYEEYEE-架构文档.md》§3.1。
/// </remarks>
public sealed record Channel
{
    public Guid       ChannelId      { get; init; } = Guid.NewGuid();
    public string     Name           { get; init; } = string.Empty;
    public string     Protocol       { get; init; } = string.Empty; // openai-compat / comfyui / mcp
    public SkillLocation Location    { get; init; }
    public string      Endpoint      { get; init; } = string.Empty; // 本地可为 127.0.0.1:8188
    public string      CredentialsRef { get; init; } = string.Empty; // 指向系统密钥库的句柄
    public IReadOnlyList<Capability> Capabilities { get; init; }
        = Array.Empty<Capability>();
}

/// <summary>通道服务接口 —— 全架构唯一允许访问本地资源的边界。</summary>
/// <remarks>
/// 桌面端与 Web 端实现此接口的不同版本：
///   - 桌面端：读取系统密钥库、枚举本机 ComfyUI、spawn MCP 进程
///   - Web 端：仅返回服务端配置的共享通道，拒绝本地类型请求
/// 节点代码不得出现 <c>if (isDesktop)</c> 分支。
/// 见《YEEYEEYEE-架构文档.md》§3.1 / §3.2。
/// </remarks>
public interface IChannelService
{
    /// <summary>按服务端会话上下文过滤通道；不可见通道等同于不存在。</summary>
    Task<IEnumerable<Channel>> GetChannelsAsync(
        SessionContext session, CancellationToken ct = default);

    /// <summary>按服务端会话上下文过滤技能。</summary>
    Task<IEnumerable<Skill>> GetSkillsAsync(
        SessionContext session, CancellationToken ct = default);

    /// <summary>每次执行按当前 server claims 重新鉴权，并支持幂等与取消。</summary>
    Task<ExecutionResult> ExecuteAsync(
        SessionContext session, Guid channelId, Invocation invocation,
        string idempotencyKey, CancellationToken ct = default);

    Task CancelAsync(SessionContext session, Guid jobId, CancellationToken ct = default);
}

/// <summary>成员角色，用于通道与技能的权限判定。</summary>
public enum MemberRole
{
    None        = 0,
    Member      = 1,
    TeamAdmin   = 2,
    RoomOwner   = 3,
}

// ============================================================
//  技能层
// ============================================================

/// <summary>技能节点定义（画布图的一部分）。</summary>
public sealed record SkillNode
{
    public string                        Id       { get; init; } = string.Empty;
    public Guid                          ToolId   { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Params { get; init; }
        = new Dictionary<string, JsonElement>();
}

/// <summary>技能连线，以字段路径表达数据流向。</summary>
/// <remarks>
/// <c>From</c>/<c>To</c> 格式为 <c>nodeId.output.field</c> → <c>nodeId.input.field</c>。
/// 见《YEEYEEYEE-技能体系.md》§3.2。
/// </remarks>
public sealed record SkillEdge
{
    public string From { get; init; } = string.Empty;   // n1.output.text
    public string To   { get; init; } = string.Empty;   // n2.input.prompt
}

/// <summary>技能图：节点 + 连线。</summary>
public sealed record SkillGraph
{
    public IReadOnlyList<SkillNode> Nodes { get; init; } = Array.Empty<SkillNode>();
    public IReadOnlyList<SkillEdge> Edges { get; init; } = Array.Empty<SkillEdge>();
}

/// <summary>
/// 技能实体 —— YEEYEEYEE第四种资产。
/// 不拥有能力，只通过 <see cref="References"/> 引用能力。
/// </summary>
/// <remarks>
/// <see cref="References"/> 是核心字段，支撑三项职责：
/// ① 依赖追踪  ② 循环检测  ③ 迁移校验（云端上传 / 本地下载）。
/// 见《YEEYEEYEE-技能体系.md》§3.1。
/// </remarks>
public sealed record Skill
{
    public Guid                         Id          { get; init; } = Guid.NewGuid();
    public string                       Name        { get; init; } = string.Empty;
    public string                       Description { get; init; } = string.Empty;
    public SkillType                    Type        { get; init; }
    public SkillLocation                Location    { get; init; }
    public SkillScope                   Scope       { get; init; }
    public string                       Version     { get; init; } = "1.0.0";
    public SkillGraph                   Graph       { get; init; } = new();
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; }
        = new Dictionary<string, JsonElement>();

    /// <summary>带类型、目标 ID、版本/约束的引用；未知或无权引用必须 fail-closed。</summary>
    public IReadOnlyList<TypedReference> References { get; init; } = Array.Empty<TypedReference>();

    public Guid?  OwnerId { get; init; }
    public Guid?  TeamId  { get; init; }   // 空 = 个人
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

// ============================================================
//  技能来源判定
// ============================================================

public enum ReferenceKind
{
    Skill = 1,
    Tool = 2,
    Channel = 3,
    Asset = 4,
    LocalResource = 5,
}

/// <summary>显式 typed ref，版本约束与资源依赖随引用传输。</summary>
public sealed record TypedReference
{
    public ReferenceKind Kind { get; init; }
    public Guid TargetId { get; init; }
    public string VersionConstraint { get; init; } = string.Empty;
    public IReadOnlyList<TypedReference> Dependencies { get; init; } = Array.Empty<TypedReference>();
}

public sealed record Tool
{
    public Guid ToolId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Capability Capability { get; init; }
    public IReadOnlyList<TypedReference> LocalDependencies { get; init; } = Array.Empty<TypedReference>();
}

/// <summary>来源判定算法，不依赖用户手选，纯由引用图推导。</summary>
/// <remarks>
/// 算法（《YEEYEEYEE-技能体系.md》§4.2）：
///   - 任一本地依赖           → Local
///   - 仅云端依赖或无资源依赖  → Cloud
///   - 本地 + 云端混合         → Hybrid（按本地处理）
/// </remarks>
public static class SkillLocationJudge
{
    private const int MaxDepth = 32;   // 递归深度上限，防止恶意引用图打爆调用栈

    public static SkillLocation Judge(
        Skill skill,
        IReadOnlyDictionary<Guid, Channel> channels,
        IReadOnlyDictionary<Guid, Tool> tools,
        IReadOnlyDictionary<Guid, Skill> skills)
    {
        if (skill is null) throw new ArgumentNullException(nameof(skill));
        var visited = new HashSet<Guid> { skill.Id };
        return JudgeCore(skill, channels, tools, skills, visited, 0);
    }

    private static SkillLocation JudgeCore(
        Skill skill,
        IReadOnlyDictionary<Guid, Channel> channels,
        IReadOnlyDictionary<Guid, Tool> tools,
        IReadOnlyDictionary<Guid, Skill> skills,
        HashSet<Guid> visited,
        int depth)
    {
        if (depth > MaxDepth)
            throw new SkillReferenceException(skill.Id, $"引用嵌套超过上限 {MaxDepth} 层");

        var hasLocal = false;
        var hasCloud = false;
        foreach (var reference in skill.References)
        {
            if (reference.TargetId == Guid.Empty || !visited.Add(reference.TargetId))
                throw new SkillReferenceException(reference.TargetId, "未知或循环引用");

            switch (reference.Kind)
            {
                case ReferenceKind.Channel when channels.TryGetValue(reference.TargetId, out var channel):
                    hasLocal |= channel.Location is SkillLocation.Local or SkillLocation.Hybrid;
                    hasCloud |= channel.Location is SkillLocation.Cloud;
                    break;
                case ReferenceKind.Skill when skills.TryGetValue(reference.TargetId, out var child):
                    var childLocation = JudgeCore(child, channels, tools, skills, visited, depth + 1);
                    hasLocal |= childLocation is SkillLocation.Local or SkillLocation.Hybrid;
                    hasCloud |= childLocation is SkillLocation.Cloud;
                    break;
                case ReferenceKind.Tool when tools.TryGetValue(reference.TargetId, out var tool):
                    hasLocal |= tool.LocalDependencies.Count > 0;
                    hasCloud |= tool.LocalDependencies.Count == 0;
                    break;
                default:
                    throw new SkillReferenceException(reference.TargetId, "未知、缺失或不支持的引用");
            }

            visited.Remove(reference.TargetId);
        }

        if (hasLocal && hasCloud) return SkillLocation.Hybrid;
        if (hasLocal) return SkillLocation.Local;
        return SkillLocation.Cloud;
    }
}

/// <summary>技能引用异常：循环引用或嵌套过深。</summary>
public sealed class SkillReferenceException : InvalidOperationException
{
    public Guid FaultingSkillId { get; }
    public SkillReferenceException(Guid skillId, string message) : base(message)
        => FaultingSkillId = skillId;
}

// ============================================================
//  撤销与协作
// ============================================================

/// <summary>撤销栈操作记录，AI 生成的一整批节点算一次操作。</summary>
/// <remarks>
/// 见《YEEYEEYEE-架构文档.md》§5.3。MVP 仅用于单人撤销；协作房间广播 CRDT 操作，不能用快照覆盖他人改动。
/// </remarks>
public sealed record OperationRecord
{
    public Guid                OperationId    { get; init; } = Guid.NewGuid();
    public Guid                ActorId        { get; init; }
    public DateTime            Timestamp      { get; init; } = DateTime.UtcNow;
    public OperationSource     Source         { get; init; }
    public SkillGraph          BeforeSnapshot { get; init; } = new();
    public SkillGraph          AfterSnapshot  { get; init; } = new();
    public IReadOnlyList<string> AffectedNodeIds { get; init; } = Array.Empty<string>();
}

/// <summary>MVP 单人撤销契约；不表达协作远端快照回滚。</summary>
public interface IUndoStack
{
    void Push(OperationRecord record);
    OperationRecord? Pop();   // 仅本地/单人撤回，返回被撤销的记录
}

// ============================================================
//  迁移校验
// ============================================================

/// <summary>迁移校验结果，拒绝时明确告知缺失项。</summary>
/// <remarks>
/// 「提示差哪个通道」而非只报错的契约依据。
/// 见《YEEYEEYEE-技能体系.md》§5。
/// </remarks>
public sealed record MigrationCheck
{
    public bool                          Allowed { get; init; }
    public string                        Reason  { get; init; } = string.Empty;
    public IReadOnlyList<MissingChannel> Missing { get; init; }
        = Array.Empty<MissingChannel>();
}

public sealed record MissingChannel
{
    public Guid   ChannelId { get; init; }
    public string Name       { get; init; } = string.Empty;
    public string Hint       { get; init; } = string.Empty;   // 建议的解决方式
}

public interface ISkillMigration
{
    /// <summary>上传到云端前校验：含本地权重/文件路径则禁止。</summary>
    Task<MigrationCheck> ValidateUploadAsync(Skill skill, CancellationToken ct = default);

    /// <summary>下载到本地前校验：列出本机缺失的通道。</summary>
    Task<MigrationCheck> ValidateDownloadAsync(Skill skill, CancellationToken ct = default);
}

// ============================================================
//  契约变更规则（维护者必读）
// ============================================================
//  1. 枚举新增值必须追加在末尾，禁止插入中间或改已有值 —— 枚举以 int 显式赋值。
//  2. record 新增字段必须设默认值，保证旧序列化数据可反序列化。
//  3. 接口方法签名变更视为破坏性变更，须升 major 版本。
//  4. 新增能力只改 Capability 枚举，禁止在调用方写厂商判断分支。
//  5. References 递归深度上限见 SkillLocationJudge.MaxDepth，
//     任何调整必须同步更新《YEEYEEYEE-技能体系.md》§3.1。
