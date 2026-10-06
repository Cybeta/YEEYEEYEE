using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>
/// 网页端「看得见设置」那一页的数据。
///
/// **只读，而且读了也不给密钥。** 两个理由，都不是偷懒：
/// ① 密钥在盘上是 DPAPI 密文（桌面端 <c>SecretProtector</c>），而 DPAPI 只在 Windows 上有——
///    这个服务端的目标框架是 net10.0（为了跑在 Linux 容器里），**根本解不开**；
/// ② 就算解得开也不该解：密钥属于桌面端那台机器，挂在公网上的网页端没有理由拿着它。
///
/// 所以这里**直接读那份 JSON 文件**，绕开 <see cref="AiProviderSettings.Load"/>（那个会去解密钥），
/// 只投影「配了什么」：端点、模型、以及「密钥配没配」这个**布尔**。改设置仍然只能到桌面端——
/// 那不是这一版没做完，是这条链的边界。
/// </summary>
internal static class WebSettingsApi
{
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public static void Map(WebApplication app)
    {
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
