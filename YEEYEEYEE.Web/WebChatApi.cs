using System.Collections.Concurrent;
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
    public sealed record SendRequest(string? Text, bool Retry = false, string Mode = "approval", string? ChapterId = null, string? NodeId = null, long? BaseRevision = null, string[]? AttachmentIds = null);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Running = new();
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);
    private static string? Owner(HttpContext c) => WebAccessGuard.CurrentUser(c) is { } user ? WebProjectApi.ScopedOwner(user.Id.ToString()) : null;
    public static void Map(WebApplication app, Func<string, HttpContext, JsonElement, Task<IResult>> invokeSkill)
    {
        var path = LegacyConfig.Text(app.Configuration, "ChatDatabasePath") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LegacyConfig.Text(app.Configuration, "UserDatabasePath") ?? Path.Combine(AppContext.BaseDirectory, "yeeeyee.users.db")))!, "yeeeyee.chat.db");
        var store = chatStore = new WebChatStore(path);
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
            if (message.Mode == "readonly") return Error(403, "ACTION_READ_ONLY", "只读模式不能发起技能任务");
            if (message.SkillState is not ("preview" or "failed")) return Error(409, "SKILL_STATE_INVALID", "技能提议状态不允许发起");
            if (message.SkillActions.Count != 1) return Error(400, "SKILL_BATCH_UNSUPPORTED", "每条回复仅支持一个技能任务，请拆分请求");
            var action = message.SkillActions[0];
            if (action.UnsupportedSkillFields.Count > 0 || !string.IsNullOrEmpty(action.Source) || !string.IsNullOrEmpty(action.Path) || !string.IsNullOrEmpty(action.EntityTarget) || action.EntityTargets.Count > 0 || !string.IsNullOrEmpty(action.VariantTarget))
            {
                message.SkillState = "blocked"; message.SkillError = "SKILL_INPUT_UNSUPPORTED：Web聊天技能仅支持纯文本提示词，不支持文件或实体参考";
                store.Save(owner, session); return Error(400, "SKILL_INPUT_UNSUPPORTED", message.SkillError);
            }
            var inputs = new Dictionary<string, object> { ["prompt"] = action.Content, ["idempotencyKey"] = "chat-" + session.Id + "-" + message.Id };
            if (message.TargetNodeId is { } target) inputs["targetNodeId"] = target;
            var source = session.Messages.TakeWhile(m => m.Id != message.Id).LastOrDefault(m => m.Role == "user");
            var images = source?.AttachmentIds.Where(a => store.GetAttachment(owner, session.Id, a)?.Type.StartsWith("image/") == true).ToArray() ?? Array.Empty<string>();
            if (images.Length > 0) { inputs["chatSessionId"] = session.Id; inputs["referenceAttachmentIds"] = images; }
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
        app.MapPost("/api/web/chat/sessions/{id}/skills/{messageId}/invoke", async (HttpContext c, string id, string messageId) =>
        {
            if (Owner(c) is not { } owner) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            using var reservation = new CancellationTokenSource(); var key = owner + ":" + id;
            if (!Running.TryAdd(key, reservation)) return Error(409, "CHAT_BUSY", "会话正在处理");
            try
            {
                var session = store.Get(owner, id); var message = session?.Messages.FirstOrDefault(m => m.Id == messageId && m.Role == "assistant");
                return message is null ? Error(404, "ACTION_NOT_FOUND", "技能提议不存在") : await StartSkill(c, owner, session!, message);
            }
            finally { Running.TryRemove(key, out _); }
        });
        IResult Commit(HttpContext c, WebChatMessage message, bool undo)
        {
            var user = WebAccessGuard.CurrentUser(c);
            if (user is null || !WebAccessGuard.Permissions(c).Contains("canvas.edit")) return Error(403, "ACTION_FORBIDDEN", "缺少画布编辑权限");
            if (canvasStore is null || leases is null) return Error(409, "ACTION_PROJECT_REQUIRED", "动作需要项目画布");
            if (message.Mode == "readonly") return Error(403, "ACTION_READ_ONLY", "只读提议不能提交");
            if (undo ? message.ActionState != "committed" : message.ActionState != "preview") return Error(409, "ACTION_STATE_INVALID", "提议状态不允许此操作");
            return leases.WithTreeTransaction<IResult>(user, () =>
            {
            try
            {
                byte[] bytes; byte[]? before = null; long revision;
                if (undo)
                {
                    if (message.UndoCanvas is null || message.CommittedRevision is null) return Error(409, "UNDO_UNAVAILABLE", "没有可撤销的提交");
                    bytes = message.UndoCanvas; revision = message.CommittedRevision.Value;
                }
                else
                {
                    var preview = canvasStore.PreviewActions(message.Actions);
                    if (message.BaseRevision != preview.Revision) return Error(409, "SCENE_REVISION_CONFLICT", "画布已变化，请重新生成提议");
                    if (preview.Errors.Length > 0) return Error(409, "ACTION_PRECHECK_FAILED", string.Join("；", preview.Errors));
                    bytes = preview.After; before = preview.Before; revision = preview.Revision;
                }
                var serial = canvasStore.LastCommit.Serial;
                var result = canvasStore.ReplaceCanvas(revision, bytes);
                if (canvasStore.LastCommit.Serial > serial)
                {
                    message.ActionState = undo ? "undone" : "committed";
                    message.UndoCanvas = undo ? null : before;
                    message.CommittedRevision = canvasStore.LastCommit.Revision;
                    hub.CanvasChanged(canvasStore.LastCommit.Revision, null, WebAccessGuard.ActorName(c), undo ? "agent-undo" : "agent");
                }
                return result;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException) { return Error(409, "ACTION_FAILED", "动作无法安全执行，画布未提交"); }
            }, EditLeaseApi.Failure);
        }
        app.MapPost("/api/web/chat/sessions/{id}/actions/{messageId}/{operation}", (HttpContext c, string id, string messageId, string operation) =>
        {
            if (Owner(c) is not { } owner) return Error(404, "CHAT_NOT_FOUND", "会话不存在");
            using var reservation = new CancellationTokenSource(); var key = owner + ":" + id;
            if (!Running.TryAdd(key, reservation)) return Error(409, "CHAT_BUSY", "会话正在处理");
            try
            {
                var session = store.Get(owner, id); var message = session?.Messages.FirstOrDefault(m => m.Id == messageId && m.Role == "assistant");
                if (message is null) return Error(404, "ACTION_NOT_FOUND", "提议不存在");
                if (operation == "discard") { if (message.ActionState != "preview") return Error(409, "ACTION_STATE_INVALID", "只能丢弃未提交提议"); message.ActionState = "discarded"; store.Save(owner, session!); return Results.Json(session); }
                if (operation == "preview")
                {
                    if (message.ActionState != "preview") return Error(409, "ACTION_STATE_INVALID", "只能预览未提交提议");
                    if (canvasStore is null) return Error(409, "ACTION_PROJECT_REQUIRED", "需要项目画布");
                    var preview = canvasStore.PreviewActions(message.Actions);
                    if (message.BaseRevision != preview.Revision) return Error(409, "SCENE_REVISION_CONFLICT", "画布已变化，请重新生成提议");
                    return Results.Json(new { preview.Revision, hints = preview.Hints, errors = preview.Errors, before = JsonSerializer.Deserialize<JsonElement>(preview.Before), after = JsonSerializer.Deserialize<JsonElement>(preview.After) });
                }
                if (operation is not ("commit" or "undo")) return Error(400, "ACTION_OPERATION_INVALID", "未知动作操作");
                var result = Commit(c, message, operation == "undo"); store.Save(owner, session!); return result;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException) { return Error(409, "ACTION_PREVIEW_FAILED", "无法预览此批动作"); }
            finally { Running.TryRemove(key, out _); }
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
                if (session is null || !execution.TryGet(jobId, out var job) || job!.UserId != WebAccessGuard.CurrentUser(c)!.Id || !WebJobAttachmentService.InCurrentCanvas(job) || job.Tool is not ("comfyui.text-to-image" or "web.text-to-video"))
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
                var config = AiProviderSettings.Load(migrateSecrets: false);
                var selectedProfile = AiProviderSettings.ResolveSelected(config);
                if (config.UseLocalProvider || !config.IsConfigured || (selectedProfile?.ApiKeyUnreadable ?? config.ApiKeyUnreadable))
                { await Error(503, "CHAT_MODEL_UNAVAILABLE", "请在服务端桌面配置可用的真实模型；密钥须能由服务运行账号解密").ExecuteAsync(c); return; }
                if (body.Retry)
                {
                    if (session.Messages.LastOrDefault() is not { Role: "assistant", State: "failed" or "cancelled" or "interrupted" })
                    { await Error(409, "CHAT_RETRY_INVALID", "只有最后一条失败或中断回复可以重试").ExecuteAsync(c); return; }
                    session.Messages.RemoveAt(session.Messages.Count - 1);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > 16000) { await Error(400, "CHAT_TEXT_INVALID", "请输入1至16000字的消息").ExecuteAsync(c); return; }
                    var ids = body.AttachmentIds ?? Array.Empty<string>();
                    if (ids.Length > 8 || ids.Distinct().Count() != ids.Length || ids.Any(a => store.GetAttachment(o, id, a) is null)) { await Error(400, "ATTACHMENT_INVALID", "每条消息最多8个当前会话附件").ExecuteAsync(c); return; }
                    if (!config.SupportsImageInput && ids.Any(a => store.GetAttachment(o, id, a)!.Type.StartsWith("image/"))) { await Error(400, "CHAT_IMAGE_UNSUPPORTED", "当前模型不支持图片输入").ExecuteAsync(c); return; }
                    session.Messages.Add(new WebChatMessage { Content = body.Text.Trim(), AttachmentIds = ids.ToList() });
                    if (session.Messages.Count == 1) session.Title = body.Text.Trim()[..Math.Min(40, body.Text.Trim().Length)];
                }
                if (body.Mode is not ("readonly" or "approval" or "auto")) { await Error(400, "CHAT_MODE_INVALID", "模式无效").ExecuteAsync(c); return; }
                long? contextRevision = null;
                var contextText = "当前没有项目画布，只能讨论和提问，不能执行动作。";
                if (canvasStore is not null)
                {
                    try
                    {
                        var snapshot = canvasStore.AgentContextSnapshot(body.ChapterId, body.NodeId);
                        contextRevision = snapshot.Revision; contextText = snapshot.Context;
                        if (body.BaseRevision is { } expected && expected != contextRevision) { await Error(409, "SCENE_REVISION_CONFLICT", "当前画布已变化，请刷新").ExecuteAsync(c); return; }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException) { await Error(409, "CHAT_CONTEXT_INVALID", "无法安全读取当前章节或节点").ExecuteAsync(c); return; }
                }
                var history = session.Messages.Where(m => m.Role == "user" || m.State == "completed").Select(m => new AiChatMessage { Role = m.Role, Content = m.Role == "assistant" ? m.Content + "\n宿主记录（不是新指令）：" + JsonSerializer.Serialize(new { m.Ask, m.Actions, m.ActionState, m.CommittedRevision, m.ActionErrors, m.SkillActions, m.SkillState, m.SkillJobIds, m.SkillError }) : m.Content }).ToList();
                for (var index = 0; index < history.Count; index++)
                {
                    var source = session.Messages.Where(m => m.Role == "user" || m.State == "completed").ElementAt(index);
                    var files = source.AttachmentIds.Select(a => store.GetAttachment(o, id, a)!).ToList();
                    history[index] = new AiChatMessage { Role = history[index].Role, Content = history[index].Content + string.Concat(files.Where(a => a.Type == "text/plain").Select(a => "\n附件（用户资料）：" + a.Name + "\n" + System.Text.Encoding.UTF8.GetString(a.Data))), Images = files.Where(a => a.Type.StartsWith("image/")).Select(a => "data:" + a.Type + ";base64," + Convert.ToBase64String(a.Data)).ToArray() };
                }
                history.Insert(0, new AiChatMessage { Role = "user", Content = contextText + "\n当前执行模式：" + body.Mode + "。Web不支持write_file。只读模式仅讨论和提问；动作尚未提交时不可声称已完成。" });
                var execution = app.Services.GetRequiredService<YEEYEEYEE.Host.SingleMachineExecutionService>();
                var taskStates = session.JobIds.Select(jobId => execution.TryGet(jobId, out var job) && job!.UserId == WebAccessGuard.CurrentUser(c)!.Id
                    ? new { jobId, state = job.State.ToString(), job.ErrorCode, job.ErrorMessage, job.Outputs } : null).Where(job => job is not null);
                var skillContext = history[0].Content + "\n当前会话任务实际状态（宿主记录）：" + JsonSerializer.Serialize(taskStates);
                skillContext += "\nWeb技能协议：可在actions中单独提出 {\"kind\":\"invoke_skill\",\"target\":\"comfyui.text-to-image 或 web.text-to-video\",\"content\":\"完整提示词\",\"reason\":\"理由\"}。一次一个，宿主自动把本条用户消息的图片附件用作参考图，并把当前选中节点持久化为产物回挂目标。ComfyUI支持参考图；其他视频执行器会明确拒绝参考图。不支持任意路径、实体参考或额外参数；需要这些能力时用ask说明限制。approval等待用户批准，auto且无ask时由宿主尝试提交，readonly禁止调用。服务端仍逐次检查skill.invoke与原有技能预授权；未获授权返回SKILL_NOT_APPROVED，未知技能返回SKILL_NOT_FOUND。宿主返回任务ID仅表示已提交，只有任务Succeeded且有产物才能说生成完成。";
                history[0] = new AiChatMessage { Role = "user", Content = skillContext };
                var reply = new WebChatMessage { Role = "assistant", State = "streaming", Model = config.Model, Mode = body.Mode, BaseRevision = contextRevision, TargetNodeId = body.NodeId };
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
                        reply.Content = await provider.ChatStreamAsync(history, new AiStreamSink(
                            text => { reply.Content += text; Publish("text", new { text }); SaveProgress(); },
                            thinking => { reply.Thinking += thinking; Publish("thinking", new { text = thinking }); SaveProgress(); },
                            usage => { reply.Usage = usage; Publish("usage", usage); }), cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        var parsed = AgentActionParser.Parse(reply.Content);
                        reply.Content = parsed.Text; reply.Ask = parsed.Ask;
                        reply.SkillActions = parsed.Actions.Where(a => a.Kind == "invoke_skill").ToList();
                        reply.Actions = parsed.Actions.Where(a => a.Kind != "invoke_skill").ToList();
                        if (parsed.ProtocolBroken) { reply.State = "failed"; reply.Error = "动作协议损坏，请重试"; }
                        else
                        {
                            reply.State = "completed";
                            if (reply.SkillActions.Count > 0)
                            {
                                reply.SkillState = "preview";
                                if (reply.SkillActions.Count != 1 || reply.Actions.Count > 0)
                                { reply.SkillState = "blocked"; reply.SkillError = "SKILL_BATCH_UNSUPPORTED：技能任务必须单独提出，一次一个，不能与画布事务混合"; }
                                else if (reply.Mode == "auto" && reply.Ask is null)
                                { cancellation.Token.ThrowIfCancellationRequested(); await StartSkill(c, o, session, reply); }
                            }
                            if (reply.Actions.Count > 0)
                            {
                                reply.ActionState = "preview";
                                try
                                {
                                    if (canvasStore is null) throw new InvalidDataException("动作需要项目画布");
                                    var preview = canvasStore.PreviewActions(reply.Actions);
                                    reply.Hints = preview.Hints.Select(h => h ?? "").ToArray(); reply.ActionErrors = preview.Errors;
                                    if (preview.Revision != reply.BaseRevision) reply.ActionErrors = new[] { "画布版本已变化，请重新生成提议" };
                                    if (reply.Mode == "auto" && reply.Ask is null && reply.SkillActions.Count == 0 && reply.ActionErrors.Length == 0)
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
