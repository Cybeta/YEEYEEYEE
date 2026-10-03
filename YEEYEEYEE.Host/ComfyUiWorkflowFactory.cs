using System.Text.Json;
using System.Text.Json.Nodes;
using YEEYEEYEE.Core;

namespace YEEYEEYEE.Host;

public sealed class ComfyUiWorkflowFactory
{
    private readonly string? checkpoint;

    public ComfyUiWorkflowFactory(string? checkpoint = null)
    {
        this.checkpoint = checkpoint;
    }

    public JsonElement Create(Invocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        // 用户在某台服务器的某份工作流里选过时走这一支：那张图从网页导出来是什么样，
        // 提交上去就是什么样，我们只把参数写进**认出来的那几个输入**里。
        //
        // 放在最前面：内置模板是「我们自己搭的最小可跑图」，而用户选的是**这台服务器上真实存在的**工作流
        // （装了哪些自定义节点、用哪个底模，只有它自己知道）。两者都在时，用户的选择说了算。
        if (GetString(invocation, "workflowTemplate") is { Length: > 0 } template)
            return BindTemplate(template, invocation);

        var prompt = GetString(invocation, "prompt")
            ?? GetString(invocation, "text")
            ?? throw new InvalidOperationException("Invocation 缺少 prompt 输入");
        var negative = GetString(invocation, "negativePrompt")
            ?? GetString(invocation, "negative_prompt")
            ?? string.Empty;
        var width = GetInt(invocation, "width", 512, 64, 2048);
        var height = GetInt(invocation, "height", 512, 64, 2048);
        var steps = GetInt(invocation, "steps", 20, 1, 150);
        var cfg = GetDouble(invocation, "cfg", 7, 1, 30);
        var seed = GetLong(invocation, "seed", Random.Shared.NextInt64(0, long.MaxValue), 0, long.MaxValue);
        var model = GetString(invocation, "checkpoint") ?? checkpoint
            ?? throw new InvalidOperationException("未配置 ComfyUI checkpoint");
        // 参考图按列表接收：能用几张由工作流模板决定，画布与请求层不设上限。
        var referenceImages = GetStringList(invocation, "referenceImages");
        if (referenceImages.Count == 0)
        {
            // 兼容旧的单图输入。
            var single = GetString(invocation, "referenceImage");
            if (!string.IsNullOrWhiteSpace(single)) referenceImages.Add(single);
        }
        var referenceMode = GetString(invocation, "referenceMode") ?? "img2img";
        var useReference = referenceImages.Count > 0;
        if (useReference && !string.Equals(referenceMode, "img2img", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"未实现参考图模式 {referenceMode}，请检查工作流配置。");
        var denoise = GetDouble(invocation, "denoise", useReference ? 0.6 : 1.0, 0.05, 1.0);

        var workflow = new JsonObject
        {
            ["3"] = new JsonObject
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["seed"] = seed,
                    ["steps"] = steps,
                    ["cfg"] = cfg,
                    ["sampler_name"] = "euler",
                    ["scheduler"] = "normal",
                    ["denoise"] = denoise,
                    ["model"] = new JsonArray("4", 0),
                    ["positive"] = new JsonArray("6", 0),
                    ["negative"] = new JsonArray("7", 0),
                    ["latent_image"] = new JsonArray("5", 0)
                }
            },
            ["4"] = new JsonObject
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new JsonObject { ["ckpt_name"] = model }
            },
            ["6"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JsonObject { ["text"] = prompt, ["clip"] = new JsonArray("4", 1) }
            },
            ["7"] = new JsonObject
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new JsonObject { ["text"] = negative, ["clip"] = new JsonArray("4", 1) }
            },
            ["8"] = new JsonObject
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new JsonObject { ["samples"] = new JsonArray("3", 0), ["vae"] = new JsonArray("4", 2) }
            },
            ["9"] = new JsonObject
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new JsonObject { ["filename_prefix"] = "yeeeyee", ["images"] = new JsonArray("8", 0) }
            }
        };

        // 目前只实现「单张底图」的 img2img 模板：取第一张参考图经 VAEEncode 进入 KSampler，
        // denoise 控制重绘强度。需要多图合成（例如 IPAdapter）时，在此按 referenceMode 增加分支
        // 并使用 referenceImages 的全部图片——画布、请求与执行器都无需改动。
        if (useReference)
        {
            workflow["10"] = new JsonObject
            {
                ["class_type"] = "LoadImage",
                ["inputs"] = new JsonObject { ["image"] = referenceImages[0] }
            };
            workflow["5"] = new JsonObject
            {
                ["class_type"] = "VAEEncode",
                ["inputs"] = new JsonObject { ["pixels"] = new JsonArray("10", 0), ["vae"] = new JsonArray("4", 2) }
            };
        }
        else
        {
            workflow["5"] = new JsonObject
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }
            };
        }

        return JsonDocument.Parse(workflow.ToJsonString()).RootElement.Clone();
    }

    /// <summary>
    /// 把参数写进用户选的那份工作流。
    ///
    /// 槽位（哪个节点收什么）由调用方在**参考图上传之后**才绑值——上传会把本机路径换成
    /// ComfyUI 侧的文件名，绑值必须发生在换名之后，否则写进去的是本机路径，服务端找不到那个文件。
    /// 所以这里拿到的是「已经换好名的」referenceImages。
    /// </summary>
    private static JsonElement BindTemplate(string template, Invocation invocation)
    {
        ComfyUiWorkflowSlots slots;
        try
        {
            slots = invocation.Inputs.TryGetValue("workflowSlots", out var declared)
                    && declared.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<ComfyUiWorkflowSlots>(declared.GetString()!) ?? ComfyUiWorkflowBinder.Detect(template)
                : ComfyUiWorkflowBinder.Detect(template);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("工作流的槽位读不懂，没法确定参数该放哪：" + error.Message);
        }

        var images = GetStringList(invocation, "referenceImages");
        var bound = ComfyUiWorkflowBinder.Bind(template, slots, new ComfyUiBindValues
        {
            Prompt = GetString(invocation, "prompt") ?? string.Empty,
            Negative = GetString(invocation, "negativePrompt") ?? GetString(invocation, "negative_prompt") ?? string.Empty,
            Width = GetInt(invocation, "width", 0, 0, 8192),
            Height = GetInt(invocation, "height", 0, 0, 8192),
            Seed = TryGetSeed(invocation),
            // 认不出底图入口时 Bind 不会写它，所以这里给不给都安全。
            ImageName = images.Count > 0 ? images[0] : string.Empty
        });

        return JsonDocument.Parse(bound.ToJsonString()).RootElement.Clone();
    }

    private static long? TryGetSeed(Invocation invocation) =>
        invocation.Inputs.TryGetValue("seed", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static List<string> GetStringList(Invocation invocation, string name)
    {
        var results = new List<string>();
        if (!invocation.Inputs.TryGetValue(name, out var value)) return results;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    results.Add(item.GetString()!);
        }
        else if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            results.Add(value.GetString()!);
        }
        return results;
    }

    private static string? GetString(Invocation invocation, string name)
        => invocation.Inputs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(Invocation invocation, string name, int fallback, int min, int max)
    {
        if (!invocation.Inputs.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return fallback;
        var result = value.GetInt32();
        return Math.Clamp(result, min, max);
    }

    private static long GetLong(Invocation invocation, string name, long fallback, long min, long max)
    {
        if (!invocation.Inputs.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return fallback;
        var result = value.GetInt64();
        return Math.Clamp(result, min, max);
    }

    private static double GetDouble(Invocation invocation, string name, double fallback, double min, double max)
    {
        if (!invocation.Inputs.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return fallback;
        var result = value.GetDouble();
        return Math.Clamp(result, min, max);
    }
}
