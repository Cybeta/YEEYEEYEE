using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using YEEYEEYEE.Desktop;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 设置里的「生图 / 生视频接口」页。
///
/// 为什么单独做一页、而不是并进模型接入设置：出图与出视频是**另外两条链路**，
/// 各自的地址、模型、密钥都能独立配置（常见情况是「聊天用 A 家、出图用 B 家、ComfyUI 在本地」）。
/// 把它们塞进「聊天用哪个模型」那一页，会让人误以为改了聊天模型就顺带改了出图链路；
/// 所以这一页在开头就把这层关系挑明，字段说明也只写「什么时候生效」。
///
/// 页内的编辑都发生在传进来的 <see cref="AiProviderConfig"/> 工作副本上，
/// 由调用方拿 <c>Commit</c> 决定何时写回与落盘；这里不主动保存，避免半截配置被写进磁盘。
/// </summary>
internal static class SettingsMediaPage
{
    /// <summary>
    /// 构建「生图 / 生视频接口」页。
    /// 返回根控件、一个 Commit（把界面上的编辑写回 config）、一个 Reload（从 config 重新回显）。
    /// </summary>
    public static (Control Root, Action Commit, Action Reload) Build(
        YEEYEEYEE.Desktop.AiProviderConfig config,
        SettingsPageContext context)
    {
        // 页内的写法保持原样：外壳的能力只在开头取一次别名，正文不必到处写 context.xxx。
        var owner = context.Owner;
        var report = context.Report;
        Func<bool> saveToDisk = context.SaveAll;

        var root = new StackPanel { Spacing = 8, Margin = new Thickness(20) };

        // 读输入框文本时统一按「空串而非 null」处理：Avalonia 的 Text 是可空的，
        // 直接赋 null 会把 config 里的字符串属性变成 null，后面 IsNullOrWhiteSpace 之外的地方容易炸。
        string TextOf(TextBox box) => box.Text ?? string.Empty;

        // 分块之间的细分隔线：这些字段彼此独立（图像 / ComfyUI / 视频），
        // 光靠加粗标题在长面板里不够，给一条线让「这一块到此为止」一眼可见。
        void Divider() => root.Children.Add(new Border
        {
            Height = 1,
            Background = Brush("DfLine"),
            Margin = new Thickness(0, 6, 0, 0)
        });

        // 字段 = 标题 + 输入框 + 一句说明。
        // 说明紧跟在输入框下面（而不是集中写在页尾）：这些开关什么时候生效，光看字段名猜不出来，
        // 离得越近越不容易被当成「改完立刻对正在跑的出图生效」。
        TextBox AddField(string label, string value, string note, string? watermark = null, bool password = false)
        {
            var field = Field(label, value, watermark);
            // 复用 Field 拿到统一样式，再补上密码行为；密钥不该明文回显在屏幕上。
            if (password) field.Box.PasswordChar = '●';
            root.Children.Add(field.Label);
            root.Children.Add(field.Box);
            root.Children.Add(Note(note));
            return field.Box;
        }

        // ---------- 页首：先把「这一页管什么」讲清楚 ----------
        root.Children.Add(Note(
            "这一页只管出图 / 出视频的接口，跟「聊天用哪个模型」无关：出图、出视频是另外两条链路，" +
            "地址、模型、密钥都可以单独填；只有留空时才会沿用当前选中模型的密钥。"));

        // ---------- 智能导入 ----------
        // 放在最顶端：这一页的字段是「知道地址和模型之后填的」，而多数人是先拿到一份接口文档。
        // 把它摆在字段前面，等于把「先读文档、再自动填」这条顺序摆到了台面上。
        var smartImport = Primary("智能导入：给一个接口说明网页");
        smartImport.HorizontalAlignment = HorizontalAlignment.Left;
        smartImport.Click += async (_, _) => await RunSmartImportAsync();
        root.Children.Add(new Border
        {
            Background = Brush("DfPrimarySoft"),
            BorderBrush = Brush("DfLineGlow"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    smartImport,
                    Note("流程：抓取该网页（抓不到就粘贴正文）→ 解析出接口与可用模型 → 建出「生图池1 1K」这类技能" +
                         "→ 写密钥与地址模型（加密落盘）→ 问一次要不要做一次最小测试以确认接口真的可用。" +
                         "本地规则解析不出来时，可以让已接入的大模型把正文整理成结构化 JSON。")
                }
            }
        });

        // ---------- 图像接口 ----------
        root.Children.Add(Header("图像接口"));
        var imageEndpoint = AddField(
            "图像接口地址（留空则复用文本接口地址）",
            config.ImageEndpoint,
            "填了只影响出图这条链路；留空时按文本接口地址推导，这样只做图生图、不换服务商时不用重复填。",
            "https://…（留空复用文本接口）");
        var imageModel = AddField(
            "图像模型名",
            config.ImageModel,
            "填上它、并且能解析出地址，图像链路才算配置好；留空时出图会提示「未配置图像模型」。",
            "例如 dall-e-3 / flux / sd-xl");
        var imageApiKey = AddField(
            "图像接口密钥",
            config.ImageApiKey,
            "留空则沿用当前选中模型的密钥；填了只用于出图，聊天那条链路不受影响。",
            "留空则沿用当前选中模型的密钥",
            password: true);
        var imageSize = AddField(
            "图像尺寸（宽x高）",
            config.ImageSize,
            "作为 size 参数发给图像接口；实际支持哪些尺寸由模型决定，服务端不接受会直接返回错误。",
            "1024x1024");
        var imageMaxRefs = AddField(
            "一次最多参考图数（0 表示不限制）",
            config.ImageMaxReferenceImages.ToString(),
            "这是接口（模型）一次能吃几张参考图的能力，不是画布的限制；ComfyUI 链路的上限由工作流模板声明，不使用这个值。",
            "0");
        var imageSteps = AddField(
            "默认步数（节点未单独设置时使用）",
            config.DefaultImageSteps,
            "只在单个节点没有自己指定步数时生效；留空表示不发送步数，由服务端或工作流用默认值。",
            "留空则不发送");
        var imageCfg = AddField(
            "默认 CFG（节点未单独设置时使用）",
            config.DefaultImageCfg,
            "只在单个节点没有自己指定 CFG 时生效；留空表示不发送，由服务端用默认值。",
            "留空则不发送");
        var imageNegative = AddField(
            "默认负面词（节点未单独设置时使用）",
            config.DefaultNegativePrompt,
            "只在单个节点没有写自己的负面词时生效；留空表示不加负面词。",
            "留空则不加");

        var testImage = Secondary("测试连接");
        testImage.HorizontalAlignment = HorizontalAlignment.Left;
        testImage.Click += async (_, _) =>
        {
            // 先落盘再去测：链路工厂（ImageProviderFactory）是**重新从磁盘读配置**的，
            // 不先保存的话，测的是磁盘上那份旧配置，界面上的新改动根本没参与，等于白测。
            if (!saveToDisk())
                report("配置没能写盘，测试用的是磁盘上旧的那份", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);

            IImageProvider provider;
            try
            {
                provider = ImageProviderFactory.Create();
            }
            catch (Exception error)
            {
                report($"图像链路创建失败：{error.Message}", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Error);
                return;
            }

            if (!provider.IsConfigured)
            {
                report(
                    "图像链路还没配置好：在「图像接口」里填上图像模型（地址可留空复用文本接口），" +
                    "或改用「复制提示词」把提示词拿到别处出图。",
                    YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);
                return;
            }

            report($"正在出图测试（{provider.Name} · {config.ImageModel}），最多等 5 分钟…", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Info);
            var watch = Stopwatch.StartNew();
            try
            {
                // 真的发一次最小出图请求：只有拿到真图才算这条链路通，
                // 光看「配置填了」证明不了地址、密钥、模型名三者都对。
                var result = await provider.GenerateAsync(new ImageGenerationRequest
                {
                    Prompt = "test",
                    NegativePrompt = "",
                    Width = 512,
                    Height = 512
                });
                watch.Stop();

                if (result.Status != ImageGenerationStatus.Succeeded || string.IsNullOrWhiteSpace(result.FilePath))
                {
                    report(
                        $"图像测试失败：{(string.IsNullOrWhiteSpace(result.Error) ? "接口没有返回可用的图片数据。" : result.Error)}" +
                        $"（耗时 {watch.ElapsedMilliseconds} ms）",
                        YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Error);
                    return;
                }

                report(
                    $"图像测试成功：耗时 {watch.ElapsedMilliseconds} ms，产出 {Path.GetFileName(result.FilePath)}" +
                    $"（{result.Provider} · {result.Model}）。",
                    YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Success);
            }
            catch (Exception error)
            {
                // 测试按钮的职责就是把失败如实摆出来，不在这里吞掉任何异常；
                // 具体是超时、DNS 还是鉴权，交给下面的消息原文说明。
                watch.Stop();
                report($"图像测试异常：{error.Message}（耗时 {watch.ElapsedMilliseconds} ms）", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Error);
            }
        };
        root.Children.Add(testImage);

        // ---------- ComfyUI ----------
        Divider();
        root.Children.Add(Header("ComfyUI"));
        var comfyUrl = AddField(
            "ComfyUI 地址",
            config.ComfyUiBaseUrl,
            "填了它并配好 checkpoint，才具备走本地 ComfyUI 出图的前提；只填地址不算配置完成。",
            "http://127.0.0.1:8188");
        var comfyCheckpoint = AddField(
            "Checkpoint 文件名",
            config.ComfyUiCheckpoint,
            "工作流里加载的模型文件名；必须与 ComfyUI 机器上实际存在的文件名一致，写错会让任务在跑的时候才失败。",
            "例如 sd_xl_base_1.0.safetensors");
        var comfyClientId = AddField(
            "客户端 ID",
            config.ComfyUiClientId,
            "提交任务时带上，用于把 ComfyUI 推回来的任务事件对回到本机这个客户端，多端连同一个 ComfyUI 时避免串台。",
            "yeeeyee-desktop");
        var assetDir = AddField(
            "资产目录（出图 / 出视频产物落盘位置）",
            config.AssetDirectory,
            "留空则用默认资产目录；换到别的盘或同步盘时，改这里即可，已产出的文件不会自动搬迁。",
            "留空则用默认目录");

        var pickAssetDir = Secondary("选择目录");
        pickAssetDir.HorizontalAlignment = HorizontalAlignment.Left;
        pickAssetDir.Click += async (_, _) =>
        {
            // 与 AgentSettingsDialog 里选工作目录同一套写法：用窗口的 StorageProvider，
            // 拿不到本机路径（例如云端虚拟位置）时就不改，免得写进去一个用不了的路径。
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择出图 / 出视频资产目录",
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) assetDir.Text = path;
        };
        root.Children.Add(pickAssetDir);

        var testComfy = Secondary("测试连接");
        testComfy.HorizontalAlignment = HorizontalAlignment.Left;
        testComfy.Click += (_, _) =>
        {
            if (!saveToDisk())
                report("配置没能写盘，测试用的是磁盘上旧的那份", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);

            IImageProvider provider;
            try
            {
                provider = ImageProviderFactory.Create();
            }
            catch (Exception error)
            {
                report($"图像链路创建失败：{error.Message}", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Error);
                return;
            }

            // ComfyUI 的测试只解析链路、不提交任务：ComfyUI 是异步出图，会真的排队跑工作流、
            // 占显卡也占时间，一个「测试连接」按钮不该顺手起一个任务。这里如实报出实际解析到的链路。
            if (provider.Name == "ComfyUI")
            {
                report(
                    $"链路就绪：ComfyUI（checkpoint={config.ComfyUiCheckpoint}）。本次只解析链路，没有提交生成任务。",
                    YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Success);
                return;
            }

            if (config.IsComfyUiConfigured)
            {
                // 地址与 checkpoint 都填了却没走到 ComfyUI：说明本地执行服务没起来（ComfyUI 没在跑），
                // 或者地址 / checkpoint 对不上，工厂因此回落到了云端链路。这里如实报出实际走的是谁。
                report(
                    $"ComfyUI 已在配置里填好，但这次解析到的链路是「{provider.Name}」而不是 ComfyUI：" +
                    "多半是 ComfyUI 没在运行（或地址 / checkpoint 对不上），本地执行服务起不来，于是回落到了云端链路。",
                    YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);
                return;
            }

            report(
                $"还没配好 ComfyUI：填上「ComfyUI 地址」与「Checkpoint 文件名」后才会优先走本地链路。" +
                $"当前解析到「{provider.Name}」。",
                YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);
        };
        root.Children.Add(testComfy);

        // ---------- 视频接口 ----------
        Divider();
        root.Children.Add(Header("视频接口"));
        var videoEndpoint = AddField(
            "视频接口地址（留空则复用文本接口地址）",
            config.VideoEndpoint,
            "视频常常是另一家云，建议单独填；留空才复用文本接口地址。",
            "https://…（留空复用文本接口）");
        var videoModel = AddField(
            "视频模型名",
            config.VideoModel,
            "填上它、并且能解析出地址，视频链路才算启用；留空表示没启用视频。",
            "例如 sora-2 / veo-3");
        var videoApiKey = AddField(
            "视频接口密钥",
            config.VideoApiKey,
            "留空则沿用当前选中模型的密钥；填了只用于出视频，聊天与出图都不受影响。",
            "留空则沿用当前选中模型的密钥",
            password: true);
        var videoMaxRefs = AddField(
            "一次最多参考帧数（0 表示不限制）",
            config.VideoMaxReferenceImages.ToString(),
            "这是视频接口一次能同时使用几张参考帧的能力，不是画布的限制。",
            "1");
        var videoSeconds = AddField(
            "默认时长（秒；0 表示由服务端默认）",
            config.VideoDefaultSeconds.ToString(),
            "没有单独指定时长时用的默认秒数；0 表示不传，由服务端决定。",
            "0");

        // 这句是「如实相告」而不是客套：视频是异步任务、要轮询、也可能按次计费，
        // 用户看到「测试连接」很自然会以为它会跑一次生成，必须先说清不会。
        root.Children.Add(Note(
            "说明：视频接口的「测试连接」只验证链路是否已配置（地址 + 模型），" +
            "不会真的提交生成任务——视频是异步任务，要花钱、还要轮询，一个测试按钮不该顺手起这个任务。" +
            "真要出视频：在分镜节点上右键选「出这一镜的视频」，它按「提交 → 轮询 → 下载」走，" +
            "首帧默认用节点上最新那张图（= 这一镜的画面）。"));

        var testVideo = Secondary("测试连接");
        testVideo.HorizontalAlignment = HorizontalAlignment.Left;
        testVideo.Click += (_, _) =>
        {
            if (!saveToDisk())
                report("配置没能写盘，测试用的是磁盘上旧的那份", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);

            // 只创建工厂看配置解析结果，**不调用 GenerateAsync**：一旦调用就等于提交任务。
            var provider = VideoProviderFactory.Create(config);
            if (!provider.IsConfigured)
            {
                report($"视频链路未就绪（{provider.Name}）：请在「视频接口」里填上地址与模型。"
                    + "本次只做配置检查，没有提交生成任务。",
                    YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);
                return;
            }

            report($"视频链路已就绪（{provider.Name} · {config.VideoModel}）：提交 → 轮询 → 下载这条路已经能跑。"
                + "本次没有提交生成任务。", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Success);
        };
        root.Children.Add(testVideo);

        // ---------- 出图观感 ----------
        // 放在这一页的最后：它不是接口字段，填错了也不会坏掉，属于「出图这件事长什么样」。
        Divider();
        root.Children.Add(Header("出图观感"));
        var gachaReveal = new CheckBox
        {
            Content = "出图「开奖」：一批出完后留一排背面朝上的卡，点开走全屏揭晓，再挑一张",
            IsChecked = config.GachaReveal,
            FontSize = 11,
            Foreground = Brush("DfInk2")
        };
        root.Children.Add(gachaReveal);
        root.Children.Add(Note(
            "开启后：整批出完之前那排卡一直背面朝上（只显示「已完成 5/6」这类真实计数，不出缩略图），" +
            "出完后点它才开奖，揭晓时依次翻面；挑一张收进节点，其余进回收站。" +
            "关掉就是原来的样子：出好一张显示一张，右键挑、删、重做。"));
        root.Children.Add(Note(
            "两种模式下有一条是一样的：整批还在跑时都不能挑。中途挑会把整批丢掉，" +
            "而已经发出去的出图请求取消不了，它们跑完的图就没人认领了，所以统一等这一批出完。"));

        // 判档要一个能读图的模型。**模型不能读图时不再置灰**：判档是用户的意图，
        // 能不能兑现由出图那一刻去问他（换多模态模型 / 这次跳过判档直接出图）——
        // 直接置灰等于替他做了「不许判」的决定，而他要的可能只是先把图跑通。
        var judgeUnavailable = config.UseLocalProvider
            ? "现在选的是本地模拟，判定不会真的发生。"
            : !config.IsConfigured
                ? "还没有配置接口地址与模型。"
                : string.Empty;
        var judgeNeedsVision = judgeUnavailable.Length == 0 && !config.SupportsImageInput;
        var judgeQuality = new CheckBox
        {
            Content = "出图后让模型判一下：照着你写的提示词与负面词逐张打分，分数换成档位（金 / 红 / 紫 / 蓝 / 白），开奖按档位出效果",
            IsChecked = config.JudgeImageQuality,
            IsEnabled = judgeUnavailable.Length == 0,
            FontSize = 11,
            Foreground = Brush("DfInk2")
        };
        root.Children.Add(judgeQuality);
        root.Children.Add(Note(judgeUnavailable.Length > 0
            ? "现在打不开这条：" + judgeUnavailable
            : judgeNeedsVision
                ? "当前模型**没有开「支持图片输入」**，而判档要一个能读图的模型。出图时会问你一句："
                  + "去接入设置里勾上「模型支持图片输入」（或换一个能看图的模型），还是**这次跳过判档、直接把图出完跑通**。"
                  + "跳过的那一批没有档位，也就没有裂纹卡与自动重出。"
                : "开着会**多花一次模型调用**（把这一批图连同出图要求、负面词、节点上下文发过去），所以默认关。"));
        root.Children.Add(Note(
            QualityJudgement.ScoreBandNote +
            "判的是**绝对分**，不是这一批里的名次——所以一张也能判，同一张图两次出图可以互相比较。" +
            "评审会看四件事：与要求的贴合度、崩坏（多余手指 / 肢体断裂）、穿帮（多出来的东西 / 光影矛盾）、" +
            "角色与物体互相嵌进去。**踩中负面提示词的那张是裂纹卡**（卡面裂开），它的分数再高也不计入预兆。" +
            "另外本地还会先筛一道客观坏图（读不出来 / 整张一个颜色 / 尺寸只有要求的一半以下），那些直接是白档。" +
            "档位是模型的判断而不是客观结论，同一张图换个模型可能换档——界面上会写明这一点。"));

        // 裂纹卡自动重出：**只有判档开着时才可能生效**，所以它的可勾状态跟判档绑在一起。
        var autoRedraw = new CheckBox
        {
            Content = "判出裂纹卡就自动重出那几张（只提示，不用你操作）",
            IsChecked = config.AutoRedrawCrackedCards,
            IsEnabled = judgeQuality.IsEnabled,
            FontSize = 11,
            Foreground = Brush("DfInk2")
        };
        root.Children.Add(autoRedraw);
        root.Children.Add(Note(judgeUnavailable.Length > 0
            ? "现在打不开这条：" + judgeUnavailable
            : "它**只在判档开着时才生效**——判档关着就没有「裂纹卡」这个结论，也就没有可重出的对象。"
              + "重出只针对踩中负面词的那几张，不推倒整批；同一批最多自动重出 "
              + $"{NodeImageBatch.MaxCrackedRedrawRounds} 次，到顶会如实说「还是裂纹，请人工改提示词」。"
              + "它是在你点过「出图」之后再花一次钱，不想花就把它关掉。"));

        root.Children.Add(Note(
            "**每个厂家可以有自己的形象**（开奖许愿那一拍用它，没有就用回自绘的 ◈ 徽记）：把图放进下面任一个 `"
            + ProviderAvatar.FolderName + "` 目录，文件名用厂家 id —— "
            + "deepseek / moonshot / qwen / zhipu / siliconflow / openai / ollama / custom（png / jpg / jpeg / webp / gif 都认）。"
            + "程序不联网取图、也不内置任何图——素材从哪来、能不能用，由你自己决定并负责。\n· "
            + string.Join("\n· ", ProviderAvatar.Roots())));

        // 让应用用**自己已配置的图像链路**生成这一家的形象：密钥在你手里，我不碰它；
        // 换模型、加厂家时你自己点一下就能补一张，不用等我发版。
        var makeAvatar = Secondary("用当前图像链路生成这一家的形象");
        makeAvatar.HorizontalAlignment = HorizontalAlignment.Left;
        makeAvatar.Click += async (_, _) =>
        {
            // 与「测试连接」同一个道理：图像链路工厂是重新读磁盘配置的，不先保存就会拿着旧配置去生成。
            if (!saveToDisk())
                report("配置没能写盘，生成用的是磁盘上旧的那一份", YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Warning);

            var endpoint = config.ImageEndpoint.Length > 0 ? config.ImageEndpoint : config.Endpoint;
            var preset = ProviderPreset.Match(endpoint);
            var badge = ProviderBadges.Of(preset.Id);

            report($"正在用当前图像链路生成「{preset.Name}」的形象（{ProviderAvatarStudio.Size}×{ProviderAvatarStudio.Size}），最多等 5 分钟…",
                YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Info);
            var note = await ProviderAvatarStudio.GenerateAsync(preset.Id, preset.Name, badge.ColorHex);
            report(note, note.StartsWith("已生成", StringComparison.Ordinal)
                ? YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Success
                : YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Error);
        };
        root.Children.Add(makeAvatar);
        root.Children.Add(Note(
            "这个按钮画的是**自创角色**：只借这一家的气质与配色，提示词里明确要求不模仿任何已存在的作品角色或商标。" +
            "生成一次就存进上面那个目录，之后不用再生成；想换一张再点一次即可（同名的旧图会被替换）。"));

        // ---------- 写回 ----------
        // 文本字段原样写回（是否留空由各链路自己按「留空则复用」处理）；
        // 整数字段用 TryParse，解析失败就**保持原值不动**——用户正在中间状态打字（例如删光了准备重填）时，
        // 直接写 0 会把「限制参考图张数」这类设置悄悄清成「不限制」，那是个安静的破坏。
        void CommitEdits()
        {
            config.ImageEndpoint = TextOf(imageEndpoint);
            config.ImageModel = TextOf(imageModel);
            config.ImageApiKey = TextOf(imageApiKey);
            config.ImageSize = TextOf(imageSize);
            config.DefaultImageSteps = TextOf(imageSteps);
            config.DefaultImageCfg = TextOf(imageCfg);
            config.DefaultNegativePrompt = TextOf(imageNegative);
            if (int.TryParse(TextOf(imageMaxRefs), out var imageMaxRefValue))
                config.ImageMaxReferenceImages = imageMaxRefValue;

            config.ComfyUiBaseUrl = TextOf(comfyUrl);
            config.ComfyUiCheckpoint = TextOf(comfyCheckpoint);
            config.ComfyUiClientId = TextOf(comfyClientId);
            config.AssetDirectory = TextOf(assetDir);

            config.VideoEndpoint = TextOf(videoEndpoint);
            config.VideoModel = TextOf(videoModel);
            config.VideoApiKey = TextOf(videoApiKey);
            if (int.TryParse(TextOf(videoMaxRefs), out var videoMaxReferenceImages))
                config.VideoMaxReferenceImages = videoMaxReferenceImages;
            if (int.TryParse(TextOf(videoSeconds), out var videoDefaultSeconds))
                config.VideoDefaultSeconds = videoDefaultSeconds;

            // 出图观感（文档顶层，不属于某一份接口配置）：勾选框是三态的，只有 true 才算开。
            config.GachaReveal = gachaReveal.IsChecked == true;
            config.JudgeImageQuality = judgeQuality.IsChecked == true;
            config.AutoRedrawCrackedCards = autoRedraw.IsChecked == true;
        }

        /// <summary>
        /// 从 config 重新回显。智能导入会直接改这份 config（并落盘），写的就是本页这几个字段；
        /// 不重新回显的话，用户随后一点「保存」就会用界面上的旧值把刚导入的结果覆盖回去。
        /// </summary>
        void ReloadFromConfig()
        {
            imageEndpoint.Text = config.ImageEndpoint;
            imageModel.Text = config.ImageModel;
            imageApiKey.Text = config.ImageApiKey;
            imageSize.Text = config.ImageSize;
            imageMaxRefs.Text = config.ImageMaxReferenceImages.ToString();
            imageSteps.Text = config.DefaultImageSteps;
            imageCfg.Text = config.DefaultImageCfg;
            imageNegative.Text = config.DefaultNegativePrompt;
            comfyUrl.Text = config.ComfyUiBaseUrl;
            comfyCheckpoint.Text = config.ComfyUiCheckpoint;
            comfyClientId.Text = config.ComfyUiClientId;
            assetDir.Text = config.AssetDirectory;
            videoEndpoint.Text = config.VideoEndpoint;
            videoModel.Text = config.VideoModel;
            videoApiKey.Text = config.VideoApiKey;
            videoMaxRefs.Text = config.VideoMaxReferenceImages.ToString();
            videoSeconds.Text = config.VideoDefaultSeconds.ToString();
            gachaReveal.IsChecked = config.GachaReveal;
            judgeQuality.IsChecked = config.JudgeImageQuality;
            autoRedraw.IsChecked = config.AutoRedrawCrackedCards;
        }

        /// <summary>
        /// 智能导入：先把四页的编辑写回 config，再让导入窗口在这份 config 上写，最后把所有页按结果重新回显。
        ///
        /// 第一步不能省：导入窗口最后会 Save「config 里那份」，
        /// 用户在别的页刚填、还没提交的内容会被那次写盘悄悄丢掉。
        /// </summary>
        async Task RunSmartImportAsync()
        {
            context.CommitAll();
            var summary = await SettingsApiImportDialog.ShowAsync(owner, config);
            if (summary is null) return;   // 什么都没写成：不刷新，也不报「导入成功」
            context.RefreshAll();
            report(summary, YEEYEEYEE.Desktop.Avalonia.AgentNoteLevel.Success);
        }

        return (root, CommitEdits, ReloadFromConfig);
    }
}
