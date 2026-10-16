using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

internal static class WebChatApi
{
    private static ProjectCanvasSceneStore? canvasStore => WebProjectApi.Current is { } mode ? WebProjectApi.Store(mode) : null;
    private static EditLeaseStore? leases => WebProjectApi.Current is { } mode ? WebProjectApi.Leases(mode) : null;
    private static WebChatStore chatStore = null!;
    internal static WebChatStore.Attachment? Attachment(HttpContext c, string session, string id) => Owner(c) is { } owner && chatStore.Get(owner, session) is not null ? chatStore.GetAttachment(owner, session, id) : null;
    public sealed record SendRequest(string? Text, bool Retry = false, string Mode = "approval", string? ChapterId = null, string? NodeId = null, long? BaseRevision = null, string[]? AttachmentIds = null, string? Source = null, string? TargetNodeTitle = null);
    public sealed record SkillInput(string? AspectRatio);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Running = new();
    private static readonly System.Text.RegularExpressions.Regex EmptyJsonTail = new(
        @"(?:\A|\n)[ \t]*```json[^\S\r\n]*\r?\n\s*(?:\{\s*\}\s*)?(?:```\s*)?\z",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);
    private static string? Owner(HttpContext c) => WebAccessGuard.CurrentUser(c) is { } user ? WebProjectApi.ScopedOwner(user.Id.ToString()) : null;
    private static YEEYEEYEE.Host.SingleMachineExecutionService execution = null!;

    private static YEEYEEYEE.Core.ExecutionResult AttachmentJob(Auth.WebUser user, WebChatMessage message, AgentAction action)
    {
        if (action.Target != message.TargetNodeId || !Guid.TryParse(action.Source, out var jobId) || !message.SkillJobIds.Contains(jobId) ||
            !execution.TryGet(jobId, out var job) || job!.UserId != user.Id || !WebJobAttachmentService.InCurrentCanvas(job) ||
            job.State != YEEYEEYEE.Core.JobState.Succeeded || !job.Outputs.Any(o => o.Ref == action.Content) ||
            !job.Inputs.TryGetValue("targetNodeId", out var target) || target.GetString() != action.Target)
            throw new InvalidDataException("附件必须来自本条对话在原目标节点生成的成功产物");
        return job.ToResult();
    }

    /// <summary>这条消息此刻**待执行**的那一批动作（已提交过的不再重复执行）。</summary>
    private static AgentAction[] PendingActions(WebChatMessage message) =>
        message.Actions.Skip(message.CommittedActionCount).ToArray();

    /// <summary>
    /// 一批动作的身份指纹：提交时拿它核对「用户批准的是不是这一版」。
    ///
    /// 为什么需要它：同一条消息上的动作会在**生成任务完成之后**被追加（attach_asset），
    /// 而消息 ID 与画布修订都可能不变——只按那两个判据，用户会在没看到新动作的情况下
    /// 把它一起批了。指纹覆盖整批动作的每个字段与顺序，并带上目标节点，所以
    /// 「追加一个动作」「换一批动作」「顺序变了」都会算出不同的值。
    /// </summary>
    private static string ActionFingerprint(IEnumerable<AgentAction> pending, string? targetNodeId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (targetNodeId ?? string.Empty) + "\n" + JsonSerializer.Serialize(pending.ToArray()))));

    internal static IResult CommitMessage(Auth.WebUser? user, bool canEdit, WebChatMessage message, bool undo, CanvasEventHub hub)
    {
        if (user is null || user.Disabled || !canEdit) return Error(403, "ACTION_FORBIDDEN", "缺少画布编辑权限");
        if (canvasStore is null || leases is null) return Error(409, "ACTION_PROJECT_REQUIRED", "动作需要项目画布");
        if (message.Mode == "readonly") return Error(403, "ACTION_READ_ONLY", "只读提议不能提交");
        if (undo ? message.ActionState != "committed" : message.ActionState != "preview") return Error(409, "ACTION_STATE_INVALID", "提议状态不允许此操作");
        if (!undo && message.PreviewFingerprint != ActionFingerprint(PendingActions(message), message.TargetNodeId))
            return Error(409, "ACTION_PREVIEW_REQUIRED", "动作已变化，请重新预检后再提交");
        return leases.WithTreeTransaction<IResult>(user, () =>
        {
            try
            {
                byte[] bytes; byte[]? before = null; long revision;
                var entityIds = message.Actions.Where(a => a.Kind == "attach_asset" && Guid.TryParse(a.EntityTarget, out _)).Select(a => Guid.Parse(a.EntityTarget)).Distinct().ToArray();
                var expectedLibrary = entityIds.Length == 0 ? Array.Empty<byte>() : undo
                    ? message.CommittedEntityLibrary ?? throw new InvalidDataException("缺少实体撤销基准")
                    : canvasStore.EntityLibraryBytes();
                if (undo)
                {
                    if (message.UndoCanvas is null || message.CommittedRevision is null) return Error(409, "UNDO_UNAVAILABLE", "没有可撤销的提交");
                    bytes = message.UndoCanvas; revision = message.CommittedRevision.Value;
                }
                else
                {
                    var pending = PendingActions(message);
                    var preview = canvasStore.PreviewActions(pending, action => AttachmentJob(user, message, action));
                    if (message.BaseRevision != preview.Revision) return Error(409, "SCENE_REVISION_CONFLICT", "画布已变化，请重新生成提议");
                    if (preview.Errors.Length > 0) return Error(409, "ACTION_PRECHECK_FAILED", string.Join("；", preview.Errors));
                    bytes = preview.After; before = preview.Before; revision = preview.Revision;
                }
                var serial = canvasStore.LastCommit.Serial;
                var result = canvasStore.ReplaceChatCanvas(revision, bytes, entityIds, expectedLibrary, out var committedLibrary);
                if (canvasStore.LastCommit.Serial > serial)
                {
                    message.ActionState = undo ? "undone" : "committed";
                    message.UndoCanvas = undo ? null : message.UndoCanvas ?? before;
                    message.CommittedRevision = canvasStore.LastCommit.Revision;
                    if (entityIds.Length > 0) message.CommittedEntityLibrary = undo ? null : committedLibrary;
                    message.CommittedActionCount = undo ? 0 : message.Actions.Count;
                    // 这一版动作已经执行掉了，旧批准不能再被用来执行第二遍（撤销之后会由预检重新武装）。
                    message.PreviewFingerprint = null;
                    hub.CanvasChanged(canvasStore.LastCommit.Revision, null, user.DisplayName, undo ? "agent-undo" : "agent");
                }
                return result;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
            { return Error(409, "ACTION_FAILED", "动作无法安全执行，画布未提交"); }
        }, EditLeaseApi.Failure);
    }

    internal static void CompleteJob(YEEYEEYEE.Core.Job job, Auth.WebUser user, CanvasEventHub hub)
    {
        if (chatStore is null || !job.Inputs.TryGetValue("webCanvasPath", out var path) || WebProjectApi.RegisteredCanvas(path.GetString()!) is not { } mode) return;
        WebProjectApi.InCanvas(mode, () =>
        {
            var owner = WebProjectApi.ScopedOwner(user.Id.ToString());
            foreach (var listed in chatStore.List(owner).Where(s => s.Messages.Any(m => m.SkillState == "submitted" && m.SkillJobIds.LastOrDefault() == job.JobId)))
            {
                var key = owner + ":" + listed.Id;
                using var reservation = new CancellationTokenSource();
                if (!Running.TryAdd(key, reservation)) continue;
                try
                {
                    var session = chatStore.Get(owner, listed.Id);
                    if (session is null || !session.Messages.Any(m => m.SkillState == "submitted" && m.SkillJobIds.LastOrDefault() == job.JobId)) continue;
                    foreach (var message in session.Messages.Where(m => m.SkillState == "submitted" && m.SkillJobIds.LastOrDefault() == job.JobId))
                    {
                        message.SkillState = job.State == YEEYEEYEE.Core.JobState.Succeeded ? "completed" : "failed";
                        message.SkillError = job.ErrorMessage;
                        if (job.State != YEEYEEYEE.Core.JobState.Succeeded) continue;
                        if (message.TargetNodeId is null || message.ActionState is "discarded" or "undone") continue;
                        if (message.ActionState == "committed") message.BaseRevision = message.CommittedRevision;
                        foreach (var output in job.Outputs)
                            message.Actions.Add(new AgentAction { Kind = "attach_asset", Target = message.TargetNodeId, Source = job.JobId.ToString(), Content = output.Ref, Title = "生成媒体", EntityTarget = message.SkillActions.Single().EntityTarget, VariantTarget = message.SkillActions.Single().VariantTarget });
                        if (job.Outputs.Count == 0) continue;
                        message.ActionState = message.Mode == "readonly" ? "none" : "preview";
                        // 动作集合刚刚被追加过了：旧批准立刻失效，必须按新动作重新预检
                        // （自动提交在下面自己把指纹记成当前这一批，人工批准则要用户再点一次预检）。
                        message.PreviewFingerprint = null;
                        try
                        {
                            var pending = PendingActions(message);
                            var preview = canvasStore!.PreviewActions(pending, action => AttachmentJob(user, message, action));
                            message.Hints = preview.Hints.Select(h => h ?? "").ToArray(); message.ActionErrors = preview.Errors;
                            if (message.Mode == "auto" && message.Ask is null && preview.Errors.Length == 0)
                            {
                                message.PreviewFingerprint = ActionFingerprint(pending, message.TargetNodeId);
                                var result = CommitMessage(user, Auth.UserRoles.ClaimsFor(user.Role).Contains("canvas.edit"), message, false, hub);
                                if (result is IStatusCodeHttpResult status && status.StatusCode >= 400)
                                    message.ActionErrors = new[] { JsonSerializer.Serialize((result as IValueHttpResult)?.Value) };
                            }
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
                        { message.PreviewFingerprint = null; message.ActionErrors = new[] { "生成产物无法安全回挂，请检查目标节点与项目资产" }; }
                    }
                    chatStore.Save(owner, session);
                }
                finally { Running.TryRemove(key, out _); }
            }
            return true;
        });
    }

    public static void Map(WebApplication app, Func<string, HttpContext, JsonElement, Task<IResult>> invokeSkill)
    {
        var path = LegacyConfig.Text(app.Configuration, "ChatDatabasePath") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LegacyConfig.Text(app.Configuration, "UserDatabasePath") ?? Path.Combine(AppContext.BaseDirectory, "yeeeyee.users.db")))!, "yeeeyee.chat.db");
        var store = chatStore = new WebChatStore(path);
        execution = app.Services.GetRequiredService<YEEYEEYEE.Host.SingleMachineExecutionService>();
        app.MapPost("/api/web/chat/sessions/{id}/attachments", async (HttpContext c, string id) =>
        {
            if (Owner(c) is not { } owner || store.Get(owner, id) is null) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            if (!WebAccessGuard.Permissions(c).Contains("skill.invoke")) return Error(403, "CHAT_FORBIDDEN", "没有附件上传权限");
            if (c.Request.ContentLength is null or > 5242880) return Error(413, "ATTACHMENT_TOO_LARGE", "附件最多5MiB，须提供长度");
            var type = c.Request.ContentType?.Split(';')[0];
            if (type is not ("image/png" or "image/jpeg" or "text/plain")) return Error(415, "ATTACHMENT_TYPE_INVALID", "仅支持PNG、JPEG与UTF-8纯文本");
            using var data = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = await c.Request.Body.ReadAsync(buffer, c.RequestAborted)) > 0)
            {
                if (data.Length + read > 5242880) return Error(413, "ATTACHMENT_TOO_LARGE", "附件最多5MiB");
                await data.WriteAsync(buffer.AsMemory(0, read), c.RequestAborted);
            }
            var bytes = data.ToArray();
            if (bytes.Length == 0) return Error(400, "ATTACHMENT_EMPTY", "附件不能为空");
            if (type == "image/png" && !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || type == "image/jpeg" && (bytes.Length < 3 || bytes[0] != 255 || bytes[1] != 216 || bytes[2] != 255))
                return Error(415, "ATTACHMENT_TYPE_INVALID", "图片签名与类型不符");
            if (type == "text/plain")
            {
                if (bytes.Length > 65536) return Error(413, "ATTACHMENT_TOO_LARGE", "文本附件最多64KiB");
                try { if (new System.Text.UTF8Encoding(false, true).GetString(bytes).Contains('\0')) return Error(415, "ATTACHMENT_TYPE_INVALID", "文本不能包含二进制数据"); }
                catch (System.Text.DecoderFallbackException) { return Error(415, "ATTACHMENT_TYPE_INVALID", "文本必须为UTF-8"); }
            }
            var name = Path.GetFileName(c.Request.Query["name"].ToString());
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200) return Error(400, "ATTACHMENT_NAME_INVALID", "文件名须为1至200字");
            try { var attachment = store.SaveAttachment(owner, id, name, type, bytes); return Results.Json(new { attachment.Id, attachment.Name, attachment.Type, size = bytes.Length }); }
            catch (InvalidOperationException ex) { return Error(409, "ATTACHMENT_LIMIT", ex.Message); }
        });
        app.MapGet("/api/web/chat/sessions/{id}/attachments/{attachmentId}", (HttpContext c, string id, string attachmentId) =>
            Attachment(c, id, attachmentId) is { } attachment ? Results.Bytes(attachment.Data, attachment.Type) : Error(404, "ATTACHMENT_NOT_FOUND", "附件不存在"));

        var hub = app.Services.GetRequiredService<CanvasEventHub>();
        async Task<IResult> StartSkill(HttpContext c, string owner, WebChatSession session, WebChatMessage message)
        {
            if (message.SkillState is not ("preview" or "failed")) return Error(409, "SKILL_STATE_INVALID", "技能提议状态不允许发起");
            if (message.SkillActions.Count != 1) return Error(400, "SKILL_BATCH_UNSUPPORTED", "每条回复仅支持一个技能任务，请拆分请求");
            var action = message.SkillActions[0];
            if (action.UnsupportedSkillFields.Count > 0 || action.Target != "web.text-to-video" && (action.Seconds is not null || action.Width is not null || action.Height is not null) || !string.IsNullOrEmpty(action.Source) || !string.IsNullOrEmpty(action.Path) || action.EntityTargets.Count > 0)
            {
                message.SkillState = "blocked"; message.SkillError = "SKILL_INPUT_UNSUPPORTED：Web聊天技能不支持该文件或实体引用参数";
                store.Save(owner, session); return Error(400, "SKILL_INPUT_UNSUPPORTED", message.SkillError);
            }
            var inputs = new Dictionary<string, object> { ["prompt"] = action.Content };
            if (message.TargetNodeId is { } nodeId)
            {
                inputs["targetNodeId"] = nodeId;
                if (action.Target is WebImageGeneration.SkillId or "comfyui.text-to-image")
                {
                    try
                    {
                        var target = canvasStore!.GenerationTarget(Guid.Parse(nodeId));
                        var output = ProjectCanvasSceneStore.OutputTarget(target.Canvas, target.Node, action.EntityTarget, action.VariantTarget);
                        action.EntityTarget = output?.Entity.ToString() ?? "";
                        action.VariantTarget = output?.Variant.ToString() ?? "";
                    }
                    catch (InvalidDataException ex) { return Error(409, "OUTPUT_TARGET_INVALID", ex.Message); }
                }
            }
            else if (!string.IsNullOrEmpty(action.EntityTarget) || !string.IsNullOrEmpty(action.VariantTarget))
                return Error(409, "OUTPUT_TARGET_INVALID", "设定图输出需要发送时选中的目标节点");
            c.Items["webChatGeneration"] = true;

            if (action.Target is WebImageGeneration.SkillId or "comfyui.text-to-image")
            {
                if (WebImageGeneration.Size(action.AspectRatio) is null)
                    return Error(400, "IMAGE_RATIO_REQUIRED", "请先选择图片比例");
                inputs["aspectRatio"] = action.AspectRatio;
            }
            else if (action.Target == "web.text-to-video")
            {
                if (action.Seconds is { } seconds) inputs["seconds"] = seconds;
                if (action.Width is { } width) inputs["width"] = width;
                if (action.Height is { } height) inputs["height"] = height;
                if (!string.IsNullOrEmpty(action.AspectRatio))
                {
                    var size = action.AspectRatio switch
                    {
                        "16:9" => (Width: 1280, Height: 720),
                        "9:16" => (Width: 720, Height: 1280),
                        _ => (Width: 0, Height: 0)
                    };
                    if (size.Width == 0 || action.Width is { } requestedWidth && requestedWidth != size.Width || action.Height is { } requestedHeight && requestedHeight != size.Height)
                        return Error(400, "VIDEO_RATIO_INVALID", "视频比例须为16:9或9:16，且与尺寸一致");
                    inputs["width"] = size.Width; inputs["height"] = size.Height;
                }
            }
            else if (!string.IsNullOrEmpty(action.AspectRatio))
            {
                message.SkillState = "blocked"; message.SkillError = "SKILL_INPUT_UNSUPPORTED：该技能不支持图片比例参数";
                store.Save(owner, session); return Error(400, "SKILL_INPUT_UNSUPPORTED", message.SkillError);
            }
            var images = action.ReferenceAttachmentIds.ToArray();
            var available = session.Messages.TakeWhile(m => m.Id != message.Id).Where(m => m.Role == "user").SelectMany(m => m.AttachmentIds).ToHashSet();
            if (images.Any(a => !available.Contains(a))) return Error(400, "REFERENCE_INVALID", "参考图必须是本条回复之前的用户附件");
            if (images.Length > 0) { inputs["chatSessionId"] = session.Id; inputs["referenceAttachmentIds"] = images; }
            message.SkillIdempotencyKey = message.SkillState == "failed"
                ? "chat-" + Guid.NewGuid().ToString("N")
                : message.SkillIdempotencyKey ?? "chat-" + session.Id + "-" + message.Id;
            inputs["idempotencyKey"] = message.SkillIdempotencyKey;
            message.SkillState = "preview";
            store.Save(owner, session);
            var result = await invokeSkill(action.Target, c, JsonSerializer.SerializeToElement(inputs));
            var value = JsonSerializer.SerializeToElement((result as IValueHttpResult)?.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (value.TryGetProperty("jobId", out var jobId) && jobId.TryGetGuid(out var job))
            {
                if (!session.JobIds.Contains(job)) session.JobIds.Add(job);
                if (!message.SkillJobIds.Contains(job)) message.SkillJobIds.Add(job);
                message.SkillState = "submitted"; message.SkillError = null;
            }
            else
            {
                message.SkillState = "failed";
                message.SkillError = value.TryGetProperty("code", out var code) && value.TryGetProperty("message", out var error) ? code.GetString() + "：" + error.GetString() : "SKILL_SUBMIT_FAILED：任务提交未返回有效任务 ID";
            }
            store.Save(owner, session);
            return result;
        }
        app.MapPost("/api/web/chat/sessions/{id}/skills/{messageId}/invoke", async (HttpContext c, string id, string messageId, SkillInput body) =>
        {
            if (Owner(c) is not { } owner) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            using var reservation = new CancellationTokenSource(); var key = owner + ":" + id;
            if (!Running.TryAdd(key, reservation)) return Error(409, "CHAT_BUSY", "会话正在处理");
            try
            {
                var session = store.Get(owner, id); var message = session?.Messages.FirstOrDefault(m => m.Id == messageId && m.Role == "assistant");
                if (message is null) return Error(404, "ACTION_NOT_FOUND", "技能提议不存在");
                if (message.SkillState is not ("preview" or "failed")) return Error(409, "SKILL_STATE_INVALID", "技能提议状态不允许发起");
                if (message.SkillActions.Count == 1 && message.SkillActions[0].Target is WebImageGeneration.SkillId or "comfyui.text-to-image")
                {
                    if (WebImageGeneration.Size(body.AspectRatio) is null) return Error(400, "IMAGE_RATIO_REQUIRED", "请选择图片比例");
                    message.SkillActions[0].AspectRatio = body.AspectRatio!;
                }
                return await StartSkill(c, owner, session!, message);
            }
            finally { Running.TryRemove(key, out _); }
        });
        IResult Commit(HttpContext c, WebChatMessage message, bool undo) => CommitMessage(WebAccessGuard.CurrentUser(c), WebAccessGuard.Permissions(c).Contains("canvas.edit"), message, undo, hub);
        app.MapPost("/api/web/chat/sessions/{id}/actions/{messageId}/{operation}", (HttpContext c, string id, string messageId, string operation) =>
        {
            if (Owner(c) is not { } owner) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            using var reservation = new CancellationTokenSource(); var key = owner + ":" + id;
            if (!Running.TryAdd(key, reservation)) return Error(409, "CHAT_BUSY", "会话正在处理");
            try
            {
                var session = store.Get(owner, id); var message = session?.Messages.FirstOrDefault(m => m.Id == messageId && m.Role == "assistant");
                if (message is null) return Error(404, "ACTION_NOT_FOUND", "提议不存在");
                if (operation == "discard") { if (message.ActionState != "preview") return Error(409, "ACTION_STATE_INVALID", "只能丢弃未提交提议"); message.ActionState = "discarded"; message.PreviewFingerprint = null; store.Save(owner, session!); return Results.Json(session); }
                if (operation == "preview")
                {
                    var restore = message.ActionState == "undone";
                    var user = WebAccessGuard.CurrentUser(c)!;
                    if (restore && (user.Disabled || !WebAccessGuard.Permissions(c).Contains("canvas.edit"))) return Error(403, "ACTION_FORBIDDEN", "缺少画布编辑权限");
                    if (restore && message.Mode == "readonly") return Error(403, "ACTION_READ_ONLY", "只读提议不能恢复审批");
                    if (restore ? message.SkillState != "completed" || message.CommittedActionCount != 0 || message.TargetNodeId is null || message.Actions.Count == 0 || message.Actions.Any(a => a.Kind != "attach_asset" || a.Target != message.TargetNodeId) : message.ActionState != "preview")
                        return Error(409, "ACTION_STATE_INVALID", "只能预览未提交提议或已撤销的原生成附件");
                    if (canvasStore is null || restore && leases is null) return Error(409, "ACTION_PROJECT_REQUIRED", "需要项目画布");
                    IResult Preview()
                    {
                        var pending = PendingActions(message);
                        var preview = canvasStore.PreviewActions(pending, action => AttachmentJob(user, message, action));
                        // 预检的结论**无论成败都写回消息**：失败之后旧的 Hints / ActionErrors 与旧的批准资格
                        // 都不能留着——「上一次预检过了」不是这一批动作过得了的依据。
                        message.Hints = preview.Hints.Select(h => h ?? "").ToArray();
                        message.ActionErrors = preview.Errors;
                        message.PreviewFingerprint = null;
                        if (preview.Errors.Length > 0)
                        {
                            store.Save(owner, session!);
                            return Error(409, "ACTION_PRECHECK_FAILED", string.Join("；", preview.Errors));
                        }
                        if (restore)
                        {
                            message.BaseRevision = preview.Revision;
                            message.ActionState = "preview";
                        }
                        else if (message.BaseRevision != preview.Revision)
                        {
                            message.ActionErrors = new[] { "画布版本已变化，请重新生成提议" };
                            store.Save(owner, session!);
                            return Error(409, "SCENE_REVISION_CONFLICT", "画布已变化，请重新生成提议");
                        }
                        // 预检通过：**这一版动作**才是可以被批准的那一版（提交要拿指纹核对）。
                        message.PreviewFingerprint = ActionFingerprint(pending, message.TargetNodeId);
                        store.Save(owner, session!);
                        return Results.Json(new { preview.Revision, hints = preview.Hints, errors = preview.Errors, before = JsonSerializer.Deserialize<JsonElement>(preview.Before), after = JsonSerializer.Deserialize<JsonElement>(preview.After), sessionId = session!.Id, messageId = message.Id, diff = preview.Diff });
                    }
                    return restore ? leases!.WithTreeTransaction<IResult>(user, Preview, EditLeaseApi.Failure) : Preview();
                }
                if (operation is not ("commit" or "undo")) return Error(400, "ACTION_OPERATION_INVALID", "未知动作操作");
                var result = Commit(c, message, operation == "undo"); store.Save(owner, session!); return result;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
            {
                // 「预检没跑完」与「动作校验没过」是两件事，不能混成一件事：
                // · ACTION_PRECHECK_FAILED（见 Preview 里那条）= 试算跑完了、结论是这批动作不通过，
                //   结论写在 ActionErrors 上；
                // · 这里是**根本没跑完**（画布/产物读不了、数据坏掉这类）。所以不往 ActionErrors / Hints 里
                //   写东西——那是动作校验的字段，塞进去就等于把执行异常伪装成校验失败。
                // 但上一次预检留下的**批准资格**必须收掉：这批动作这次没有被重新核对过，
                // 留着指纹等于让用户按一份没人复核的动作提交（前端刷新后也会看到它已失效）。
                if (operation == "preview" && store.Get(owner, id) is { } failed
                    && failed.Messages.FirstOrDefault(m => m.Id == messageId && m.Role == "assistant") is { PreviewFingerprint: not null } stale)
                {
                    stale.PreviewFingerprint = null;
                    store.Save(owner, failed);
                }
                if (operation != "preview") return Error(409, "ACTION_FAILED", "动作无法安全执行，画布未提交");
                // 原因只说得出人话的那种（InvalidDataException 是我们自己抛的领域原因）；
                // IO / JSON 这类系统级异常不把原文摊给用户看。
                var detail = ex is InvalidDataException ? ex.Message : "服务端读取画布或产物时出错";
                return Error(409, "ACTION_PREVIEW_FAILED", $"预检没能跑完：{detail}。这批动作没有被重新核对，请重试。");
            }
            finally { Running.TryRemove(key, out _); }
        });
        // 只读：**待审批预览**里某个产物的字节。待提交的产物还没写进正式节点，
        // 正式那条按节点取产物的接口读不到它——所以这里按「这条消息这批待执行动作」重新试算一次。
        // 三道闸都在：会话归属（Owner + 消息属于这个会话）、预览仍然有效（还在 preview 状态）、
        // 资源来自这批动作的试算结果（由 store 侧判定）。不取编辑锁，也不查 canvas.edit——
        // 与正式那条产物接口同一档：看得见聊天会话的人才看得见它自己的待审批产物。
        app.MapGet("/api/web/chat/sessions/{id}/messages/{messageId}/preview/records/{recordId:guid}/attachments/{attachmentId:guid}/file",
            (HttpContext c, string id, string messageId, Guid recordId, Guid attachmentId) =>
        {
            if (Owner(c) is not { } owner) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            var session = store.Get(owner, id);
            var message = session?.Messages.FirstOrDefault(m => m.Id == messageId && m.Role == "assistant");
            if (message is null) return Error(404, "ACTION_NOT_FOUND", "提议不存在");
            // 只有还停在这批动作的审批预览上时才服务：已提交、已丢弃、已撤销的动作，
            // 它的产物要么已经进了正式画布（走正式接口），要么根本不会生效。
            if (message.ActionState != "preview") return Error(409, "ACTION_STATE_INVALID", "这条提议已不在待审批预览状态");
            if (canvasStore is null) return Error(409, "ACTION_PROJECT_REQUIRED", "动作需要项目画布");
            var user = WebAccessGuard.CurrentUser(c);
            if (user is null || user.Disabled) return Error(403, "ACTION_FORBIDDEN", "缺少画布权限");
            return canvasStore.PreviewAttachmentFile(PendingActions(message), action => AttachmentJob(user, message, action), recordId, attachmentId);
        });
        app.MapPost("/api/web/chat/sessions/{id}/jobs/{jobId:guid}", (HttpContext c, string id, Guid jobId) =>
        {
            if (Owner(c) is not { } owner) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            using var reservation = new CancellationTokenSource(); var key = owner + ":" + id;
            if (!Running.TryAdd(key, reservation)) return Error(409, "CHAT_BUSY", "会话正在处理");
            try
            {
                var session = store.Get(owner, id);
                var execution = app.Services.GetRequiredService<YEEYEEYEE.Host.SingleMachineExecutionService>();
                if (session is null || !execution.TryGet(jobId, out var job) || job!.UserId != WebAccessGuard.CurrentUser(c)!.Id || !WebJobAttachmentService.InCurrentCanvas(job) || job.Tool is not ("comfyui.text-to-image" or WebImageGeneration.SkillId or "web.text-to-video"))
                    return Error(404, "JOB_NOT_FOUND", "会话或任务不存在");
                if (!session.JobIds.Contains(jobId)) session.JobIds.Add(jobId);
                store.Save(owner, session); return Results.Json(session);
            }
            finally { Running.TryRemove(key, out _); }
        });
        app.MapGet("/api/web/chat/sessions", (HttpContext c) => Owner(c) is { } o ? Results.Json(store.List(o)) : Error(403, "CHAT_LOGIN_REQUIRED", "聊天需要账号登录"));
        app.MapPost("/api/web/chat/sessions", (HttpContext c) =>
        {
            if (Owner(c) is not { } o) return Error(403, "CHAT_LOGIN_REQUIRED", "聊天需要账号登录");
            var s = new WebChatSession(); store.Save(o, s); return Results.Json(s);
        });
        app.MapGet("/api/web/chat/sessions/{id}", (HttpContext c, string id) =>
            Owner(c) is { } o && store.Get(o, id) is { } s ? Results.Json(s) : Error(404, "CHAT_NOT_FOUND", "会话不存在"));
        app.MapDelete("/api/web/chat/sessions/{id}", (HttpContext c, string id) =>
        {
            if (Owner(c) is not { } o || store.Get(o, id) is null) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            var key = o + ":" + id;
            using var reservation = new CancellationTokenSource();
            if (!Running.TryAdd(key, reservation)) return Error(409, "CHAT_BUSY", "请先取消正在生成的回复");
            try { return store.Delete(o, id) ? Results.Json(new { deleted = true }) : Error(404, "CHAT_NOT_FOUND", "会话不存在"); }
            finally { Running.TryRemove(key, out _); }
        });
        app.MapPost("/api/web/chat/sessions/{id}/cancel", (HttpContext c, string id) =>
        {
            if (Owner(c) is not { } o || store.Get(o, id) is null) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            if (Running.TryGetValue(o + ":" + id, out var source)) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
            return Results.Json(new { cancelled = true });
        });
        app.MapPost("/api/web/chat/sessions/{id}/messages", async (HttpContext c, string id, SendRequest body, IHttpClientFactory factory) =>
        {
            if (Owner(c) is not { } o || store.Get(o, id) is not { } session) { await Error(404, "CHAT_NOT_FOUND", "会话不存在").ExecuteAsync(c); return; }
            if (!WebAccessGuard.Permissions(c).Contains("skill.invoke")) { await Error(403, "CHAT_FORBIDDEN", "当前账号没有模型调用权限").ExecuteAsync(c); return; }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, app.Lifetime.ApplicationStopping);
            cancellation.CancelAfter(TimeSpan.FromMinutes(5));
            var key = o + ":" + id;
            if (!Running.TryAdd(key, cancellation)) { await Error(409, "CHAT_BUSY", "会话正在生成回复").ExecuteAsync(c); return; }
            try
            {
                // 获取锁后重新读历史，避免并行请求读取旧快照。
                session = store.Get(o, id)!;
                if (session is null) { await Error(404, "CHAT_NOT_FOUND", "会话不存在").ExecuteAsync(c); return; }
                var config = WebSettingsApi.LoadModels();
                var selectedProfile = AiProviderSettings.ResolveSelected(config);
                if (config.UseLocalProvider || !config.IsConfigured || (selectedProfile?.ApiKeyUnreadable ?? config.ApiKeyUnreadable))
                { await Error(503, "CHAT_MODEL_UNAVAILABLE", "请由管理员在 Web 设置中新增或更新服务端模型；密钥须能由服务运行账号解密").ExecuteAsync(c); return; }
                WebChatMessage? retryMessage = null;
                if (body.Retry)
                {
                    if (session.Messages.LastOrDefault() is not { Role: "assistant", State: "failed" or "cancelled" or "interrupted" } failed)
                    { await Error(409, "CHAT_RETRY_INVALID", "只有最后一条失败或中断回复可以重试").ExecuteAsync(c); return; }
                    retryMessage = failed;
                    body = body with { Mode = failed.Mode, ChapterId = failed.TargetChapterId, NodeId = failed.TargetNodeId, BaseRevision = failed.BaseRevision, Source = failed.Source, TargetNodeTitle = failed.TargetNodeTitle };
                    session.Messages.RemoveAt(session.Messages.Count - 1);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > 16000) { await Error(400, "CHAT_TEXT_INVALID", "请输入1至16000字的消息").ExecuteAsync(c); return; }
                    var ids = body.AttachmentIds ?? Array.Empty<string>();
                    if (ids.Length > 8 || ids.Distinct().Count() != ids.Length || ids.Any(a => store.GetAttachment(o, id, a) is null)) { await Error(400, "ATTACHMENT_INVALID", "每条消息最多8个当前会话附件").ExecuteAsync(c); return; }
                    if (!config.SupportsImageInput && ids.Any(a => store.GetAttachment(o, id, a)!.Type.StartsWith("image/"))) { await Error(400, "CHAT_IMAGE_UNSUPPORTED", "当前模型不支持图片输入").ExecuteAsync(c); return; }
                    session.Messages.Add(new WebChatMessage { Content = body.Text.Trim(), AttachmentIds = ids.ToList(), TargetNodeId = body.NodeId, TargetChapterId = body.ChapterId, BaseRevision = body.BaseRevision, Mode = body.Mode, Source = body.Source, TargetNodeTitle = body.TargetNodeTitle });
                    if (session.Messages.Count == 1) session.Title = body.Text.Trim()[..Math.Min(40, body.Text.Trim().Length)];
                }
                if (body.Mode is not ("readonly" or "approval" or "auto")) { await Error(400, "CHAT_MODE_INVALID", "模式无效").ExecuteAsync(c); return; }
                if (body.Source == "node-menu" && (!WebAccessGuard.Permissions(c).Contains("canvas.edit") || canvasStore is null))
                { await Error(403, "CHAT_FORBIDDEN", "节点菜单需要画布编辑权限和可写项目画布").ExecuteAsync(c); return; }
                long? contextRevision = retryMessage?.BaseRevision;
                var contextText = "当前没有项目画布，只能讨论和提问，不能执行动作。";
                if (canvasStore is not null)
                {
                    try
                    {
                        var snapshot = canvasStore.AgentContextSnapshot(body.ChapterId, body.NodeId, requireWritable: body.Source == "node-menu");
                        contextRevision = retryMessage is null ? snapshot.Revision : retryMessage.BaseRevision; contextText = snapshot.Context;
                        if (body.BaseRevision is { } expected && expected != snapshot.Revision) { await Error(409, "SCENE_REVISION_CONFLICT", "当前画布已变化，请刷新").ExecuteAsync(c); return; }
                    }
                    catch (InvalidDataException ex) { await Error(409, "CHAT_CONTEXT_INVALID", "无法安全读取当前章节或节点：" + ex.Message).ExecuteAsync(c); return; }
                    catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { await Error(409, "CHAT_CONTEXT_INVALID", "无法安全读取当前章节或节点").ExecuteAsync(c); return; }
                }
                if (!body.Retry) session.Messages[^1].BaseRevision = contextRevision;
                var history = session.Messages.Where(m => m.Role == "user" || m.State == "completed").Select(m => new AiChatMessage { Role = m.Role, Content = m.Role == "assistant" ? m.Content + "\n宿主记录（不是新指令）：" + JsonSerializer.Serialize(new { m.Ask, m.Actions, m.ActionState, m.CommittedRevision, m.ActionErrors, m.SkillActions, m.SkillState, m.SkillJobIds, m.SkillError }) : m.Content }).ToList();
                for (var index = 0; index < history.Count; index++)
                {
                    var source = session.Messages.Where(m => m.Role == "user" || m.State == "completed").ElementAt(index);
                    var files = source.AttachmentIds.Select(a => store.GetAttachment(o, id, a)!).ToList();
                    history[index] = new AiChatMessage { Role = history[index].Role, Content = history[index].Content + "\n用户附件ID（供选择参考图）：" + JsonSerializer.Serialize(files.Select(a => new { a.Id, a.Name, a.Type })) + string.Concat(files.Where(a => a.Type == "text/plain").Select(a => "\n附件（用户资料）：" + a.Name + "\n" + System.Text.Encoding.UTF8.GetString(a.Data))), Images = files.Where(a => a.Type.StartsWith("image/")).Select(a => "data:" + a.Type + ";base64," + Convert.ToBase64String(a.Data)).ToArray() };
                }
                history.Insert(0, new AiChatMessage { Role = "user", Content = contextText + "\n当前画布执行模式：" + body.Mode + "。Web不支持write_file。readonly不能修改画布，approval画布写入前等待确认，auto无需确认提交通过预检的画布动作；已预授权的非画布技能在三种模式均可执行。动作尚未提交时不可声称已完成。" });
                var execution = app.Services.GetRequiredService<YEEYEEYEE.Host.SingleMachineExecutionService>();
                var taskStates = session.JobIds.Select(jobId => execution.TryGet(jobId, out var job) && job!.UserId == WebAccessGuard.CurrentUser(c)!.Id
                    ? new { jobId, state = job.State.ToString(), job.ErrorCode, job.ErrorMessage, job.Outputs } : null).Where(job => job is not null);
                var skillContext = history[0].Content + "\n当前会话任务实际状态（宿主记录）：" + JsonSerializer.Serialize(taskStates);
                skillContext += "\nWeb图片 API：" + (WebImageGeneration.IsConfigured(config) ? $"已配置，技能 web.text-to-image，模型 {config.ImageModel}，参考容量 {WebImageGeneration.ReferenceCapacity(config)}。" : "未配置，不可调用 web.text-to-image。") + "需保持人物、场景或道具外观时自主选参考图；纯创作可不带参考。需要参考但模型容量不足时必须提示用户选择支持参考图的模型，不得删掉参考继续生成。图片提议由宿主在聊天内请求用户选择比例后才提交。";
                skillContext += "\nWeb技能协议：调用技能时必须在回复末尾输出一个json代码块，顶层是包含actions数组的对象，完整示例：{\"actions\":[{\"kind\":\"invoke_skill\",\"target\":\"web.text-to-image\",\"content\":\"完整提示词\",\"reason\":\"理由\",\"aspectRatio\":\"1:1\",\"referenceAttachmentIds\":[]}]}。不得输出裸动作对象或顶层数组。target必须是一个确切技能ID：web.text-to-image、comfyui.text-to-image或web.text-to-video。一次一个。图片aspectRatio最终以用户在聊天内选择为准；视频web.text-to-video支持seconds整数秒、width与height整数尺寸，或aspectRatio为16:9（1280x720）或9:16（720x1280），须按用户要求明确传递，如15秒传seconds:15。视频可用referenceAttachmentIds选择一张已上传图片作首帧；绑定节点未指定附件时宿主使用当前分镜采用图片作首帧，失效或缺失的必要资产会阻止调用。referenceAttachmentIds由你自主选择用户已上传的图片附件ID，不需要参考时为空；宿主不会自动传全部附件。生成成功后宿主会把媒体附件动作追加到本条回复的所选节点：readonly只沟通不写入，approval等待画布审批，auto通过同样的权限、锁、预检与修订校验后自动提交。附件动作由宿主根据真实产物创建，不要自行编造attach_asset或声称未提交产物已回挂；设定图可提供entityTarget和variantTarget完整ID作为输出目标，必须绑定发送时的选中节点；设定节点仅有唯一对应引用时宿主可解析，否则用ask请求明确绑定，不得按标题猜测。实体图槽与节点附件独立，节点有图不表示实体有图。分镜必需参考由服务端从绑定实体变体解析并全部提交，缺图、失效版本或容量不足会阻止创建任务，不得以纯文本降级。不支持任意路径或外部网络参考。已预授权且有skill.invoke权限的非画布技能在readonly、approval、auto中均可执行；服务端仍逐次检查预授权，未获授权返回SKILL_NOT_APPROVED，未知技能返回SKILL_NOT_FOUND。宿主返回任务ID仅表示已提交，只有任务Succeeded且有产物才能说生成完成。";
                skillContext += "\n调用技能前必须先在本次回复中完整输出可解析且非空的动作协议，包含实际invoke_skill动作及必需参数；不得仅声明正在调用技能，不得输出空json代码块或空对象。若还缺少必要信息，输出有效ask而非声称任务已发起。只有宿主返回任务ID后才能说任务已提交。";
                history[0] = new AiChatMessage { Role = "user", Content = skillContext };
                var reply = new WebChatMessage { Id = retryMessage?.Id ?? Guid.NewGuid().ToString("N"), Role = "assistant", State = "streaming", Model = config.Model, ContextWindow = config.ContextWindow, Mode = body.Mode, BaseRevision = contextRevision, TargetNodeId = body.NodeId, TargetChapterId = body.ChapterId, Source = body.Source, TargetNodeTitle = body.TargetNodeTitle };
                session.Messages.Add(reply); store.Save(o, session);
                c.Response.ContentType = "text/event-stream"; c.Response.Headers.CacheControl = "no-cache"; c.Response.Headers["X-Accel-Buffering"] = "no";
                var events = Channel.CreateUnbounded<(string Type, string Data)>();
                void Publish(string type, object data) => events.Writer.TryWrite((type, JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
                Publish("snapshot", session);
                async Task Generate()
                {
                    var checkpoint = DateTime.UtcNow;
                    void SaveProgress() { if (DateTime.UtcNow - checkpoint < TimeSpan.FromMilliseconds(250)) return; store.Save(o, session); checkpoint = DateTime.UtcNow; }
                    try
                    {
                        var provider = new OpenAiCompatibleProvider(config, factory.CreateClient("chat"));
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        object UsageSnapshot() => new { usage = reply.Usage, reply.ContextWindow, reply.FirstTokenSeconds, reply.DecodeSeconds, reply.UsageReport };
                        void ObserveOutput(bool thinking)
                        {
                            var first = reply.FirstTokenSeconds is null;
                            reply.ObserveOutput(clock.Elapsed.TotalSeconds, thinking);
                            if (first) Publish("usage", UsageSnapshot());
                        }
                        reply.Content = await provider.ChatStreamAsync(history, new AiStreamSink(
                            text => { ObserveOutput(thinking: false); reply.Content += text; Publish("text", new { text }); SaveProgress(); },
                            thinking => { ObserveOutput(thinking: true); reply.Thinking += thinking; Publish("thinking", new { text = thinking }); SaveProgress(); },
                            usage => { reply.Usage = usage; Publish("usage", UsageSnapshot()); SaveProgress(); }), cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        var responseLength = reply.Content.Length;
                        var parsed = AgentActionParser.Parse(reply.Content);
                        var emptyProtocol = parsed.Actions.Count == 0 && parsed.Ask is null ? EmptyJsonTail.Match(reply.Content) : null;
                        var protocolFailed = parsed.ProtocolBroken || emptyProtocol?.Success == true;
                        app.Logger.LogInformation("Web聊天协议解析：{ResponseLength} {ActionCount} {ProtocolFailed}", responseLength, parsed.Actions.Count, protocolFailed);
                        reply.Content = emptyProtocol?.Success == true ? reply.Content[..emptyProtocol.Index].Trim() : parsed.Text; reply.Ask = parsed.Ask;
                        reply.SkillActions = parsed.Actions.Where(a => a.Kind == "invoke_skill").ToList();
                        reply.Actions = parsed.Actions.Where(a => a.Kind != "invoke_skill").ToList();
                        if (protocolFailed) { reply.State = "failed"; reply.Error = "动作协议为空或无效，未发起任务，请重试"; }
                        else
                        {
                            reply.State = "completed";
                            if (reply.SkillActions.Count > 0)
                            {
                                reply.SkillState = "preview";
                                if (reply.SkillActions.Count != 1)
                                { reply.SkillState = "blocked"; reply.SkillError = "SKILL_BATCH_UNSUPPORTED：每条回复仅支持一个生成技能"; }
                                else if (reply.Ask is null && reply.SkillActions[0].Target is not WebImageGeneration.SkillId and not "comfyui.text-to-image")
                                { cancellation.Token.ThrowIfCancellationRequested(); await StartSkill(c, o, session, reply); }
                            }
                            if (reply.Actions.Count > 0)
                            {
                                reply.ActionState = "preview";
                                try
                                {
                                    if (canvasStore is null) throw new InvalidDataException("动作需要项目画布");
                                    var preview = canvasStore.PreviewActions(reply.Actions, action => AttachmentJob(WebAccessGuard.CurrentUser(c)!, reply, action));
                                    reply.Hints = preview.Hints.Select(h => h ?? "").ToArray(); reply.ActionErrors = preview.Errors;
                                    if (preview.Revision != reply.BaseRevision) reply.ActionErrors = new[] { "画布版本已变化，请重新生成提议" };
                                    // 这一版动作通过了预检：记下指纹，批准/自动提交都要拿它核对。
                                    reply.PreviewFingerprint = reply.ActionErrors.Length == 0 ? ActionFingerprint(reply.Actions, reply.TargetNodeId) : null;
                                    if (reply.Mode == "auto" && reply.Ask is null && reply.ActionErrors.Length == 0)
                                    {
                                        cancellation.Token.ThrowIfCancellationRequested();
                                        Commit(c, reply, false);
                                        if (reply.ActionState != "committed") reply.ActionErrors = new[] { "自动提交被锁、权限、版本或校验阻止，请检查后重试审批" };
                                    }
                                }
                                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
                                { reply.ActionErrors = new[] { "ACTION_PRECHECK_FAILED：" + (ex is InvalidDataException ? ex.Message : "此批动作不能安全执行") }; }
                            }
                        }
                    }
                    catch (OperationCanceledException) { reply.State = "cancelled"; reply.Error = "生成已取消、连接中断或请求超时，请重试"; }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or JsonException)
                    { reply.State = "failed"; reply.Error = "模型请求失败，请检查服务端模型配置或稍后重试"; app.Logger.LogWarning("Web聊天失败：{Type}", ex.GetType().Name); }
                    finally { store.Save(o, session); Publish("done", session); events.Writer.TryComplete(); }
                }
                var generation = Generate();
                try
                {
                    await foreach (var e in events.Reader.ReadAllAsync(c.RequestAborted))
                    {
                        await c.Response.WriteAsync($"event: {e.Type}\ndata: {e.Data}\n\n", c.RequestAborted);
                        await c.Response.Body.FlushAsync(c.RequestAborted);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { cancellation.Cancel(); }
                finally { await generation; }
            }
            finally { Running.TryRemove(key, out _); }
        });
    }
}
