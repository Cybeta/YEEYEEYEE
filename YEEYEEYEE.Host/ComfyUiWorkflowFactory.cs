using System.Text.Json;
using System.Text.Json.Nodes;
using YEEYEEYEE.Core;

namespace YEEYEEYEE.Host;

public sealed class ComfyUiWorkflowFactory
{
    private readonly string? checkpoint;
    private readonly string? ltxCheckpoint;

    public ComfyUiWorkflowFactory(string? checkpoint = null, string? ltxCheckpoint = null)
    {
        this.checkpoint = checkpoint;
        this.ltxCheckpoint = ltxCheckpoint;
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

        if (invocation.Capability == Capability.TextToVideo)
            return CreateLtxTextToVideo(invocation);

        if (invocation.Capability == Capability.ImageToVideo)
            throw new InvalidOperationException("图生视频必须带一份选定的 ComfyUI 工作流。");

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

    private JsonElement CreateLtxTextToVideo(Invocation invocation)
    {
        var prompt = GetString(invocation, "prompt") ?? GetString(invocation, "text") ?? string.Empty;
        var model = GetString(invocation, "model") ?? ltxCheckpoint
            ?? "ltx-2.3-22b-distilled-1.1.safetensors";
        var width = GetInt(invocation, "width", 256, 64, 2048);
        var height = GetInt(invocation, "height", 256, 64, 2048);
        var frames = GetInt(invocation, "videoFrames", 17, 1, 4096);
        var seed = GetLong(invocation, "seed", Random.Shared.NextInt64(0, long.MaxValue), 0, long.MaxValue);

        var workflow = new JsonObject
        {
            ["1"] = new JsonObject
            {
                ["class_type"] = "DenoLTX23PresetLoader",
                ["inputs"] = new JsonObject
                {
                    ["pipeline_mode"] = "Checkpoint Style",
                    ["checkpoint_name"] = model,
                    ["diffusion_model_name"] = "Ltx/ltx-2.3-22b-distilled-1.1_transformer_only_bf16.safetensors",
                    ["gguf_unet_name"] = "__none__",
                    ["video_vae_name"] = "LTX23_video_vae_bf16.safetensors",
                    ["audio_vae_name"] = "LTX23_audio_vae_bf16.safetensors",
                    ["text_encoder_name"] = "gemma_3_12B_it_fp4_mixed.safetensors",
                    ["text_projection_name"] = "ltx-2.3_text_projection_bf16.safetensors",
                    ["clip_device"] = "default",
                    ["weight_dtype"] = "default"
                }
            },
            ["2"] = new JsonObject
            {
                ["class_type"] = "easy ltxMultiTrackEncode",
                ["inputs"] = new JsonObject
                {
                    ["model"] = new JsonArray("1", 0),
                    ["clip"] = new JsonArray("1", 1),
                    ["audio_vae"] = new JsonArray("1", 3),
                    ["local_prompt"] = prompt,
                    ["global_prompt"] = prompt,
                    ["epsilon"] = 0.001,
                    ["width"] = width,
                    ["height"] = height,
                    ["frame_rate"] = 24.0,
                    ["video_length"] = frames,
                    ["half_latent_size"] = false
                }
            },
            ["3"] = new JsonObject
            {
                ["class_type"] = "BasicScheduler",
                ["inputs"] = new JsonObject
                {
                    ["model"] = new JsonArray("2", 0),
                    ["scheduler"] = "simple",
                    ["steps"] = 8,
                    ["denoise"] = 1.0
                }
            },
            ["4"] = new JsonObject
            {
                ["class_type"] = "easy ltxSamplerSimple",
                ["inputs"] = new JsonObject
                {
                    ["model"] = new JsonArray("2", 0),
                    ["positive"] = new JsonArray("2", 1),
                    ["negative"] = new JsonArray("2", 2),
                    ["video_latent"] = new JsonArray("2", 3),
                    ["audio_latent"] = new JsonArray("2", 4),
                    ["sampler_name"] = "euler",
                    ["sigmas"] = new JsonArray("3", 0),
                    ["cfg"] = 1.0,
                    ["seed"] = seed
                }
            },
            ["5"] = new JsonObject
            {
                ["class_type"] = "LTXVTiledVAEDecode",
                ["inputs"] = new JsonObject
                {
                    ["vae"] = new JsonArray("1", 2),
                    ["latents"] = new JsonArray("4", 2),
                    ["horizontal_tiles"] = 1,
                    ["vertical_tiles"] = 1,
                    ["overlap"] = 1,
                    ["last_frame_fix"] = false,
                    ["working_device"] = "auto",
                    ["working_dtype"] = "auto"
                }
            },
            ["6"] = new JsonObject
            {
                ["class_type"] = "LTXVAudioVAEDecode",
                ["inputs"] = new JsonObject
                {
                    ["samples"] = new JsonArray("4", 3),
                    ["audio_vae"] = new JsonArray("1", 3)
                }
            },
            ["7"] = new JsonObject
            {
                ["class_type"] = "VHS_VideoCombine",
                ["inputs"] = new JsonObject
                {
                    ["images"] = new JsonArray("5", 0),
                    ["audio"] = new JsonArray("6", 0),
                    ["frame_rate"] = 24.0,
                    ["loop_count"] = 0,
                    ["filename_prefix"] = "trae_ltx23_web",
                    ["format"] = "video/h264-mp4",
                    ["pingpong"] = false,
                    ["save_output"] = true,
                    ["pix_fmt"] = "yuv420p",
                    ["crf"] = 23,
                    ["save_metadata"] = true,
                    ["trim_to_audio"] = false
                }
            }
        };
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
            ImageNames = images,
            // 源视频 / 源音频（已经上传过的服务器侧文件名）：视频二创、对口型、视频修复那一支吃的是
            // **一段片子**（对口型还要一段音），底图那条路对它们没用。
            VideoNames = GetStringList(invocation, "referenceVideos"),
            AudioNames = GetStringList(invocation, "referenceAudios"),
            FirstFrameName = GetString(invocation, "firstFrame") ?? string.Empty,
            LastFrameName = GetString(invocation, "lastFrame") ?? string.Empty,
            ControlNetNames = GetStringList(invocation, "controlNetImages"),
            PoseNames = GetStringList(invocation, "poseImages"),
            DepthNames = GetStringList(invocation, "depthImages"),
            IpAdapterNames = GetStringList(invocation, "ipAdapterImages"),
            BatchSize = GetInt(invocation, "batchSize", 0, 0, 64)
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
