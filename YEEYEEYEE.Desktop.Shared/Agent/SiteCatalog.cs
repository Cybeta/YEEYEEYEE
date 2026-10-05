using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 站点下的一个**池子**：一个模型乘一个档位，就是一次可执行的出图 / 出视频配置。
///
/// 为什么要有这一层：站点通常有几十个模型、每个模型又有几档分辨率，逐个建技能文件会把技能目录刷满
/// （真实的聚合站一家就有 20 个生图模型 × 3 档）。池子不是技能，是**站点下面的选项**——
/// 调用前让用户选一个，选完才拼成一次调用。所以它们只活在站点文件里，不各占一个文件。
/// </summary>
public sealed class SitePool
{
    /// <summary>模型名，原样保留文档 / 清单里的写法（含「(池6)」这类后缀，截断就变成另一个模型）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>image / video。</summary>
    public string Kind { get; set; } = "image";

    /// <summary>档位原文（1K / 2K / 4K / 720p / 5s…）；清单没声明档位时为空。</summary>
    public string Tier { get; set; } = string.Empty;

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>视频时长（秒）；图像池为 0。</summary>
    public int Seconds { get; set; }

    /// <summary>
    /// 这个池子能不能吃参考图（决定一次调用走文生图还是图生图）。
    /// **三态**：true / false / null（未知）。清单里没写这一项时是 null——
    /// 那时既不该声称它支持（会让图生图在服务端撞墙），也不该声称它不支持（会白白挡住能用的池子）。
    /// 未知就允许试，让服务端去说。
    /// </summary>
    public bool? SupportsReference { get; set; }

    public int MaxReferenceImages { get; set; }

    /// <summary>给人看的价（例如「2.5 积分」）；清单没写就空着，不猜。</summary>
    public string Price { get; set; } = string.Empty;

    /// <summary>
    /// 同一档位的**数字**单价（0 表示清单没写）。
    /// 存数字是为了能算「单价 × 张数」——出 6 张就是 6 倍的钱，那个数必须在点下去之前算得出来，
    /// 而从「2.5 积分」这种字符串里反推数字是猜。
    /// </summary>
    public double UnitPrice { get; set; }

    /// <summary>清单里被标成下线的池子：留着看得见，但不进选择器的候选。</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public bool IsVideo => string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase);

    /// <summary>同一站点内唯一（模型 + 档位）。</summary>
    [JsonIgnore]
    public string Key => $"{Model}|{Tier}";

    /// <summary>选择器里那一行。</summary>
    [JsonIgnore]
    public string Label => Tier.Length == 0 ? Model : $"{Model} · {Tier}";

    /// <summary>一行补充说明：价格、参考图能力、画幅。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Price.Length > 0) parts.Add(Price);
        if (Width > 0 && Height > 0) parts.Add($"{Width}×{Height}");
        if (Seconds > 0) parts.Add($"{Seconds}s");
        parts.Add(SupportsReference switch
        {
            true => MaxReferenceImages > 0 ? $"支持参考图（最多 {MaxReferenceImages} 张）" : "支持参考图",
            false => "不支持参考图",
            null => "参考图能力未知"
        });
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// ComfyUI 站点下的一个**工作流**：服务器 <c>userdata/workflows/</c> 里的一份文件，已转成 API 格式。
///
/// 为什么它不是 <see cref="SitePool"/>：池子是「一个模型乘一个档位」，参数是宽度、时长、单价，
/// 拼出来是一次 HTTP 调用；工作流是一整张**节点图**，参数藏在各个节点里，没有「档位」这回事。
/// 硬塞进池子会让 Price / 宽高 / 时长这些字段恒为空，checkpoint 也没地方放。
///
/// 为什么这里**只存元信息、不存 API JSON 正文**：这台服务器的 <c>object_info</c> 是 21.8 MB，
/// 而工作流有 315 份、正文合计好几 MB（实测 4 份样本 4.4K～30.7K）。把这些塞进站点文件，
/// 会让每次打开选择器都要读一个十几 MB 的 JSON。正文因此按份落在
/// <c>skills/sites/&lt;站点id&gt;/</c> 下（见 <see cref="SiteCatalog.PayloadDirectory"/>），
/// 只有真正要提交时才读那**一份**。
/// </summary>
public sealed class SiteWorkflow
{
    /// <summary>站点内唯一：服务器上 <c>workflows/</c> 下的相对路径（含子目录），如 <c>T-图像-Krea/T01-….json</c>。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>给人看的名字：去掉扩展名的文件名。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>顶层文件夹名（服务器自己按模态分的那一层，如 <c>G视频-Wan图生</c>）；根目录下为空。</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>image / video / audio / other。判据见 <see cref="KindReason"/>，不猜。</summary>
    public string Kind { get; set; } = "other";

    /// <summary>凭什么是这个种类（取自文件夹名还是节点类型），如实写出来给用户核对。</summary>
    public string KindReason { get; set; } = string.Empty;

    /// <summary>转成 API 格式后还剩几个节点。</summary>
    public int NodeCount { get; set; }

    /// <summary>转换时被跳过的节点说明（静音 / 绕过 / 非后端节点 / 匿名控件）；没有跳过时为空。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>这份没转成的原因；空表示转成了。</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>这一份是不是该种类下的推荐项（推荐规则见 <see cref="ComfyUiLibrary.PickRecommended"/>）。</summary>
    public bool Recommended { get; set; }

    /// <summary>清单里被标成不用的工作流：留着看得见，但不进选择器的候选。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>API 正文落在哪个文件（相对 <see cref="SiteCatalog.PayloadDirectory"/> 的文件名）。</summary>
    public string PayloadFile { get; set; } = string.Empty;

    /// <summary>
    /// 导入体检的结论：这份工作流有几个必填输入是**我们转换时丢的**。
    ///
    /// 为什么要把这个数留在条目上（而不是只写在导入那一次的报告里）：它会让「用它出片」缺东西，
    /// 而提交会被 ComfyUI 拒收（缺必填输入，HTTP 400）——不说的话，用户只会以为是我们坏了。必须在**选它的那一刻**
    /// 与**点下去出片之前**都能看到，而报告滑过去就没了。
    /// </summary>
    public int DroppedInputs { get; set; }

    /// <summary>
    /// 这份工作流**自己**原稿里就断着的输入有几处（不是转换的问题：没接线的 Reroute、没有同名 Set 的 Get）。
    /// 同上：选它出片会缺东西，得让人在选之前就知道，而不是跑完发现产出是空的。
    /// </summary>
    public int BrokenInputs { get; set; }

    /// <summary>
    /// 「我们丢了」的那几处，逐条写清是哪个节点的哪个输入。
    /// 只记个数不够用：用户要的是**能拿去核对的清单**——重新导入时选「让大模型认一认」，
    /// 或去 ComfyUI 里看看那几根线到底接哪儿了。
    /// </summary>
    public List<string> DroppedInputDetails { get; set; } = new();

    /// <summary>
    /// 「它自己断线」的那几处，逐条写清节点与输入——用户去 ComfyUI 里要接的就是这几根。
    /// 只说「有 18 处断线」等于把找人这活儿又推回给用户。
    /// </summary>
    public List<string> BrokenInputDetails { get; set; } = new();

    /// <summary>
    /// 这份工作流有**几处必填输入我判断不了**（链子停在一个我不担保的类型上：服务器的节点定义里没有它，
    /// 或者它本来就是纯前端件——两者在 JSON 里长得一样）。
    ///
    /// 为什么既不算「我们丢了」、也不能不记：算成「我们丢了」是把「我不认识」说成「我们弄丢了」，
    /// 用户会照它去重导、去改一份本来没毛病的工作流；可不记下来，他在选择器里看到的就是一份
    /// 没有任何记号的好工作流，点下去却收 400。**所以照实说「判断不了」，并且照样挂记号。**
    /// </summary>
    public int UncertainInputs { get; set; }

    /// <summary>判断不了的那几处，逐条写清节点、输入与我凭什么判断不了。</summary>
    public List<string> UncertainInputDetails { get; set; } = new();

    /// <summary>
    /// 这份工作流引用的文件里，**这台机器上没有**的有几处（示例素材、模型、依赖）。
    ///
    /// 为什么要单列：正文结构一点毛病都没有，所以「我们丢了 / 自己断线」两栏永远看不见它——
    /// 提交时也不会被挡下，但这条链跑到那一步就会失败（实测拿不到产物）。
    /// 而且它**随服务器上的文件变化**——换台机器结论就变。
    /// </summary>
    public int MissingFiles { get; set; }

    /// <summary>
    /// 缺文件的那几处，逐条写清是哪个节点的哪个输入、缺的是什么文件。
    /// 它决定了两件不同的事：**示例素材**给了就能跑；**模型/依赖**得先把文件补到服务器上。
    /// </summary>
    public List<string> MissingFileDetails { get; set; } = new();

    /// <summary>
    /// 缺文件里**我们会替换、且真有产物靠它**的那几个素材槽位（结构化：节点、哪一路、示例文件名）。
    ///
    /// 为什么单列一份结构化的：出片前要拿它挡一次提交——「这次没给满、空着的槽位又留着
    /// 早已失效的示例」就是一次 400（实测 B02），而 `MissingFileDetails` 是给人看的整句，
    /// 拿来判这件事只能比字符串。这几轮的规矩：判据只写一份，措辞再照着判据说。
    /// </summary>
    public List<ComfyUiMissingMedia> MissingMedia { get; set; } = new();

    public DateTimeOffset ConvertedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public bool IsVideo => string.Equals(Kind, "video", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsImage => string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool Converted => Error.Length == 0;

    /// <summary>选择器里那一行。</summary>
    [JsonIgnore]
    public string Label => Folder.Length == 0 ? Title : $"{Folder} / {Title}";

    /// <summary>一行补充说明：节点数、种类判据、跳过与失败。</summary>
    public string Describe()
    {
        var parts = new List<string> { $"节点 {NodeCount} 个" };
        if (Error.Length > 0) parts.Add("**没转成**：" + Error);
        if (Note.Length > 0) parts.Add(Note);
        if (KindReason.Length > 0) parts.Add($"判为{KindName}：{KindReason}");
        return string.Join(" · ", parts);
    }

    private string KindName => Kind switch
    {
        "video" => "视频",
        "image" => "图像",
        "audio" => "声音",
        _ => "未知"
    };
}

/// <summary>
/// 一个已登记的站点：基础地址、各类接口路径、以及它下面全部可用池子。
///
/// 一个站点一个文件（<c>skills/sites/&lt;id&gt;.json</c>），而不是每个池子一个文件——
/// 站点是用户理解与操作的单位（「这家站有哪些能用的」），池子是它下面的选项。
/// </summary>
public sealed class SiteProfile
{
    /// <summary>稳定标识（由主机名派生，例如 example）。文件名用它，改名不会搬家。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>界面上显示的名字；默认取主机名里有辨识度的那一段，可改。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>基础地址，含版本前缀（https://video.example.com/v1）。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>这份站点是从哪来的（文档页地址）。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>池子清单是从哪个地址拉到的；空表示来自文档里的「可用模型」表。</summary>
    public string ListSource { get; set; } = string.Empty;

    /// <summary>文生图接口路径。</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>图生图接口路径（吃参考图时走它）；清单 / 文档没写就空着。</summary>
    public string ImageEditPath { get; set; } = string.Empty;

    /// <summary>出视频接口路径。</summary>
    public string VideoPath { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    /// <summary>鉴权方式：bearer / x-api-key / query。</summary>
    public string AuthStyle { get; set; } = "bearer";

    /// <summary>
    /// 这个站点自己的密钥。**内存里是明文，写盘时加密**（<see cref="ProtectedApiKey"/>）。
    /// 空表示没设，调用时退回设置里的图像接口密钥。
    ///
    /// 为什么密钥要挂在站点上、而不是只用设置里那一把：**一家站一把账号**。
    /// 只有一个全局密钥的话，导入第二家站会把第一家的密钥覆盖掉，第一家那批池子从此全部 401——
    /// 而池子是站点的一部分，密钥同样是。
    /// </summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 磁盘上的加密密钥（DPAPI）。
    /// 与明文分开存是为了**能分清「没设」和「解不开」**——合成一个字段的话，
    /// 换了 Windows 账户导致解不开时，界面会说「你没填过密钥」，而用户明明填过。
    /// </summary>
    public string ProtectedApiKey { get; set; } = string.Empty;

    /// <summary>磁盘上的密钥解不开（换了 Windows 账户或机器）：界面上要如实说，不能当成「没设」。</summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable { get; set; }

    [JsonIgnore]
    public bool HasApiKey => ApiKey.Length > 0;

    /// <summary>密钥状态（**绝不回显完整密钥**）。</summary>
    public string DescribeApiKey() => ApiKeyUnreadable
        ? "密钥解不开（换了 Windows 账户或机器）：重新填一次"
        : ApiKey.Length > 0
            ? $"已设置：{SecretProtector.Describe(ApiKey)}"
            : "未设置：调用时会退回设置里的「图像接口密钥」";

    public List<SitePool> Pools { get; set; } = new();

    /// <summary>
    /// 这个站点是**哪一种来源**：<c>api</c>（一家有接口的聚合站）还是 <c>comfyui</c>（一台 ComfyUI 服务器）。
    ///
    /// 为什么要分：两者的「子项」是两种完全不同的东西——接口站下面是一个个「模型 × 档位」的池子，
    /// ComfyUI 下面是一份份**工作流**。合成一种会让选择器里混进语义不同的条目，
    /// 也会让「这一家有哪些能用的」这个问题有两种答案。
    ///
    /// 默认 <c>api</c>：这个字段是后加的，老站点文件里没有它，读出来应当仍按接口站解释。
    /// </summary>
    public string Backend { get; set; } = "api";

    /// <summary>
    /// ComfyUI 的 checkpoint 文件名（只有 <see cref="Backend"/> 为 comfyui 时有意义）。
    /// 存在站点上而不是只存在设置里：**一家站一个 checkpoint**，多台 ComfyUI 各自加载的底模通常不同，
    /// 只有一个全局值时，切换站点会把另一台的 checkpoint 覆盖掉。
    /// </summary>
    public string Checkpoint { get; set; } = string.Empty;

    /// <summary>ComfyUI 站点下的工作流清单（只有 <see cref="Backend"/> 为 comfyui 时有意义）。</summary>
    public List<SiteWorkflow> Workflows { get; set; } = new();

    /// <summary>
    /// 这台机器上学到的「前端节点规则」（哪种节点是直通、哪种自带值、哪种是纯界面件）。
    ///
    /// 为什么要落在站点上：代码里那张内置表只认我们见过的那几族；新机器装了别的纯前端节点时，
    /// 它在转换时会被跳过，**下游必填输入整项消失**（缺必填输入 → 提交会被 ComfyUI 拒收）。
    /// 导入时体检会把这些节点列出来，用户可以选「让大模型认一认」——认出来的规则记在这里，
    /// 下次导入这台机器时**自动接着用**，不必再问一遍。
    /// </summary>
    public List<ComfyUiVirtualNodeRule> VirtualNodeRules { get; set; } = new();

    /// <summary>
    /// 全库在「同一个节点类型 + 同一个输入名」下**真实用过的固定选项值**（比例这类），键形如
    /// <c>ResolutionSelector.aspect_ratio</c>。导入时扫一遍全部正文得到，只有几十条短字符串。
    ///
    /// 为什么非得存它：这类控件的合法选项清单在服务端的 <c>object_info</c> 里（二十多 MB），
    /// 每份正文只留着当前选中的那一个值；而「照着当前值的写法把数字换掉」会造出服务端不认的字符串——
    /// 实测 `ResolutionSelector.aspect_ratio` 当前是 <c>16:9 (Widescreen)</c>，改成 9:16 时
    /// 按数字替换得到 <c>9:16 (Widescreen)</c>，合法值却是 <c>9:16 (Portrait Widescreen)</c>
    /// （括号里的朝向词不跟着数字走），提交被 400 拒。
    /// 全库在同一个节点上出现过的值都是**这台机器上真跑得通的**，从那里面挑就不必猜。
    /// </summary>
    public Dictionary<string, List<string>> OptionValues { get; set; } = new();

    /// <summary>
    /// 「**文件选择槽**」：`类名.输入名` → 收哪一类素材（`image` / `video` / `audio`）。
    ///
    /// 为什么要有这一份：有一类自定义节点的底图入口**既不在 `LoadImage` 家族里、输入名也不以 `image` 开头**
    /// （实测 `NanFengH3MultiReferenceGeneratorV10` 的 `图片1`…`图片9`），它按候选清单看其实就是
    /// 「服务器 input 目录的文件选择」，与 `LoadImage.image` 同一种形状。判据在**服务端的节点定义**里，
    /// 而生成时手上只有工作流正文——所以导入时（那时有节点定义）算好存这儿，生成时由 binder 读。
    ///
    /// 只存**这个站点用到的类**，所以很小。看不到这份表时，binder 只认老的那几种入口，
    /// 其余照旧如实说「我没认出来」——不会因为缺表就说错话。
    /// </summary>
    public Dictionary<string, string> FileSlots { get; set; } = new();

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>这个站点用哪把密钥：出图 / 出视频各自的字段（留空则沿用当前选中模型的密钥）。</summary>
    public string Kind => HasVideoPools && ImagePools.Count == 0 ? "video" : "image";

    [JsonIgnore]
    public bool IsComfyUi => string.Equals(Backend, "comfyui", StringComparison.OrdinalIgnoreCase);

    /// <summary>选择器里的工作流候选：转成了、且没被标成不用的那些。</summary>
    [JsonIgnore]
    public IReadOnlyList<SiteWorkflow> UsableWorkflows =>
        Workflows.Where(workflow => workflow.Enabled && workflow.Converted).ToList();

    [JsonIgnore]
    public IReadOnlyList<SiteWorkflow> ImageWorkflows => UsableWorkflows.Where(workflow => workflow.IsImage).ToList();

    [JsonIgnore]
    public IReadOnlyList<SiteWorkflow> VideoWorkflows => UsableWorkflows.Where(workflow => workflow.IsVideo).ToList();

    /// <summary>该种类下被推荐的那一份；没有推荐项时返回 null（不退回第一项——那会假装有推荐）。</summary>
    public SiteWorkflow? Recommended(bool video) =>
        (video ? VideoWorkflows : ImageWorkflows).FirstOrDefault(workflow => workflow.Recommended);

    [JsonIgnore]
    public IReadOnlyList<SitePool> ImagePools => Pools.Where(pool => !pool.IsVideo).ToList();

    [JsonIgnore]
    public IReadOnlyList<SitePool> VideoPools => Pools.Where(pool => pool.IsVideo).ToList();

    [JsonIgnore]
    public bool HasVideoPools => Pools.Any(pool => pool.IsVideo);

    [JsonIgnore]
    public string Label => DisplayName.Length > 0 ? DisplayName : Id;

    /// <summary>选择器里的候选：清单里没被标下线的那些。</summary>
    [JsonIgnore]
    public IReadOnlyList<SitePool> UsablePools => Pools.Where(pool => pool.Enabled).ToList();

    /// <summary>第一段说明：这家有多少池子、清单从哪来。</summary>
    public string Describe()
    {
        // ComfyUI 站点没有池子，它的子项是工作流——报「生图 0 个、视频 0 个」会让人以为这家是空的。
        if (IsComfyUi)
        {
            var text = $"工作流：图像 {ImageWorkflows.Count} 份、视频 {VideoWorkflows.Count} 份";
            if (Workflows.Count > UsableWorkflows.Count)
                text += $"（另有 {Workflows.Count - UsableWorkflows.Count} 份没转成或已停用）";
            if (BaseUrl.Length > 0) text += $"｜{BaseUrl}";
            if (Checkpoint.Length > 0) text += $"｜checkpoint {Checkpoint}";
            return text;
        }

        var parts = new List<string>
        {
            $"生图 {ImagePools.Count} 个",
            $"视频 {VideoPools.Count} 个"
        };
        var pools = $"池子：{string.Join("、", parts)}";
        if (BaseUrl.Length > 0) pools += $"｜{BaseUrl}";
        pools += ListSource.Length > 0 ? $"｜清单来自 {ListSource}" : "｜清单来自文档的可用模型表";
        return pools;
    }
}

/// <summary>用户选定的一个池子：哪一家站、哪一个池子。跑一次出图要的「用谁、用哪档、用哪把密钥」都在这两个里。</summary>
public sealed record SitePoolChoice(SiteProfile Site, SitePool Pool);

/// <summary>用户选定的一个工作流：哪一台 ComfyUI、哪一份工作流。</summary>
public sealed record SiteWorkflowChoice(SiteProfile Site, SiteWorkflow Workflow);

/// <summary>
/// 用户选定的一次出图 / 出视频来源。**两种来源只在这一个地方合流**：
/// 接口站的池子（一家站 × 一个模型 × 一个档位）与 ComfyUI 的工作流（一台服务器 × 一份节点图）。
///
/// 为什么不让工作流伪装成池子：池子的参数是「宽高 / 时长 / 单价」，拼出来是一次 HTTP 调用；
/// 工作流是一整张节点图，参数藏在各节点里。合成一种之后，选择器、自检、成本估算里
/// 每一处都得再判一次「这到底是哪一种」，而判断漏了一处就是安静地算错价、或按错的尺寸出图。
/// </summary>
public sealed record ImageSourceChoice(SitePoolChoice? Pool, SiteWorkflowChoice? Workflow)
{
    public static ImageSourceChoice OfPool(SitePoolChoice pool) => new(pool, null);

    public static ImageSourceChoice OfWorkflow(SiteWorkflowChoice workflow) => new(null, workflow);

    public string SiteLabel => Workflow?.Site.Label ?? Pool?.Site.Label ?? string.Empty;

    public string ItemLabel => Workflow is { } workflow
        ? workflow.Workflow.Label
        : Pool is { } pool ? pool.Pool.Label : string.Empty;

    /// <summary>一行说明（「站点 · 子项」）。</summary>
    public string Label => SiteLabel.Length == 0 ? ItemLabel : $"{SiteLabel} · {ItemLabel}";

    public bool IsVideo => Workflow is { } workflow ? workflow.Workflow.IsVideo : Pool?.Pool.IsVideo ?? false;

    /// <summary>走接口站池子时的那个池子；走 ComfyUI 工作流时为 null。</summary>
    public SitePool? PoolItem => Pool?.Pool;

    /// <summary>走 ComfyUI 工作流时的那份工作流；走接口站池子时为 null。</summary>
    public SiteWorkflow? WorkflowItem => Workflow?.Workflow;

    /// <summary>走的是不是 ComfyUI 工作流（而不是接口站池子）。</summary>
    public bool IsWorkflow => Workflow is not null;

    /// <summary>
    /// 这一次的单价；**算不出来时是 null**（ComfyUI 工作流没有单价——它烧的是自己的显卡，
    /// 不是按次计费的接口）。返回 0 会让成本行显示「0 积分」，那是在说谎。
    /// </summary>
    public double? UnitPrice => Pool?.Pool.UnitPrice is > 0 ? Pool.Pool.UnitPrice : null;
}

/// <summary>
/// 站点文件（<c>skills/sites/&lt;id&gt;.json</c>）的读写。
///
/// 放在技能目录下的子目录里，而不是和技能混在一起：技能是「一步一步的流程」，站点是「一家有哪些能用的」，
/// 两种东西混在一层目录里，技能管理页与装载器都得靠文件名前缀去猜，那是迟早会出错的做法。
/// </summary>
public static class SiteCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 中文（站点名、模型名里的「号池」注释）不该被转义成 \uXXXX：这些文件是给人看、给人改的。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>站点目录：技能目录下的 sites/。</summary>
    public static string Directory => Path.Combine(SkillLibrary.Directory, "sites");

    public static string EnsureDirectory()
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>由地址派生站点标识：取主机名里有辨识度的那一段（video.example.com → example）。</summary>
    public static string IdFor(string? url)
    {
        var host = ApiDocAnalyzer.HostOf(url);
        if (host.Length == 0) return "site";
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // 常见的功能性前缀（www / api / video / docs…）不是站点名的一部分；
        // 只在这一段存在、且后面还有一段时才跳过它，免得把「api.example.com」认成 example 之外的怪名字。
        var skip = new[] { "www", "api", "video", "img", "image", "docs", "doc", "open", "portal" };
        var start = labels.Length > 1 && skip.Contains(labels[0], StringComparer.OrdinalIgnoreCase) ? 1 : 0;
        var picked = labels.Length > start ? labels[start] : labels[0];
        return Slug(picked);
    }

    /// <summary>
    /// 地址里的端口（没写端口时给出协议默认端口：http 80 / https 443）。
    ///
    /// 为什么要它：站点标识**只由主机名派生**，所以「同一台主机上跑两个 ComfyUI（端口不同）」
    /// 会派生出同一个 id——两个站点文件互相覆盖，先导进来的那份工作流库说没就没。
    /// 撞名时用端口给新登记的那一台加后缀（见 <see cref="ComfyUiLibrary.Install"/>）。
    /// </summary>
    public static string PortOf(string? url)
        => Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
            ? uri.Port.ToString()
            : string.Empty;

    /// <summary>
    /// 按「上次用的那一个」的印记在现有站点里把池子找回来；找不到返回 null。
    ///
    /// 找不到的可能性是真实存在的：站点被删了、池子被清单刷掉了、同名不同档。
    /// 这时候**返回 null 而不是退到第一个**——退到第一个会让人以为「上次那个」还在，
    /// 而真正发出去的是另一个模型、另一个价。
    /// </summary>
    public static SitePoolChoice? Find(IReadOnlyList<SiteProfile> sites, string? siteId, string? model, string? tier)
    {
        if (sites is null || string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(model)) return null;
        var site = sites.FirstOrDefault(item => string.Equals(item.Id, siteId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (site is null) return null;
        var key = (tier ?? string.Empty).Trim();
        var pool = site.Pools.FirstOrDefault(item =>
            string.Equals(item.Model, model.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Tier, key, StringComparison.OrdinalIgnoreCase));
        return pool is null ? null : new SitePoolChoice(site, pool);
    }

    /// <summary>
    /// 按「上次用的那一份」的印记在现有站点里把工作流找回来；找不到返回 null（理由同 <see cref="Find"/>）。
    /// </summary>
    public static SiteWorkflowChoice? FindWorkflow(IReadOnlyList<SiteProfile> sites, string? siteId, string? key)
    {
        if (sites is null || string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(key)) return null;
        var site = sites.FirstOrDefault(item => string.Equals(item.Id, siteId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (site is null) return null;
        var workflow = site.Workflows.FirstOrDefault(item =>
            string.Equals(item.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
        return workflow is null ? null : new SiteWorkflowChoice(site, workflow);
    }

    /// <summary>默认显示名：主机名本身（例如 video.example.com）；用户可以改成更好认的名字。</summary>
    public static string DefaultDisplayNameFor(string? url)
    {
        var host = ApiDocAnalyzer.HostOf(url);
        return host.Length == 0 ? "新站点" : host;
    }

    private static string Slug(string text)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
            buffer.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = buffer.ToString().Trim('-');
        return slug.Length == 0 ? "site" : slug;
    }

    /// <summary>
    /// 工作流 API 正文的存放目录：<c>sites/&lt;站点id&gt;/</c>。
    ///
    /// 与站点文件（<c>sites/&lt;id&gt;.json</c>）分开：正文合计好几 MB，塞进站点文件会让
    /// 每次打开选择器都要解析一个巨大的 JSON；分成一份一个文件之后，只有真正要提交时才读那一份。
    /// 目录名用站点 id，与站点文件同名不同「型」，一眼能看出谁属于谁。
    /// </summary>
    public static string PayloadDirectory(string siteId) => Path.Combine(Directory, siteId);

    /// <summary>
    /// 正文文件名：可读的标题片段 + 键的短哈希。
    /// 中文名与斜杠都不能直接当文件名（工作流的相对路径里带子目录，如 <c>T-图像-Krea/T01-….json</c>），
    /// 短哈希则保证「不同键绝不同名」——只用清洗过的标题会让两份中文名相同的工作流互相覆盖。
    /// </summary>
    public static string PayloadFileName(string key)
    {
        var title = Path.GetFileNameWithoutExtension(key);
        var ascii = new string(title.Where(ch => char.IsLetterOrDigit(ch)).ToArray());
        if (ascii.Length > 40) ascii = ascii[..40];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        return ascii.Length == 0 ? $"{hash}.json" : $"{ascii}.{hash}.json";
    }

    /// <summary>写一份工作流正文。原文照写，不再包一层——它就是 <c>/prompt</c> 要的东西。</summary>
    public static bool SavePayload(string siteId, string fileName, string apiJson, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(fileName))
        {
            error = "工作流正文缺少站点或文件名。";
            return false;
        }

        try
        {
            var directory = PayloadDirectory(siteId);
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), apiJson);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>读一份工作流正文；读不到返回 null（由调用方如实报出，不拿默认值顶替一张节点图）。</summary>
    public static string? LoadPayload(string siteId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            var path = Path.Combine(PayloadDirectory(siteId), fileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 清掉站点目录下**已不被任何工作流引用**的正文文件。
    ///
    /// 什么时候会有多余文件：重新导入时工作流被删掉或被改了名（服务器上删了一份工作流），
    /// 那些正文就成了孤儿。不清的话，目录会随着每次导入越滚越大，而且里面留着的是**旧图**——
    /// 名字还在、内容已经不是那一份了，比干脆没有更危险。
    /// </summary>
    public static int PrunePayloads(SiteProfile site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var directory = PayloadDirectory(site.Id);
        if (!System.IO.Directory.Exists(directory)) return 0;

        var keep = new HashSet<string>(
            site.Workflows.Select(workflow => workflow.PayloadFile).Where(name => name.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            if (keep.Contains(Path.GetFileName(path))) continue;
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>读取全部站点；单个文件出错只记录它，不影响其它站点。</summary>
    public static (List<SiteProfile> Sites, List<string> Errors) Load()
    {
        var sites = new List<SiteProfile>();
        var errors = new List<string>();
        if (!System.IO.Directory.Exists(Directory)) return (sites, errors);

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var site = JsonSerializer.Deserialize<SiteProfile>(File.ReadAllText(path), Options);
                if (site is null || string.IsNullOrWhiteSpace(site.Id))
                {
                    errors.Add($"{Path.GetFileName(path)}：缺少 id");
                    continue;
                }
                site.Pools ??= new List<SitePool>();
                site.Workflows ??= new List<SiteWorkflow>();
                // 后加字段的归一化：老文件里没有 backend，反序列化后可能留成空串（有人手工把字段写成
                // null 也会），这里一律按「接口站」解释——早先只有这一种站点。
                if (string.IsNullOrWhiteSpace(site.Backend)) site.Backend = "api";
                site.Checkpoint ??= string.Empty;
                UnprotectKey(site);
                sites.Add(site);
            }
            catch (JsonException error) { errors.Add($"{Path.GetFileName(path)}：{error.Message}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add($"{Path.GetFileName(path)}：{error.Message}"); }
        }
        return (sites, errors);
    }

    /// <summary>写入一个站点（按 Id 决定文件名，重复导入是覆盖，不新增）。</summary>
    public static bool Save(SiteProfile site, out string error)
    {
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(site);
        if (string.IsNullOrWhiteSpace(site.Id)) { error = "站点缺少标识，无法保存。"; return false; }

        try
        {
            site.UpdatedAt = DateTimeOffset.Now;
            var path = Path.Combine(EnsureDirectory(), $"{site.Id}.json");
            // 密钥加密后写盘，但内存里始终留明文：**明文才是这一份的来源**。
            // 顺手把内存也换成密文的话，下一次保存会把密文再加密一遍，密钥就废了。
            site.ProtectedApiKey = site.ApiKey.Length > 0 ? SecretProtector.Protect(site.ApiKey) : string.Empty;
            site.ApiKeyUnreadable = false;
            File.WriteAllText(path, JsonSerializer.Serialize(site, Options));
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = failure.Message;
            return false;
        }
    }

    /// <summary>
    /// 把磁盘上的加密密钥解回明文。解不开（换了 Windows 账户或机器）时**如实标记**，
    /// 绝不能当成「没设」——那会让「我明明填过密钥」变成一句「你没填」，用户会去反复重填。
    /// </summary>
    private static void UnprotectKey(SiteProfile site)
    {
        // `ProtectedApiKey` 这个字段只有我们写、且只写密文，所以「解不开」就等于「真的解不开」，
        // 不需要再去猜它是不是有人手工填的明文。要手工填的话走界面填 —— 界面填的会被加密后再存。
        if (string.IsNullOrWhiteSpace(site.ProtectedApiKey)) return;
        var decrypted = SecretProtector.Unprotect(site.ProtectedApiKey);
        if (decrypted is null)
        {
            site.ApiKeyUnreadable = true;
            site.ApiKey = string.Empty;
            return;
        }
        site.ApiKey = decrypted;
    }

    /// <summary>删掉一个站点文件（连同它的工作流正文目录）。</summary>
    public static bool TryDelete(SiteProfile site, out string error)
    {
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(site);
        try
        {
            var path = Path.Combine(Directory, $"{site.Id}.json");
            if (!File.Exists(path)) { error = "找不到这个站点的文件（可能已被移动或删除）。"; return false; }
            File.Delete(path);

            // 正文目录跟着站点一起走：留下它的话，同名站点下次导入会捡到上一次的旧工作流正文。
            var payloads = PayloadDirectory(site.Id);
            if (System.IO.Directory.Exists(payloads)) System.IO.Directory.Delete(payloads, recursive: true);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = failure.Message;
            return false;
        }
    }
}

/// <summary>
/// 从站点自己的清单接口探测池子。
///
/// 这里做的是一件事：**把「一家有哪些模型、各自什么档位」问出来**，全程只读——
/// 探测不发任何生成请求（生成要花钱、出视频还要轮询，一个"看看有什么"的动作不该顺手起任务）。
///
/// 地址候选是「候选」不是「假设」：拉到的内容必须**能被解析成池子**才认，探不到就退回文档里的
/// 可用模型表，并把实际用上的那个来源记进站点文件（下次刷新直接用它，不再重新猜）。
/// 解析器写得宽容一些——聚合站的清单形状各家不同，但只要它把模型名与档位写出来了，就该认出来。
/// </summary>
public static class SitePoolProbe
{
    /// <summary>一次最多认多少个池子：清单可能几十个模型 × 几档，全展开会把选择器淹掉。</summary>
    public const int MaxPools = 400;

    private static readonly string[] VideoNameHints =
    {
        "seedance", "veo", "kling", "runway", "sora", "minimax", "hailuo", "wan", "pika", "vidu", "firefly-video", "cogvideo"
    };

    /// <summary>
    /// 清单地址候选，按可靠性排序：站点后台的清单接口最准（它就是要给用户看模型表的那份数据），
    /// 其次是 OpenAI 兼容的 <c>/models</c>（只有模型名、没有档位）。
    /// </summary>
    public static IReadOnlyList<string> CandidateUrls(string? baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri)) return Array.Empty<string>();
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var basePart = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');

        var urls = new List<string>
        {
            $"{origin}/admin/api/managed-models",
            $"{origin}/admin/api/video-presets",
            $"{origin}/admin/api/models",
            $"{basePart}/models",
            $"{origin}/v1/models"
        };
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 宽容解析：把一份清单 JSON 变成池子。
    ///
    /// 只认「模型名 + 档位」这两件事实：名字取 alias / id / name / model / key / label 里第一个非空的，
    /// 档位取 resolutions / sizes / tiers 或 durations。取不到档位就留空（不猜一档），
    /// 一个条目都认不出来时返回空清单——由调用方退回文档里的模型表。
    /// </summary>
    public static IReadOnlyList<SitePool> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SitePool>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var items = ItemsOf(document.RootElement);
            var pools = new List<SitePool>();
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var model = FirstString(item, "alias", "id", "name", "model", "model_name", "key", "label");
                if (model.Length == 0) continue;

                var kindText = FirstString(item, "type", "kind", "category", "modality");
                var isVideo = kindText.Contains("video", StringComparison.OrdinalIgnoreCase)
                    || (kindText.Length == 0 && VideoNameHints.Any(hint => model.Contains(hint, StringComparison.OrdinalIgnoreCase)));
                if (kindText.Contains("image", StringComparison.OrdinalIgnoreCase)) isVideo = false;

                var durations = StringsOf(item, "durations", "duration", "seconds");
                var tiers = isVideo ? durations : StringsOf(item, "resolutions", "sizes", "tiers", "resolution");
                // 参考图能力**只报清单里真的写了的**：写了支持就是 true，写了不支持（或给了个 0）就是 false，
                // 压根没提就是 null（未知）——未知不该被当成任何一种。
                var referenceSignals = new[]
                {
                    BoolOf(item, "image_to_image"),
                    BoolOf(item, "video_reference_enabled"),
                    BoolOf(item, "audio_reference_enabled"),
                    NumberOf(item, "max_reference_images") > 0,
                    NumberOf(item, "max_images") > 0,
                    durations.Count > 0
                };
                var referenceMentioned = item.TryGetProperty("image_to_image", out _)
                    || item.TryGetProperty("max_reference_images", out _)
                    || item.TryGetProperty("max_images", out _)
                    || item.TryGetProperty("video_reference_enabled", out _);
                bool? supportsReference = referenceSignals.Any(signal => signal) ? true
                    : referenceMentioned ? false
                    : null;
                var maxReferences = (int)Math.Max(NumberOf(item, "max_reference_images"), Math.Max(NumberOf(item, "max_images"), 0));
                var enabled = !item.TryGetProperty("enabled", out var enabledValue) || enabledValue.ValueKind != JsonValueKind.False;
                var priceTable = PriceTableOf(item);

                if (tiers.Count == 0)
                {
                    pools.Add(Build(model, isVideo, string.Empty, 0, supportsReference, maxReferences, enabled, priceTable));
                    continue;
                }

                foreach (var tier in tiers)
                {
                    var seconds = isVideo ? SecondsOf(tier) : 0;
                    pools.Add(Build(model, isVideo, tier, seconds, supportsReference, maxReferences, enabled, priceTable));
                }
            }

            // 去重（同一模型 + 同一档位可能被两处清单各写一遍），并限流。
            var unique = new List<SitePool>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pool in pools)
                if (seen.Add(pool.Key)) unique.Add(pool);
            return unique.Count <= MaxPools ? unique : unique.Take(MaxPools).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<SitePool>();
        }
    }

    private static SitePool Build(string model, bool isVideo, string tier, int seconds, bool? supportsReference, int maxReferences, bool enabled, IReadOnlyDictionary<string, (string Label, double Value)> prices)
    {
        var (width, height) = isVideo ? (0, 0) : ApiDocAnalyzer.ParseSize(tier) ?? (0, 0);
        // 按档位取价；清单只给了一个价而条目没分档时，那个价就是这个条目唯一的价格。
        var price = (Label: string.Empty, Value: 0d);
        if (prices.TryGetValue(tier, out var exact)) price = exact;
        else if (prices.Count == 1 && tier.Length == 0) price = prices.Values.First();
        return new SitePool
        {
            Model = model,
            Kind = isVideo ? "video" : "image",
            Tier = tier,
            Width = width,
            Height = height,
            Seconds = seconds,
            SupportsReference = supportsReference,
            MaxReferenceImages = maxReferences,
            Price = price.Label,
            UnitPrice = price.Value,
            Enabled = enabled
        };
    }

    /// <summary>把 <c>prices</c> / <c>duration_prices</c> 这类「档位 → 数字」的表读成「档位 → 给人看的价」。</summary>
    private static IReadOnlyDictionary<string, (string Label, double Value)> PriceTableOf(JsonElement item)
    {
        var table = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "prices", "duration_prices", "prices_agent", "prices_duration" })
        {
            if (!item.TryGetProperty(name, out var prices) || prices.ValueKind != JsonValueKind.Object) continue;
            foreach (var pair in prices.EnumerateObject())
            {
                if (pair.Value.ValueKind != JsonValueKind.Number) continue;
                var value = pair.Value.GetDouble();
                // 标签与数字一起留下：数字用来算「单价 × 张数」，标签用来说清这是哪一档的价。
                if (!table.ContainsKey(pair.Name)) table[pair.Name] = ($"{value:0.##} 积分", value);
            }
            if (table.Count > 0) return table;
        }
        return table;
    }

    /// <summary>清单的条目数组：<c>{"data":[…]}</c> 与直接给数组两种形状都收。</summary>
    private static IEnumerable<JsonElement> ItemsOf(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray();
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<JsonElement>();
        foreach (var name in new[] { "data", "models", "items", "list", "presets" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static string FirstString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        }
        return string.Empty;
    }

    private static IReadOnlyList<string> StringsOf(JsonElement item, params string[] names)
    {
        var values = new List<string>();
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in value.EnumerateArray())
                {
                    var text = entry.ValueKind == JsonValueKind.String ? entry.GetString()?.Trim()
                        : entry.ValueKind == JsonValueKind.Number ? entry.GetDouble().ToString("0.##")
                        : null;
                    if (!string.IsNullOrWhiteSpace(text) && !values.Contains(text, StringComparer.OrdinalIgnoreCase)) values.Add(text);
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                // 逗号 / 顿号分隔的一行（"1K,2K,4K"）也当列表。
                foreach (var text in value.GetString()!.Split(new[] { ',', '，', '、', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!values.Contains(text, StringComparer.OrdinalIgnoreCase)) values.Add(text);
            }
            if (values.Count > 0) return values;
        }
        return values;
    }

    private static double NumberOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static bool BoolOf(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>把「5s」这类时长档位读成秒数；读不出来返回 0。</summary>
    private static int SecondsOf(string tier)
    {
        var digits = new string(tier.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var seconds) ? seconds : 0;
    }
}
