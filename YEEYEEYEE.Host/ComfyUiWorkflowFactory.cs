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

        // 内置模板**只出图**（KSampler → VAEDecode → SaveImage），一个视频节点都没有。
        // 出视频时不带工作流模板走到这里，就等于拿一张静图去冒充视频——所以如实拒绝。
        // 这一句是给「挑了池子却走成工作流」这类接线错误兜底的，正常情况下上面那一支已经接住了。
        if (invocation.Capability is Capability.TextToVideo or Capability.ImageToVideo)
            throw new InvalidOperationException(
                "出视频必须带一份选定的 ComfyUI 工作流：内置模板只出图，里面没有任何视频节点。");

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
            // 时长与比例：出视频那两条链会带。给了就写（帧数是**已经按帧率换算好**的），
            // 没给（例如出图）就是不写——不写等于沿用工作流自己的设定，那是它的正路。
            Length = TryGetFrames(invocation),
            Seconds = TryGetSeconds(invocation),
            AspectRatio = GetString(invocation, "aspectRatio") ?? string.Empty,
            // 认不出底图入口时 Bind 不会写它，所以这里给不给都安全。
            ImageName = images.Count > 0 ? images[0] : string.Empty,
            // 整份列表一起给：一份工作流有多个底图入口时，按顺序各收一张
            //（顺序由装配那一侧定死：角色 → 道具 → 场景）。
            ImageNames = images
        });

        return JsonDocument.Parse(bound.ToJsonString()).RootElement.Clone();
    }

    private static long? TryGetSeed(Invocation invocation) =>
        invocation.Inputs.TryGetValue("seed", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    /// <summary>
    /// 这次要写的帧数。**上界放到 20000**：这不是「图片边长」那种量级的数，
    /// 一段两分钟、24fps 的视频就是 2881 帧，砍到几百帧会让长视频那条路直接失效。
    /// </summary>
    private static int? TryGetFrames(Invocation invocation)
    {
        if (!invocation.Inputs.TryGetValue("videoFrames", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number) return null;
        var frames = value.GetInt32();
        return frames > 1 ? Math.Min(frames, 20_000) : null;
    }

    /// <summary>这次要写的**秒数**（帧数由那份工作流自己的表达式折出来时用）。</summary>
    private static double? TryGetSeconds(Invocation invocation)
    {
        if (!invocation.Inputs.TryGetValue("videoSeconds", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number) return null;
        var seconds = value.GetDouble();
        return seconds > 0 ? Math.Min(seconds, 600) : null;
    }

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
