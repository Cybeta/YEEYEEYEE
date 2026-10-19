using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

// 仅暴露服务端批准的技能和已安装的本地工作流；
// 不接受客户端传入的工作流图或工具定义。
internal static class WebSkillJobApi
{
    private const string SkillId = "comfyui.text-to-image";
    private const string VideoSkillId = "web.text-to-video";
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);

    /// <summary>
    /// 读实体引用用的选项：**大小写不敏感**。
    ///
    /// 这条请求体有两个来源：浏览器发来的（camelCase）与聊天侧转交的（内部序列化，PascalCase）。
    /// 按大小写绑定会让其中一边的引用整批变成空字段——那会退化成「引用被静默丢掉」，
    /// 而这正是本能力要消灭的事。所以这里两种写法都认。
    /// </summary>
    private static readonly JsonSerializerOptions EntityReferenceOptions = new(JsonSerializerDefaults.Web);

    public static Func<string, HttpContext, JsonElement, Task<IResult>> Map(WebApplication app)
    {
        var execution = app.Services.GetRequiredService<SingleMachineExecutionService>();
        var attachments = app.Services.GetRequiredService<WebJobAttachmentService>();
        var hub = app.Services.GetRequiredService<CanvasEventHub>();
        var qualityStore = app.Services.GetRequiredService<WebJobQualityStore>();
        var assetDirectory = Path.GetFullPath(
            LegacyConfig.Text(app.Configuration, "AssetDirectory")
            ?? Path.Combine(AppContext.BaseDirectory, "assets"));

        SessionContext Session(HttpContext context)
        {
            var user = WebAccessGuard.CurrentUser(context);
            return new SessionContext { UserId = user?.Id ?? Guid.Empty, SessionId = user?.Id ?? Guid.Empty, ClientType = ClientType.Web,
                Role = MemberRole.Member, ServerClaims = user is null ? new HashSet<string>() : new HashSet<string>(WebAccessGuard.Permissions(context), StringComparer.Ordinal) };
        }
        bool ImageApproved() => string.Equals(LegacyConfig.Text(app.Configuration, "WebSkillApprovalMode"), "preapproved-local-image", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(app.Configuration["ComfyUI:Checkpoint"]);
        bool VideoApproved() => string.Equals(LegacyConfig.Text(app.Configuration, "WebVideoApprovalMode"), "preapproved-local-video", StringComparison.Ordinal)
            && WebInvocationExecutor.VideoLinkReady(app.Configuration);
        WebWorkflowCatalog.Entry? Workflow(string id) => WebWorkflowCatalog.Load(ComfyUiConversion.BaseUrl(app.Configuration)).FirstOrDefault(e => e.Id == id);
        bool Known(string id) => id is SkillId or VideoSkillId or WebImageGeneration.SkillId || WebWorkflowCatalog.IsWorkflow(id);
        bool InvokeAllowed(SessionContext session) => session.ServerClaims.Contains("skill.invoke");
        bool SkillAllowed(SessionContext session, string skillId) => InvokeAllowed(session)
            && (skillId == WebImageGeneration.SkillId ? WebImageGeneration.IsConfigured(WebSettingsApi.LoadModels()) : skillId == SkillId ? ImageApproved() : skillId == VideoSkillId ? VideoApproved()
                : app.Configuration["WebWorkflowApprovalMode"] == "preapproved-local-workflows" && Workflow(skillId) is not null);
        IResult Denied() => Error(403, "SKILL_NOT_APPROVED", "技能未获服务端预授权或缺少 skill.invoke 权限");
        object View(Job job, SessionContext session)
        {
            var result = job.ToResult();
            var canRetry = SkillAllowed(session, result.Tool) && execution.CanRetryJob(job.JobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out _);
            return new { result.JobId, result.InvocationId, state = result.State.ToString(), capability = result.Capability.ToString(), result.ProgressPercent,
                result.ErrorCode, result.ErrorMessage, result.ExternalTaskId, result.Outputs, result.Attempt,
                result.RetryOfJobId, result.RootJobId, result.Tool, canRetry,
                targetNodeId = job.Inputs.TryGetValue("targetNodeId", out var target) ? target.GetString() : null,
                attachmentState = attachments.State(job),
                // 判档结果与具体任务绑定；界面按 outputIndex 认，不按数组顺序猜。
                quality = qualityStore.Get(job.JobId) };
        }
        bool InputsEqual(IReadOnlyDictionary<string, JsonElement> left, IReadOnlyDictionary<string, JsonElement> right) =>
            left.Count == right.Count && right.All(pair => left.TryGetValue(pair.Key, out var value) && value.GetRawText() == pair.Value.GetRawText());
        IResult Find(Guid id, SessionContext session, out Job? job)
        {
            if (!execution.TryGet(id, out job)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job!.UserId != session.UserId || !WebJobAttachmentService.InCurrentCanvas(job)) { job = null; return Error(404, "JOB_NOT_FOUND", "任务不存在"); }
            return Results.Empty;
        }
        app.MapGet("/api/web/skills", (HttpContext context) =>
        {
            var session = Session(context);
            var skills = new List<object>();
            if (SkillAllowed(session, WebImageGeneration.SkillId)) skills.Add(new { id = WebImageGeneration.SkillId, name = "Web 图片 API", capability = "TextToImage", approval = "configured-web-image", referenceCapacity = WebImageGeneration.ReferenceCapacity(WebSettingsApi.LoadModels()), aspectRatios = new[] { "1:1", "16:9", "9:16", "4:3", "3:4" }, parameters = WebChatSkillParameters.Describe(WebImageGeneration.SkillId) });
            if (SkillAllowed(session, SkillId)) skills.Add(new { id = SkillId, name = "本机 ComfyUI 文生图", capability = "TextToImage", approval = "preapproved-local-image", parameters = WebChatSkillParameters.Describe(SkillId) });
            // 视频这条技能由**两种执行器**之一承担，它们认的请求体不一样：ComfyUI 那条认 videoFrames（秒折帧）
            // 与边长档位；接口站那条只认 seconds / width / height，并且**明确拒收** videoFrames 与 seed
            // （见下面 Invoke 里的 SKILL_INPUT_UNSUPPORTED）。客户端看不见配置，所以这里如实把后端告诉它——
            // 不说的话，前端只能按 ComfyUI 的体去发，接口站那条路必然被自己的服务端挡回来。
            if (SkillAllowed(session, VideoSkillId)) skills.Add(new { id = VideoSkillId, name = "Web 文生视频", capability = "TextToVideo", approval = "preapproved-local-video", parameters = WebChatSkillParameters.Describe(VideoSkillId),
                videoBackend = string.Equals(app.Configuration["WebVideoBackend"], "ComfyUI", StringComparison.OrdinalIgnoreCase) ? "comfyui" : "http" });
            if (InvokeAllowed(session) && app.Configuration["WebWorkflowApprovalMode"] == "preapproved-local-workflows")
                skills.AddRange(WebWorkflowCatalog.Load(ComfyUiConversion.BaseUrl(app.Configuration)).Select(e => (object)new {
                    id = e.Id, name = e.Workflow.Title, capability = e.Capability.ToString(), approval = "preapproved-local-workflows",
                    inputs = new[] { "prompt" }, outputs = new[] { e.Workflow.Kind }, parameters = WebChatSkillParameters.Describe(e.Id) }));
            // 附件可选类型也由服务端声明：前端 accept、预览方式与提示都照它渲染，
            // 不两边各写一份（写散了就会出现「能选不能传」或「能传不能看」）。
            var quality = WebImageQuality.Availability(WebSettingsApi.LoadModels());
            return Results.Json(new { skills = skills.ToArray(), attachments = WebChatAttachments.Describe(), attachmentAccept = WebChatAttachments.AcceptAttribute(),
                quality = new { available = quality.Ok, message = quality.Message } });
        });
        async Task<IResult> Invoke(string skillId, HttpContext context, JsonElement body)
        {
            var session = Session(context);
            if (session.UserId == Guid.Empty) return Error(403, "LOGIN_REQUIRED", "技能任务需要账号登录");
            var workflow = WebWorkflowCatalog.IsWorkflow(skillId) ? Workflow(skillId) : null;
            if (skillId != SkillId && skillId != WebImageGeneration.SkillId && skillId != VideoSkillId && workflow is null) return Error(404, "SKILL_NOT_FOUND", "技能不存在");
            if (!SkillAllowed(session, skillId)) return Denied();
            if (body.ValueKind != JsonValueKind.Object) return Error(400, "INVALID_REQUEST", "请求必须是 JSON 对象");
            var isVideo = skillId == VideoSkillId;
            // 白名单＝手写字段 ∪ **技能自己声明的参数名**：加一个声明就多收一个参数，
            // 不必再回来改这段列表（漏改一处就是「声明了但一调用就 400」）。
            var declared = WebChatSkillParameters.Declare(skillId);
            var declaredNames = declared.Select(spec => spec.Name).ToArray();
            var baseFields = workflow is not null ? new[] { "prompt", "idempotencyKey", "targetNodeId", "candidateBatch" } : isVideo
                ? new[] { "prompt", "negativePrompt", "model", "seconds", "width", "height", "videoFrames", "seed", "idempotencyKey", "targetNodeId", "chatSessionId", "referenceAttachmentIds" }
                : new[] { "prompt", "aspectRatio", "idempotencyKey", "targetNodeId", "chatSessionId", "referenceAttachmentIds", "candidateBatch" };
            var allowedFields = baseFields.Concat(declaredNames)
                // 实体引用只对有实体库的技能有意义；工作流绑定不了它，收下就是静默丢弃。
                .Concat(workflow is null ? new[] { "entityReferences" } : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToArray();
            var invalidVideoFields = isVideo && (
                (body.TryGetProperty("negativePrompt", out var negative) && (negative.ValueKind != JsonValueKind.String || negative.GetString()!.Length > 4000)) ||
                (body.TryGetProperty("width", out var width) && (width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out var widthValue) || widthValue is < 64 or > 2048)) ||
                (body.TryGetProperty("height", out var height) && (height.ValueKind != JsonValueKind.Number || !height.TryGetInt32(out var heightValue) || heightValue is < 64 or > 2048)) ||
                (body.TryGetProperty("videoFrames", out var frames) && (frames.ValueKind != JsonValueKind.Number || !frames.TryGetInt32(out var frameValue) || frameValue is < 1 or > 4096)) ||
                (body.TryGetProperty("seed", out var seed) && (seed.ValueKind != JsonValueKind.Number || !seed.TryGetInt64(out _))));
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(prompt.GetString()) || prompt.GetString()!.Length > 4000 ||
                !body.TryGetProperty("idempotencyKey", out var key) || key.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(key.GetString()) || key.GetString()!.Length > 128 ||
                (body.TryGetProperty("model", out var model) && (model.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(model.GetString()) || model.GetString()!.Length > 256)) ||
                (body.TryGetProperty("seconds", out var seconds) && (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out var secondsValue) || secondsValue is < 1 or > 600)) ||
                body.EnumerateObject().Any(p => !allowedFields.Contains(p.Name, StringComparer.Ordinal)) || invalidVideoFields)
                return Error(400, "INVALID_REQUEST", isVideo
                    ? "仅接受 prompt、negativePrompt、model、seconds（1-600）、width、height、videoFrames、seed 与 idempotencyKey"
                    : "图片技能仅接受 prompt 与 idempotencyKey");
            if (isVideo && !string.Equals(app.Configuration["WebVideoBackend"], "ComfyUI", StringComparison.OrdinalIgnoreCase) &&
                new[] { "negativePrompt", "videoFrames", "seed" }.Any(field => body.TryGetProperty(field, out _)))
                return Error(400, "SKILL_INPUT_UNSUPPORTED", "当前视频执行器仅支持 prompt、model、seconds、width、height 与参考图，拒绝忽略额外参数");
            var inputs = new Dictionary<string, JsonElement> { ["prompt"] = prompt.Clone(), ["webCanvasPath"] = JsonSerializer.SerializeToElement(WebProjectApi.Scope) };
            if (!isVideo && workflow is null)
            {
                var ratio = body.TryGetProperty("aspectRatio", out var aspect) && aspect.ValueKind == JsonValueKind.String ? aspect.GetString() : null;
                var size = WebImageGeneration.Size(ratio);
                if (size is null && (skillId == WebImageGeneration.SkillId || body.TryGetProperty("aspectRatio", out _)))
                    return Error(400, "IMAGE_RATIO_REQUIRED", "请选择图片比例：1:1、16:9、9:16、4:3 或 3:4");
                if (size is { } dimensions)
                {
                    inputs["aspectRatio"] = JsonSerializer.SerializeToElement(ratio);
                    inputs["width"] = JsonSerializer.SerializeToElement(dimensions.Width);
                    inputs["height"] = JsonSerializer.SerializeToElement(dimensions.Height);
                }
            }
            if (body.TryGetProperty("referenceAttachmentIds", out var references))
            {
                if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() is < 1 or > 8 || !body.TryGetProperty("chatSessionId", out var chatSession) || chatSession.ValueKind != JsonValueKind.String)
                    return Error(400, "REFERENCE_INVALID", "参考图须来自当前会话，最多8张");
                if (isVideo && !string.Equals(app.Configuration["WebVideoBackend"], "ComfyUI", StringComparison.OrdinalIgnoreCase) && references.GetArrayLength() > 1)
                    return Error(400, "SKILL_INPUT_UNSUPPORTED", "当前视频接口仅支持一张首帧参考图，不能静默忽略多图");
                if (skillId == WebImageGeneration.SkillId && references.GetArrayLength() > WebImageGeneration.ReferenceCapacity(WebSettingsApi.LoadModels()))
                    return Error(400, "IMAGE_REFERENCE_UNSUPPORTED", "当前图片模型不支持所需参考图，请在 Web 设置中选择支持参考图的模型");
                if (skillId == SkillId && references.GetArrayLength() > 1) return Error(400, "SKILL_INPUT_UNSUPPORTED", "当前图片工作流仅支持一张底图，不能静默忽略多图");
                var files = new List<WebChatStore.Attachment>();
                foreach (var reference in references.EnumerateArray())
                {
                    if (reference.ValueKind != JsonValueKind.String || WebChatApi.Attachment(context, chatSession.GetString()!, reference.GetString()!) is not { } file || !file.Type.StartsWith("image/"))
                        return Error(400, "REFERENCE_INVALID", "参考图不存在或不是当前会话图片");
                    files.Add(file);
                }
                var referenceDirectory = Path.Combine(assetDirectory, "web-references"); Directory.CreateDirectory(referenceDirectory);
                var paths = new List<string>();
                foreach (var file in files)
                {
                    var filePath = Path.Combine(referenceDirectory, file.Id + (file.Type == "image/png" ? ".png" : ".jpg"));
                    await File.WriteAllBytesAsync(filePath, file.Data, context.RequestAborted); paths.Add(filePath);
                }
                inputs["referenceImages"] = JsonSerializer.SerializeToElement(paths);
            }
            else if (body.TryGetProperty("chatSessionId", out _)) return Error(400, "REFERENCE_INVALID", "会话参数须与参考图一起提供");
            if (body.TryGetProperty("targetNodeId", out var targetNode))
            {
                if (targetNode.ValueKind != JsonValueKind.String || !targetNode.TryGetGuid(out _) || WebProjectApi.Current is not { IsProject: true } mode)
                    return Error(400, "TARGET_INVALID", "目标必须是当前项目画布的节点 ID");
                if (!session.ServerClaims.Contains("canvas.edit")) return Error(403, "TARGET_FORBIDDEN", "回挂产物需要 canvas.edit 权限");
                try
                {
                    var store = WebProjectApi.Store(mode)!;
                    var snapshot = store.GenerationTarget(targetNode.GetGuid());
                    inputs["targetBaseRevision"] = JsonSerializer.SerializeToElement(snapshot.Revision);
                    if (!isVideo && (workflow is null || workflow.Capability == Capability.TextToImage))
                    {
                        var capacity = skillId == WebImageGeneration.SkillId ? WebImageGeneration.ReferenceCapacity(WebSettingsApi.LoadModels()) : skillId == SkillId ? 1 : 0;
                        var extra = inputs.TryGetValue("referenceImages", out var supplied) ? supplied.Deserialize<string[]>()! : Array.Empty<string>();
                        var paths = WebGenerationReferences.Resolve(snapshot, capacity, extra);
                        WebGenerationReferences.RecordEvidence(inputs, paths, snapshot);
                    }
                    if (isVideo)
                    {
                        var extra = inputs.TryGetValue("referenceImages", out var supplied) ? supplied.Deserialize<string[]>()! : Array.Empty<string>();
                        var paths = WebGenerationReferences.ResolveVideoTarget(snapshot, extra);
                        WebGenerationReferences.RecordEvidence(inputs, paths, snapshot);
                    }
                    if (!inputs.ContainsKey("sourceFingerprints"))
                    {
                        var paths = WebGenerationReferences.ResolveVideoTarget(snapshot, []);
                        if (paths.Length > 0)
                            throw new InvalidDataException("当前本地视频工作流不支持首帧参考，禁止忽略参考生成");
                        WebGenerationReferences.RecordEvidence(inputs, [], snapshot);
                    }
                    if (context.Items.TryGetValue("webChatGeneration", out var chat) && chat is true)
                        inputs["webChatGeneration"] = JsonSerializer.SerializeToElement(true);
                }
                catch (InvalidDataException ex) { return Error(409, "GENERATION_ASSETS_INVALID", ex.Message); }
                catch (Exception ex) when (ex is IOException or JsonException) { return Error(409, "TARGET_INVALID", "目标节点不可用"); }
                inputs["targetNodeId"] = targetNode.Clone();
            }
            // 候选批次（节点出图）：产物留在任务里等用户采用，后台不替它自动回挂。
            // 这是**显式**开关，不是「只要有 targetNodeId 就当候选」：回挂方式按入口分。
            // 图片 API、内置 ComfyUI 与已安装工作流都支持候选批次；视频与重试维持原行为。
            if (body.TryGetProperty("candidateBatch", out var candidate))
            {
                if (candidate.ValueKind != JsonValueKind.True) return Error(400, "INVALID_REQUEST", "candidateBatch 只能是 true");
                if (!inputs.ContainsKey("targetNodeId")) return Error(400, "TARGET_INVALID", "候选批次必须指定目标节点");
                inputs[WebJobAttachmentService.CandidateInput] = JsonSerializer.SerializeToElement(true);
            }
            if (!inputs.ContainsKey("referenceImageHashes"))
                WebGenerationReferences.RecordEvidence(inputs, inputs.TryGetValue("referenceImages", out var finalImages) ? finalImages.Deserialize<string[]>()! : []);
            if (isVideo && body.TryGetProperty("model", out var selectedModel)) inputs["model"] = selectedModel.Clone();
            if (isVideo && body.TryGetProperty("seconds", out var selectedSeconds)) inputs["seconds"] = selectedSeconds.Clone();
            if (isVideo && body.TryGetProperty("negativePrompt", out var negativePrompt)) inputs["negativePrompt"] = negativePrompt.Clone();
            if (isVideo && body.TryGetProperty("width", out var selectedWidth)) inputs["width"] = selectedWidth.Clone();
            if (isVideo && body.TryGetProperty("height", out var selectedHeight)) inputs["height"] = selectedHeight.Clone();
            if (isVideo && body.TryGetProperty("videoFrames", out var selectedFrames)) inputs["videoFrames"] = selectedFrames.Clone();
            if (isVideo && body.TryGetProperty("seed", out var selectedSeed)) inputs["seed"] = selectedSeed.Clone();
            // 声明过的任意参数：手写映射之外的那些（例如图片技能新增的 negativePrompt / seed）
            // 按同一个声明逐项校验后原样落进执行层输入。手写过的字段不再重复搬一遍——
            // 两边都以同一份 inputs 为准，重复搬运只会让「谁是权威」变模糊。
            var handMapped = new HashSet<string>(StringComparer.Ordinal)
            {
                "prompt", "aspectRatio", "idempotencyKey", "targetNodeId", "chatSessionId",
                "referenceAttachmentIds", "candidateBatch"
            };
            if (isVideo) handMapped.UnionWith(new[] { "model", "seconds", "width", "height", "videoFrames", "seed", "negativePrompt" });
            var declaredSupplied = declared
                .Where(spec => body.TryGetProperty(spec.Name, out _))
                .ToDictionary(spec => spec.Name, spec => body.GetProperty(spec.Name).Clone(), StringComparer.Ordinal);
            if (!WebChatSkillParameters.TryValidate(skillId, declaredSupplied, Array.Empty<string>(), out var acceptedParameters, out var parameterCode, out var parameterMessage, out _))
                return Error(400, parameterCode!, parameterMessage!);
            foreach (var spec in declared.Where(spec => !handMapped.Contains(spec.Name)))
                if (acceptedParameters.TryGetValue(spec.Name, out var declaredValue)) inputs[spec.Name] = declaredValue;
            // 实体引用：与聊天内同一条校验（类型 / ID / 变体 / 版本都要能对上），
            // 通过的引用原样进输入，执行层与任务记录都看得到；解析不出来就明确拒绝。
            if (body.TryGetProperty("entityReferences", out var entityReferences))
            {
                if (workflow is not null) return Error(400, "SKILL_INPUT_UNSUPPORTED", "工作流技能不支持实体引用");
                if (entityReferences.ValueKind != JsonValueKind.Array)
                    return Error(400, "SKILL_ENTITY_INVALID", "entityReferences 必须是数组");
                var declaredReferences = entityReferences.Deserialize<List<AgentEntityReference>>(EntityReferenceOptions) ?? new List<AgentEntityReference>();
                if (declaredReferences.Count == 0) return Error(400, "SKILL_ENTITY_INVALID", "entityReferences 不能为空数组");
                if (WebProjectApi.Current is not { IsProject: true } referenceMode)
                    return Error(409, "SKILL_ENTITY_INVALID", "实体引用需要项目画布");
                try
                {
                    var resolved = WebProjectApi.Store(referenceMode)!.ResolveEntityReferences(declaredReferences);
                    inputs["entityReferences"] = JsonSerializer.SerializeToElement(resolved, EntityReferenceOptions);
                }
                catch (InvalidDataException ex) { return Error(409, "SKILL_ENTITY_INVALID", ex.Message); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
                { return Error(409, "SKILL_ENTITY_INVALID", "无法安全读取画布实体"); }
            }
            if (workflow is not null)
            {
                inputs["workflowRevision"] = JsonSerializer.SerializeToElement(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workflow.Payload))));
                inputs["declaredOutputRole"] = JsonSerializer.SerializeToElement(workflow.Workflow.Kind);
            }
            var existing = execution.GetJobs(session.UserId).FirstOrDefault(j => j.IdempotencyKey == key.GetString());
            if (existing is not null)
            {
                if (existing.Inputs.TryGetValue("targetBaseRevision", out var originalRevision))
                    inputs["targetBaseRevision"] = originalRevision;
                else inputs.Remove("targetBaseRevision");
                if (existing.Tool != skillId || !InputsEqual(existing.Inputs, inputs))
                    return Error(409, "IDEMPOTENCY_CONFLICT", "幂等键已用于其他调用");
                return Results.Json(View(existing, session));
            }
            var invocation = new Invocation { Tool = skillId, Capability = workflow?.Capability ?? (isVideo ? Capability.TextToVideo : Capability.TextToImage), Channel = isVideo ? "video" : skillId == WebImageGeneration.SkillId ? "image-api" : "comfyui", Inputs = inputs };
            // **提交后立即返回任务快照，不在这里等内容跑完**。
            //
            // SingleMachineExecutionService.StartAsync 在锁内先登记 Job、再 Task.Run，返回的是「整次执行完成」
            // 的 Task。若在这里 await，HTTP 响应要等图/视频生成完才回来：候选卡上的槽位拿不到 jobId，
            // 生成期间只剩「排队中」，既看不到真实进度，整批也会被拖成串行提交。
            // 所以这里只取刚登记的那份快照；执行继续在后台跑，状态由 GET /api/web/jobs 轮询。
            var executionTask = execution.StartAsync(session, invocation, key.GetString()!);
            var submitted = execution.GetJobs(session.UserId).FirstOrDefault(candidate =>
                candidate.InvocationId == invocation.InvocationId ||
                string.Equals(candidate.IdempotencyKey, key.GetString(), StringComparison.Ordinal));
            if (submitted is not null)
            {
                // 后台任务已经有人等它的状态，这里不再 await；异常由任务自身记录到 Job，观察掉即可。
                _ = executionTask.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                execution.TryGet(submitted.JobId, out var snapshot);
                return Results.Json(View(snapshot ?? submitted, session));
            }
            // 理论上不会走到这里（StartAsync 同步登记 job）；留一条兜底，保持原来的完成语义。
            var result = await executionTask;
            execution.TryGet(result.JobId, out var job);
            return Results.Json(View(job!, session));
        }
        app.MapPost("/api/web/skills/{skillId}/invoke", async (string skillId, HttpRequest request) =>
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
                return await Invoke(skillId, request.HttpContext, document.RootElement.Clone());
            }
            catch (JsonException) { return Error(400, "INVALID_REQUEST", "请求 JSON 无效"); }
        });
        app.MapGet("/api/web/jobs", (HttpContext context) =>
        {
            var session = Session(context);
            return Results.Json(new { jobs = execution.GetJobs(session.UserId)
                .Where(j => Known(j.Tool) && WebJobAttachmentService.InCurrentCanvas(j))
                .Select(j => View(j, session)).ToArray() });
        });
        app.MapGet("/api/web/jobs/{jobId:guid}", (HttpContext context, Guid jobId) =>
        {
            var session = Session(context);
            var found = Find(jobId, session, out var job);
            return job is null ? found : Known(job.Tool) ? Results.Json(View(job, session)) : Error(404, "JOB_NOT_FOUND", "任务不存在");
        });
        app.MapGet("/api/web/jobs/{jobId:guid}/outputs/{outputIndex:int}", (HttpContext context, Guid jobId, int outputIndex) =>
        {
            var session = Session(context);
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (!Known(job.Tool)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job.State != JobState.Succeeded || outputIndex < 0 || outputIndex >= job.Outputs.Count)
                return Error(404, "OUTPUT_NOT_FOUND", "任务产物不存在");
            var path = ResolveAsset(job.Outputs[outputIndex].Ref, assetDirectory);
            if (path is null) return Error(404, "OUTPUT_NOT_FOUND", "任务产物文件不存在");

            // 显式打开已解析的资产，使响应绑定到已完成所属校验的路径，
            // 避免依赖延后的物理文件解析。
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var contentType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".mp4" => "video/mp4",
                    ".webm" => "video/webm",
                    ".mov" => "video/quicktime",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    _ => "application/octet-stream"
                };
                return Results.Stream(stream, contentType, enableRangeProcessing: true);
            }
            catch (FileNotFoundException)
            {
                return Error(404, "OUTPUT_NOT_FOUND", "任务产物文件不存在");
            }
            catch (DirectoryNotFoundException)
            {
                return Error(404, "OUTPUT_NOT_FOUND", "任务产物目录不存在");
            }
            catch (UnauthorizedAccessException)
            {
                return Error(403, "OUTPUT_FORBIDDEN", "任务产物不可读取");
            }
        });
        // 采用一件候选产物：把它安全追加到**原任务的目标节点**。
        //
        // 信任边界都在这边：目标节点取自任务自己的 inputs（不是客户端传的），产物取自任务自己的
        // outputs[index]（客户端传不了路径与附件 ID），权限、租约、锁定、目录与图片类型逐层校验，
        // 落盘走 AppendToCurrent（当前快照 + 字节级 CAS），幂等由确定性附件 ID 保证。
        app.MapPost("/api/web/jobs/{jobId:guid}/outputs/{outputIndex:int}/adopt", (HttpContext context, Guid jobId, int outputIndex) =>
        {
            var session = Session(context);
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (!Known(job.Tool)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            // 只有候选批次能从画布直接采用：普通后台回挂与聊天生成的审批各有各的路，不能从这儿绕。
            if (!WebJobAttachmentService.IsCandidate(job)) return Error(409, "NOT_CANDIDATE_JOB", "这条任务不是候选批次");
            if (job.State != JobState.Succeeded) return Error(409, "JOB_NOT_READY", "任务还没有成功结果");
            if (outputIndex < 0 || outputIndex >= job.Outputs.Count) return Error(404, "OUTPUT_NOT_FOUND", "任务产物不存在");
            var user = WebAccessGuard.CurrentUser(context);
            if (user is null || user.Disabled || !session.ServerClaims.Contains("canvas.edit"))
                return Error(403, "TARGET_FORBIDDEN", "采用产物需要登录及画布编辑权限");
            if (!job.Inputs.TryGetValue("targetNodeId", out var targetNode) || !targetNode.TryGetGuid(out var nodeId))
                return Error(409, "TARGET_INVALID", "这条任务没有目标节点");
            if (WebProjectApi.Current is not { IsProject: true } mode) return Error(409, "CANVAS_REQUIRES_PROJECT", "采用产物需要项目画布");
            var store = WebProjectApi.Store(mode);
            var leases = WebProjectApi.Leases(mode);
            if (store is null || leases is null) return Error(409, "CANVAS_REQUIRES_PROJECT", "采用产物需要项目画布");
            return leases.WithTreeTransaction<IResult>(user, () =>
            {
                var before = store.LastCommit;
                var result = store.AdoptJobOutput(job.ToResult(), nodeId, outputIndex);
                var after = store.LastCommit;
                // 采用成功就是一次真实的画布写入：推一条（与产物回挂同一个 scope），别的端跟着刷新。
                if (after.Serial > before.Serial)
                    hub.CanvasChanged(after.Revision, nodeId.ToString(), WebAccessGuard.ActorName(context), "job-output");
                return result;
            }, EditLeaseApi.Failure);
        });
        app.MapPost("/api/web/jobs/{jobId:guid}/cancel", (HttpContext context, Guid jobId) =>
        {
            var session = Session(context);
            if (!session.ServerClaims.Contains("job.cancel")) return Error(403, "JOB_FORBIDDEN", "没有取消权限");
            var found = Find(jobId, session, out var job);
            if (job is null) return found;
            if (!Known(job.Tool)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (job.State is not (JobState.Queued or JobState.Running)) return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消");
            try { execution.Cancel(session, jobId); return Results.Json(View(job, session)); }
            catch (ProtocolViolationException) { return Error(409, "JOB_NOT_CANCELLABLE", "当前任务不能取消"); }
        });
        app.MapPost("/api/web/jobs/{jobId:guid}/retry", async (HttpContext context, Guid jobId) =>
        {
            var session = Session(context);
            var found = Find(jobId, session, out var source);
            if (source is null) return found;
            if (!Known(source.Tool)) return Error(404, "JOB_NOT_FOUND", "任务不存在");
            if (!SkillAllowed(session, source.Tool)) return Denied();
            if (!execution.CanRetryJob(jobId, session.UserId, JobRetryPolicy.DefaultMaxAttempts, out var reason))
                return Error(409, "JOB_NOT_RETRYABLE", reason);
            try
            {
                if (source.Capability == Capability.TextToImage && source.Inputs.TryGetValue("targetNodeId", out var retryTarget))
                {
                    if (!session.ServerClaims.Contains("canvas.edit")) return Error(403, "TARGET_FORBIDDEN", "重试目标生成需要 canvas.edit 权限");
                    var store = WebProjectApi.Current is { IsProject: true } retryMode ? WebProjectApi.Store(retryMode) : null;
                    if (store is null) return Error(409, "TARGET_INVALID", "目标画布不可用");
                    var capacity = source.Tool == WebImageGeneration.SkillId ? WebImageGeneration.ReferenceCapacity(WebSettingsApi.LoadModels()) : source.Tool == SkillId ? 1 : 0;
                    var target = store.GenerationTarget(retryTarget.GetGuid());
                    var paths = source.Inputs.TryGetValue("referenceImages", out var retryImages) ? retryImages.Deserialize<string[]>()! : Array.Empty<string>();
                    var errors = GenerationAudit.CheckImageSubmission(target.Canvas, target.Node, WebGenerationReferences.Locate, paths, capacity);
                    if (errors.Count > 0) return Error(409, "GENERATION_ASSETS_INVALID", string.Join("；", errors));
                }
                var result = await execution.RetryAsync(session, jobId, Guid.NewGuid().ToString("N"));
                execution.TryGet(result.JobId, out var job);
                return Results.Json(View(job!, session));
            }
            catch (ProtocolViolationException ex) { return Error(409, ex.Code, ex.Message); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            { return Error(409, "GENERATION_ASSETS_INVALID", ex.Message); }
        });
        return Invoke;
    }

    /// <summary>
    /// 把任务产物里的 <c>asset://文件名</c> 解析成资产目录下的真实路径。
    /// **只认这个前缀**：客户端传不进任意路径，服务端也不会把任务产物以外的文件交出去。
    /// </summary>
    internal static string? ResolveAsset(string? reference, string directory)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var fileName = reference.StartsWith("asset://", StringComparison.Ordinal)
            ? Path.GetFileName(reference["asset://".Length..])
            : null;
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(path) ? path : null;
    }
}
