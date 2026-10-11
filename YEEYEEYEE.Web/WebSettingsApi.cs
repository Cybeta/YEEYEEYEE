using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using YEEYEEYEE.Web.Auth;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>公开投影不返回密钥；全局模型配置只允许管理员以版本检查写入。</summary>
internal static class WebSettingsApi
{
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    private static readonly object ModelGate = new();
    public sealed record WorkflowImport(string? DisplayName, string[]? Paths);
    public sealed record WorkflowMutation(string Key, string Operation, string? Name = null, bool? Enabled = null);
    public sealed record SkillMutation(string Operation, string? Name = null, bool? Enabled = null);
    public sealed record ModelSelection(string Revision);
    public sealed record ModelInput(string Revision, string DisplayName, string Endpoint, string Model,
        int ApiFormat = 0, bool UseFullUrl = false, string? ApiKey = null, bool Enabled = true);
    public sealed record MediaInput(string Revision, string ImageEndpoint, string ImageModel,
        string ComfyUiBaseUrl, string ComfyUiCheckpoint, string VideoEndpoint, string VideoModel,
        string? ImageApiKey = null, string? VideoApiKey = null,
        bool ClearImageKey = false, bool ClearVideoKey = false);
    private static string ModelRevision() => File.Exists(AiProviderSettings.FilePath)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AiProviderSettings.FilePath))) : "missing";
    private static JsonObject ModelRoot() => File.Exists(AiProviderSettings.FilePath)
        ? JsonNode.Parse(File.ReadAllText(AiProviderSettings.FilePath))!.AsObject() : new JsonObject();
    private static void SaveModels(JsonObject root)
    {
        var path = AiProviderSettings.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void SelectModel(JsonObject root, JsonObject profile)
    {
        var defaults = JsonSerializer.SerializeToNode(new AiProviderProfile())!.AsObject();
        foreach (var field in new[] { "DisplayName", "Endpoint", "Model", "ApiFormat", "UseFullUrl", "ApiKey", "Temperature", "ContextWindow", "MaxOutputTokens", "SendSamplingParameters", "SupportsImageInput" })
            root[field] = (profile[field] ?? defaults[field])?.DeepClone();
        root["SelectedProfileId"] = profile["Id"]!.DeepClone();
        root["UseLocalProvider"] = false; root["ProviderChoiceMade"] = true;
    }
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/web/settings/skills", (HttpContext context) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "技能管理需要管理员权限");
            var library = SkillLibrary.Load();
            return Results.Json(new { skills = library.Skills.Select(s => new { s.Id, s.Name, s.Enabled, s.Description }), errors = library.Errors });
        });
        app.MapPost("/api/web/settings/skills/{id}", (HttpContext context, string id, SkillMutation input) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "技能管理需要管理员权限");
            lock (ModelGate)
            {
                var skill = SkillLibrary.Load().Skills.FirstOrDefault(s => s.Id == id);
                if (skill is null) return Error(404, "SKILL_NOT_FOUND", "技能不存在");
                string error;
                if (input.Operation == "enabled" && input.Enabled is { } enabled)
                    return SkillLibrary.TrySetEnabled(skill, enabled, out error) ? Results.Json(new { saved = true }) : Error(503, "SKILL_SAVE_FAILED", error);
                if (input.Operation == "delete")
                    return SkillLibrary.TryDelete(skill, out error) ? Results.Json(new { deleted = true }) : Error(503, "SKILL_SAVE_FAILED", error);
                if (input.Operation != "rename" || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200) return Error(400, "INVALID_REQUEST", "技能操作或名称无效");
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(skill.FilePath))!.AsObject();
                    foreach (var key in node.Select(p => p.Key).Where(key => string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase)).ToArray())
                        node.Remove(key);
                    node["Name"] = input.Name.Trim();
                    var temporary = skill.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        File.Move(temporary, skill.FilePath, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return Results.Json(new { saved = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Error(503, "SKILL_SAVE_FAILED", "技能改名失败"); }
            }
        });
        app.MapGet("/api/web/settings/workflows/installed", (HttpContext context) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "工作流管理需要管理员权限");
            return Results.Json(new { sites = SiteCatalog.Load().Sites.Where(s => s.IsComfyUi).Select(s => new {
                s.Id, s.DisplayName, workflows = s.Workflows.Select(w => new { w.Key, w.Title, w.Enabled, w.Kind, w.Converted, w.Error, w.LastImportError }) }) });
        });
        app.MapPost("/api/web/settings/workflows/{siteId}/manage", (HttpContext context, string siteId, WorkflowMutation input) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "工作流管理需要管理员权限");
            lock (ModelGate)
            {
                var site = SiteCatalog.Load().Sites.FirstOrDefault(s => s.Id == siteId && s.IsComfyUi);
                var workflow = site?.Workflows.FirstOrDefault(w => w.Key == input.Key);
                if (site is null || workflow is null) return Error(404, "WORKFLOW_NOT_FOUND", "工作流不存在");
                string error;
                if (input.Operation == "delete") return SiteCatalog.TryDeleteWorkflow(site, input.Key, out error)
                    ? Results.Json(new { deleted = true }) : Error(503, "WORKFLOW_SAVE_FAILED", error);
                if (input.Operation == "enabled" && input.Enabled is { } enabled) workflow.Enabled = enabled;
                else if (input.Operation == "rename" && !string.IsNullOrWhiteSpace(input.Name) && input.Name.Length <= 200) workflow.Title = input.Name.Trim();
                else return Error(400, "INVALID_REQUEST", "工作流操作无效");
                return SiteCatalog.Save(site, out error) ? Results.Json(new { saved = true }) : Error(503, "WORKFLOW_SAVE_FAILED", error);
            }
        });
        app.MapGet("/api/web/settings/workflows", async (HttpContext context, IHttpClientFactory factory) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "工作流导入需要管理员权限");
            try
            {
                var http = factory.CreateClient("comfyui");
                var paths = await ComfyUiLibrary.ReadManifestAsync(http.BaseAddress!.ToString(), http, context.RequestAborted);
                return Results.Json(new { paths });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
            { return Error(502, "WORKFLOW_READ_FAILED", "工作流清单无法读取"); }
        });
        app.MapPost("/api/web/settings/workflows/import", async (HttpContext context, WorkflowImport input, IHttpClientFactory factory) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "工作流导入需要管理员权限");
            if ((input.DisplayName?.Length ?? 0) > 200 || input.Paths is { Length: > 500 }) return Error(400, "INVALID_REQUEST", "导入内容过长");
            try
            {
                var http = factory.CreateClient("comfyui");
                var result = await ComfyUiLibrary.FetchAsync(http.BaseAddress!.ToString(), http, cancellationToken: context.RequestAborted, selectedPaths: input.Paths);
                lock (ModelGate)
                {
                    var previous = SiteCatalog.Load().Sites.FirstOrDefault(s => string.Equals(s.BaseUrl, result.BaseUrl, StringComparison.OrdinalIgnoreCase));
                    var installed = ComfyUiLibrary.Install(result, input.DisplayName ?? "", app.Configuration["ComfyUI:Checkpoint"] ?? "", previous);
                    if (installed.Site is null) return Error(503, "WORKFLOW_INSTALL_FAILED", installed.Error);
                    return Results.Json(new { installed = true, result.Converted, result.Failed, result.Notes,
                        workflows = result.Workflows.Select(w => new { w.Key, w.Title, w.Converted, w.Note, w.Error }),
                        formatHint = "Web 可导入 API 格式；UI 格式请先在官方 ComfyUI 前端导出 API，当前服务不会伪转换" });
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
            { return Error(502, "WORKFLOW_IMPORT_FAILED", "工作流导入失败，请检查服务连接与工作流格式"); }
        });
        app.MapGet("/api/web/models", () =>
        {
            lock (ModelGate)
            {
                try
                {
                    var root = ModelRoot();
                    var profiles = root["Profiles"] as JsonArray ?? new JsonArray();
                    return Results.Json(new { revision = ModelRevision(), selectedProfileId = root["SelectedProfileId"]?.GetValue<string>(),
                        profiles = profiles.OfType<JsonObject>().Select(p => new { id = p["Id"]?.GetValue<string>(),
                            displayName = p["DisplayName"]?.GetValue<string>(), endpoint = p["Endpoint"]?.GetValue<string>(), model = p["Model"]?.GetValue<string>(),
                            apiFormat = p["ApiFormat"]?.GetValue<int>() ?? 0, useFullUrl = p["UseFullUrl"]?.GetValue<bool>() ?? false,
                            enabled = p["Enabled"]?.GetValue<bool>() ?? true, hasKey = !string.IsNullOrEmpty(p["ApiKey"]?.GetValue<string>()) }).ToArray() });
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return Error(503, "MODEL_READ_FAILED", "无法安全读取模型配置"); }
            }
        });
        app.MapPut("/api/web/models/{id}", (HttpContext c, string id, ModelInput input) =>
        {
            if (WebAccessGuard.CurrentUser(c)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "全局模型配置需要管理员权限");
            if (!Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                string.IsNullOrWhiteSpace(input.Model) || input.Model.Length > 256 || string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 128 || input.ApiFormat is < 0 or > 1 || (input.ApiKey?.Length ?? 0) > 8192)
                return Error(400, "MODEL_INVALID", "请填写无凭据或查询参数的 HTTP(S) 地址、模型及有效接口格式");
            lock (ModelGate)
            {
                try
                {
                    if (input.Revision != ModelRevision()) return Error(409, "MODEL_REVISION_CONFLICT", "配置已变化，请重新加载");
                    var root = ModelRoot(); var profiles = root["Profiles"] as JsonArray;
                    if (profiles is null) { profiles = new JsonArray(); root["Profiles"] = profiles; }
                    var profile = profiles.OfType<JsonObject>().FirstOrDefault(p => p["Id"]?.GetValue<string>() == id);
                    if (profile is null) { profile = new JsonObject { ["Id"] = id, ["Temperature"] = 0.7, ["SendSamplingParameters"] = true }; profiles.Add(profile); }
                    profile["DisplayName"] = input.DisplayName.Trim(); profile["Endpoint"] = input.Endpoint.Trim(); profile["Model"] = input.Model.Trim();
                    profile["ApiFormat"] = input.ApiFormat; profile["UseFullUrl"] = input.UseFullUrl; profile["Enabled"] = input.Enabled;
                    // null/空白均保留旧密钥；从不把旧密钥送回浏览器。
                    if (!string.IsNullOrWhiteSpace(input.ApiKey))
                    {
                        var protectedKey = SecretProtector.Protect(input.ApiKey.Trim());
                        if (!SecretProtector.IsEncryptedAtRest(protectedKey)) return Error(503, "MODEL_SECRET_STORAGE", "当前环境无法加密保存密钥");
                        profile["ApiKey"] = protectedKey;
                    }
                    if (root["SelectedProfileId"]?.GetValue<string>() == id || profiles.Count == 1) SelectModel(root, profile);
                    SaveModels(root); return Results.Json(new { saved = true, revision = ModelRevision() });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return Error(503, "MODEL_SAVE_FAILED", "模型配置保存失败"); }
            }
        });
        app.MapPost("/api/web/models/{id}/select", (HttpContext c, string id, ModelSelection input) =>
        {
            if (WebAccessGuard.CurrentUser(c)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "全局模型切换需要管理员权限");
            lock (ModelGate)
            {
                try
                {
                    if (input.Revision != ModelRevision()) return Error(409, "MODEL_REVISION_CONFLICT", "配置已变化，请重新加载");
                    var root = ModelRoot(); var profile = (root["Profiles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => p["Id"]?.GetValue<string>() == id);
                    if (profile is null) return Error(404, "MODEL_NOT_FOUND", "模型配置不存在");
                    profile["Enabled"] = true; SelectModel(root, profile); SaveModels(root);
                    return Results.Json(new { selected = true, revision = ModelRevision() });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                { return Error(503, "MODEL_SAVE_FAILED", "模型配置切换失败"); }
            }
        });
        IResult RemoveModel(HttpContext c, string id, ModelSelection input, bool clearKey)
        {
            if (WebAccessGuard.CurrentUser(c)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "全局模型配置需要管理员权限");
            lock (ModelGate)
            {
                try
                {
                    if (input.Revision != ModelRevision()) return Error(409, "MODEL_REVISION_CONFLICT", "配置已变化，请重新加载");
                    var root = ModelRoot(); var profiles = root["Profiles"] as JsonArray;
                    var profile = profiles?.OfType<JsonObject>().FirstOrDefault(p => p["Id"]?.GetValue<string>() == id);
                    if (profile is null) return Error(404, "MODEL_NOT_FOUND", "模型配置不存在");
                    var selected = root["SelectedProfileId"]?.GetValue<string>() == id;
                    if (clearKey)
                    {
                        profile["ApiKey"] = "";
                        if (selected) SelectModel(root, profile);
                    }
                    else
                    {
                        profiles!.Remove(profile);
                        if (selected)
                        {
                            var next = profiles.OfType<JsonObject>().FirstOrDefault(p => p["Enabled"]?.GetValue<bool>() != false);
                            if (next is not null) SelectModel(root, next);
                            else
                            {
                                // 清理活动配置的副本，防止已删除模型的凭据仍被聊天使用。
                                SelectModel(root, new JsonObject { ["Id"] = "", ["Endpoint"] = "", ["Model"] = "", ["ApiKey"] = "" });
                                root["UseLocalProvider"] = true;
                                root["ProviderChoiceMade"] = false;
                            }
                        }
                    }
                    SaveModels(root);
                    return Results.Json(new { revision = ModelRevision(), message = clearKey ? "密钥已清除" : "模型配置已删除" });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                { return Error(503, "MODEL_SAVE_FAILED", "模型配置保存失败"); }
            }
        }
        app.MapDelete("/api/web/models/{id}", (HttpContext c, string id, string revision) => RemoveModel(c, id, new ModelSelection(revision), false));
        app.MapPost("/api/web/models/{id}/clear-key", (HttpContext c, string id, ModelSelection input) => RemoveModel(c, id, input, true));
        app.MapPost("/api/web/models/{id}/test", async (HttpContext c, string id, IHttpClientFactory factory) =>
        {
            if (WebAccessGuard.CurrentUser(c)?.Role != UserRole.Admin) return Error(403, "MODEL_ADMIN_REQUIRED", "模型测试需要管理员权限");
            try
            {
                var config = AiProviderSettings.Load(migrateSecrets: false); var profile = config.Profiles.FirstOrDefault(p => p.Id == id);
                if (profile is null) return Error(404, "MODEL_NOT_FOUND", "模型配置不存在");
                if (profile.ApiKeyUnreadable) return Error(409, "MODEL_KEY_UNREADABLE", "服务账号无法解密，请重新填写密钥");
                AiProviderSettings.ApplyProfile(config, profile);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await new OpenAiCompatibleProvider(config, factory.CreateClient("chat")).ChatAsync(new[] { new AiChatMessage { Content = "Reply OK only." } }, timeout.Token);
                return Results.Json(new { reachable = true, message = "模型请求成功" });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException) { return Error(502, "MODEL_TEST_FAILED", "模型请求失败或超时，请检查配置"); }
        });
        app.MapGet("/api/web/settings/media", (HttpContext context) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin)
                return Error(403, "MODEL_ADMIN_REQUIRED", "媒体配置需要管理员权限");
            lock (ModelGate)
            {
                try
                {
                    var root = ModelRoot();
                    string Read(string key) => root[key]?.GetValue<string>() ?? "";
                    return Results.Json(new { revision = ModelRevision(), imageEndpoint = Read("ImageEndpoint"),
                        imageModel = Read("ImageModel"), comfyUiBaseUrl = Read("ComfyUiBaseUrl"),
                        comfyUiCheckpoint = Read("ComfyUiCheckpoint"), videoEndpoint = Read("VideoEndpoint"),
                        videoModel = Read("VideoModel"), imageHasKey = Read("ImageApiKey").Length > 0,
                        videoHasKey = Read("VideoApiKey").Length > 0 });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                { return Error(503, "SETTINGS_READ_FAILED", "媒体配置读取失败"); }
            }
        });
        app.MapPut("/api/web/settings/media", (HttpContext context, MediaInput input) =>
        {
            if (WebAccessGuard.CurrentUser(context)?.Role != UserRole.Admin)
                return Error(403, "MODEL_ADMIN_REQUIRED", "媒体配置需要管理员权限");
            bool ValidUrl(string? value) => value is not null && (value.Length == 0 ||
                value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment));
            if (!ValidUrl(input.ImageEndpoint) || !ValidUrl(input.VideoEndpoint) || !ValidUrl(input.ComfyUiBaseUrl) ||
                input.ImageModel is null || input.ImageModel.Length > 256 || input.VideoModel is null || input.VideoModel.Length > 256 ||
                input.ComfyUiCheckpoint is null || input.ComfyUiCheckpoint.Length > 512 ||
                (input.ImageApiKey?.Length ?? 0) > 8192 || (input.VideoApiKey?.Length ?? 0) > 8192 ||
                input.ClearImageKey && !string.IsNullOrWhiteSpace(input.ImageApiKey) || input.ClearVideoKey && !string.IsNullOrWhiteSpace(input.VideoApiKey))
                return Error(400, "MEDIA_INVALID", "媒体地址必须为无凭据或查询参数的 HTTP(S) 地址；清密钥和设置新密钥不能同时使用");
            lock (ModelGate)
            {
                try
                {
                    if (input.Revision != ModelRevision()) return Error(409, "MODEL_REVISION_CONFLICT", "配置已变化，请重新加载");
                    var root = ModelRoot();
                    foreach (var field in new[] { ("ImageEndpoint", input.ImageEndpoint), ("ImageModel", input.ImageModel),
                        ("ComfyUiBaseUrl", input.ComfyUiBaseUrl), ("ComfyUiCheckpoint", input.ComfyUiCheckpoint),
                        ("VideoEndpoint", input.VideoEndpoint), ("VideoModel", input.VideoModel) })
                        root[field.Item1] = field.Item2.Trim();
                    foreach (var field in new[] { ("ImageApiKey", input.ImageApiKey, input.ClearImageKey), ("VideoApiKey", input.VideoApiKey, input.ClearVideoKey) })
                    {
                        if (field.Item3) root[field.Item1] = "";
                        else if (!string.IsNullOrWhiteSpace(field.Item2))
                        {
                            var secret = SecretProtector.Protect(field.Item2.Trim());
                            if (!SecretProtector.IsEncryptedAtRest(secret)) return Error(503, "MODEL_SECRET_STORAGE", "当前环境无法加密保存密钥");
                            root[field.Item1] = secret;
                        }
                    }
                    SaveModels(root);
                    return Results.Json(new { saved = true, revision = ModelRevision(), message = "已保存桌面共享媒体配置；Web 任务服务的运行地址仍由服务启动配置决定" });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                { return Error(503, "SETTINGS_SAVE_FAILED", "媒体配置保存失败"); }
            }
        });
        app.MapGet("/api/web/settings", () =>
        {
            string path;
            try { path = AiProviderSettings.FilePath; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Error(500, "SETTINGS_PATH_INVALID", "模型配置路径解析失败");
            }

            if (!File.Exists(path))
                return Results.Json(new
                {
                    configured = false,
                    note = "这台服务端机器上还没有模型配置文件（ai-config.json）："
                        + "在桌面端配一次即可，或用 YEEYEEYEE_CONFIG 指一份过去。"
                });

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                return Results.Json(new
                {
                    configured = true,
                    selectedProfileId = Text(root, "SelectedProfileId"),
                    active = new
                    {
                        endpoint = Text(root, "Endpoint"),
                        model = Text(root, "Model"),
                        apiFormat = Text(root, "ApiFormat"),
                        // 「配没配」与「配的是什么」是两件事：这里只回答前者。
                        hasKey = HasValue(root, "ApiKey")
                    },
                    providers = Array(root, "Profiles", profile => (object)new
                    {
                        id = Text(profile, "Id"),
                        displayName = Text(profile, "DisplayName"),
                        endpoint = Text(profile, "Endpoint"),
                        model = Text(profile, "Model"),
                        enabled = !(TryGet(profile, "Enabled", out var enabled) &&
                            enabled.ValueKind == JsonValueKind.False),
                        hasKey = HasValue(profile, "ApiKey")
                    }),
                    media = new
                    {
                        imageEndpoint = Text(root, "ImageEndpoint"),
                        imageModel = Text(root, "ImageModel"),
                        imageHasKey = HasValue(root, "ImageApiKey"),
                        comfyUiBaseUrl = Text(root, "ComfyUiBaseUrl"),
                        comfyUiCheckpoint = Text(root, "ComfyUiCheckpoint"),
                        videoEndpoint = Text(root, "VideoEndpoint"),
                        videoModel = Text(root, "VideoModel")
                    },
                    collaboration = new
                    {
                        serverUrl = Text(root, "CollaborationServerUrl"),
                        account = Text(root, "CollaborationAccount"),
                        autoSync = Flag(root, "CollaborationAutoSync")
                    },
                    disabledSkills = Strings(root, "DisabledBuiltInSkills")
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return Error(500, "SETTINGS_READ_FAILED", "模型配置文件读取失败或格式无效");
            }
        });

        // ComfyUI 此刻到底通不通。**只读、且要登录**：路径挂在 /api/web 下面所以走同一道守卫——
        // 探针失败时正文里会带上后端地址，那是内部信息，没有理由让匿名请求问到。
        app.MapGet("/api/web/settings/comfyui", async (
            IHttpClientFactory factory,
            IConfiguration configuration,
            HttpContext context) =>
        {
            var http = factory.CreateClient("comfyui");
            var timeout = configuration.GetValue("ComfyUI:SelfCheckTimeout", TimeSpan.FromSeconds(5));
            var result = await ComfyUiSelfCheck.ProbeAsync(http, timeout, context.RequestAborted);
            return Results.Json(new
            {
                reachable = result.Reachable,
                baseUrl = http.BaseAddress?.ToString() ?? string.Empty,
                version = result.Version,
                device = result.Device,
                detail = result.Detail
            });
        });
    }

    /// <summary>
    /// 按名字取一个属性，**大小写不敏感**：写盘用的是默认命名（PascalCase），
    /// 但那是一条实现细节——哪天加了命名策略，这条只读接口不该跟着一起哑掉。
    /// </summary>
    private static bool TryGet(JsonElement node, string name, out JsonElement value)
    {
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out value)) return true;
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    /// <summary>
    /// 取一个**标量**并渲染成字符串。枚举在盘上是数字（默认序列化不写字符串枚举），
    /// 所以要连数字一起收——只认字符串的话，`apiFormat` 会静默变成空串。
    /// </summary>
    private static string Text(JsonElement node, string name)
    {
        if (!TryGet(node, name, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static bool HasValue(JsonElement node, string name) => Text(node, name).Length > 0;

    private static bool Flag(JsonElement node, string name) =>
        TryGet(node, name, out var value) && value.ValueKind == JsonValueKind.True;

    private static List<string> Strings(JsonElement node, string name) =>
        TryGet(node, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToList()
            : new List<string>();

    private static List<object> Array(JsonElement node, string name, Func<JsonElement, object> project) =>
        TryGet(node, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(project).ToList()
            : new List<object>();
}
