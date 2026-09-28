using System.Text.Json;
using System.Text.Json.Nodes;

var server = new McpServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
await server.RunAsync();

internal sealed class McpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly Stream input;
    private readonly Stream output;
    private readonly McpReadOnlyTools tools = new();

    public McpServer(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    public async Task RunAsync()
    {
        using var reader = new StreamReader(input);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException)
            {
                await WriteAsync(McpResponse.Error(null, -32700, "Invalid JSON."));
                continue;
            }

            if (request is not JsonObject obj)
            {
                await WriteAsync(McpResponse.Error(null, -32600, "Request must be a JSON object."));
                continue;
            }

            var method = obj["method"]?.GetValue<string>();
            var id = obj["id"]?.DeepClone();
            if (id is null && string.Equals(method, "notifications/initialized", StringComparison.Ordinal)) continue;

            try
            {
                var result = await DispatchAsync(method, obj["params"] as JsonObject);
                if (id is not null) await WriteAsync(McpResponse.Success(id, result));
            }
            catch (McpToolException error)
            {
                if (id is not null) await WriteAsync(McpResponse.Error(id, error.Code, error.Message));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
            {
                if (id is not null) await WriteAsync(McpResponse.Error(id, -32602, error.Message));
            }
        }
    }

    private Task<JsonNode> DispatchAsync(string? method, JsonObject? parameters) => method switch
    {
        "initialize" => Task.FromResult<JsonNode>(new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "DreamForge", ["version"] = "0.1.0" }
        }),
        "ping" => Task.FromResult<JsonNode>(new JsonObject()),
        "tools/list" => Task.FromResult<JsonNode>(tools.List()),
        "tools/call" => Task.FromResult<JsonNode>(tools.Call(parameters)),
        _ => throw new McpToolException(-32601, $"Method not found: {method ?? "(missing)"}")
    };

    private async Task WriteAsync(JsonNode response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        await output.WriteAsync(bytes);
        await output.WriteAsync(new byte[] { (byte)'\n' });
        await output.FlushAsync();
    }
}

internal sealed class McpReadOnlyTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public JsonObject List() => new()
    {
        ["tools"] = new JsonArray
        {
            Tool("project.get_info", "读取项目描述信息。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["rootPath"] = StringSchema("项目根目录。") },
                ["required"] = new JsonArray("rootPath")
            }),
            Tool("project.list_files", "列出项目根目录内的文件，不返回项目外路径。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["rootPath"] = StringSchema("项目根目录。"),
                    ["relativeDirectory"] = StringSchema("项目内相对目录，默认为根目录。"),
                    ["maxResults"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 5000 }
                },
                ["required"] = new JsonArray("rootPath")
            }),
            Tool("canvas.list_tabs", "列出项目画布库，可作为画布标签列表。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["rootPath"] = StringSchema("项目根目录。") },
                ["required"] = new JsonArray("rootPath")
            }),
            Tool("canvas.get_state", "读取指定画布的节点、连线和画布元数据。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["rootPath"] = StringSchema("项目根目录。"),
                    ["canvas"] = StringSchema("画布文件名或项目内相对路径。")
                },
                ["required"] = new JsonArray("rootPath", "canvas")
            })
        }
    };

    public JsonObject Call(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new McpToolException(-32602, "tools/call 缺少 name。\n");
        var args = parameters["arguments"] as JsonObject ?? new JsonObject();
        var value = name switch
        {
            "project.get_info" => GetProjectInfo(args),
            "project.list_files" => ListFiles(args),
            "canvas.list_tabs" => ListTabs(args),
            "canvas.get_state" => GetCanvasState(args),
            _ => throw new McpToolException(-32602, $"未知工具: {name}")
        };
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString(JsonOptions) }),
            ["structuredContent"] = value
        };
    }

    private static JsonObject GetProjectInfo(JsonObject args)
    {
        var root = ProjectRoot(args);
        var path = SafePath(root, "project.json");
        if (!File.Exists(path)) throw new McpToolException(-32602, "项目根目录中不存在 project.json。\n");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new JsonObject
        {
            ["root"] = ".",
            ["project"] = JsonNode.Parse(document.RootElement.GetRawText()),
            ["hasCanvases"] = Directory.Exists(Path.Combine(root, "canvases")),
            ["directories"] = new JsonArray("canvases", "assets", "skills", "plugins")
        };
    }

    private static JsonObject ListFiles(JsonObject args)
    {
        var root = ProjectRoot(args);
        var relative = args["relativeDirectory"]?.GetValue<string>() ?? ".";
        var directory = SafePath(root, relative);
        if (!Directory.Exists(directory)) throw new McpToolException(-32602, "项目内目录不存在。\n");
        var max = Math.Clamp(args["maxResults"]?.GetValue<int>() ?? 500, 1, 5000);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Take(max)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fileNodes = files.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray();
        return new JsonObject { ["files"] = new JsonArray(fileNodes), ["truncated"] = files.Length == max };
    }

    private static JsonObject ListTabs(JsonObject args)
    {
        var root = ProjectRoot(args);
        var directory = Path.Combine(root, "canvases");
        if (!Directory.Exists(directory)) return new JsonObject { ["tabs"] = new JsonArray() };
        var tabs = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            if (!TryReadCanvas(file, out var canvas)) continue;
            tabs.Add(new JsonObject
            {
                ["id"] = Path.GetFileName(file),
                ["title"] = canvas?["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file),
                ["path"] = Path.GetRelativePath(root, file).Replace('\\', '/'),
                ["modifiedAt"] = File.GetLastWriteTimeUtc(file).ToString("O")
            });
        }
        return new JsonObject { ["tabs"] = tabs };
    }

    private static JsonObject GetCanvasState(JsonObject args)
    {
        var root = ProjectRoot(args);
        var requested = args["canvas"]?.GetValue<string>() ?? throw new McpToolException(-32602, "缺少 canvas。\n");
        var path = SafePath(root, requested);
        if (!File.Exists(path)) throw new McpToolException(-32602, "指定画布不存在。\n");
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) throw new McpToolException(-32602, "画布必须是 JSON 文件。\n");
        if (!TryReadCanvas(path, out var canvas) || canvas is null) throw new McpToolException(-32602, "画布 JSON 无效。\n");
        return new JsonObject
        {
            ["path"] = Path.GetRelativePath(root, path).Replace('\\', '/'),
            ["state"] = canvas
        };
    }

    private static string ProjectRoot(JsonObject args)
    {
        var raw = args["rootPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(raw)) throw new McpToolException(-32602, "缺少 rootPath。\n");
        var root = Path.GetFullPath(raw);
        if (!Directory.Exists(root)) throw new McpToolException(-32602, "项目根目录不存在。\n");
        return root;
    }

    private static string SafePath(string root, string relativeOrPath)
    {
        var candidate = Path.IsPathRooted(relativeOrPath) ? Path.GetFullPath(relativeOrPath) : Path.GetFullPath(Path.Combine(root, relativeOrPath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new McpToolException(-32602, "路径必须位于项目根目录内。\n");
        return candidate;
    }

    private static bool TryReadCanvas(string path, out JsonObject? canvas)
    {
        canvas = null;
        try { canvas = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; return canvas is not null; }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    private static JsonObject StringSchema(string description) => new() { ["type"] = "string", ["description"] = description };
}

internal sealed class McpToolException : Exception
{
    public int Code { get; }
    public McpToolException(int code, string message) : base(message) => Code = code;
}

internal static class McpResponse
{
    public static JsonObject Success(JsonNode id, JsonNode? result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    public static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
