using System.Text.Json.Serialization;

namespace DreamForge.Desktop;

/// <summary>一批候选图里某一格的状态。</summary>
public enum BatchSlotStatus
{
    Waiting,
    Running,
    Done,
    Failed
}

/// <summary>
/// 一批候选图里的一格。
/// </summary>
public sealed class BatchSlot
{
    public BatchSlotStatus Status { get; set; } = BatchSlotStatus.Waiting;

    /// <summary>出好之后落在项目资产目录里的本机路径（没出好就是空）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>失败原因（成功就是空）。逐格记，不合并成一句「有 1 张失败」。</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// 用户把这一张删了（文件已移入回收站）。
    /// **保留这个格子、只做标记**，而不是从列表里抽掉：抽掉会让后面几张的编号整体前移，
    /// 用户刚记住的「第 3 张不错」就变成第 2 张，而虚影上的角标也会跟着跳。
    /// </summary>
    public bool Removed { get; set; }

    /// <summary>这一格开始跑的时刻（还没轮到就是空）。</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// 预览窗口中间那行字，**跑着的时候带已经等了多久**。
    ///
    /// 为什么给的是秒数、不是百分比：出图接口不吐进度，能诚实拿到的只有「还在跑、跑了多久」。
    /// 编一个 60% 出来是假信息——用户会照着它判断「快好了」，然后在 60% 上等十分钟。
    /// <paramref name="now"/> 由调用方传进来（不在这里读系统时钟），所以这条规则是可测的。
    /// </summary>
    public string Headline(DateTimeOffset now)
    {
        switch (Status)
        {
            case BatchSlotStatus.Running:
                if (StartedAt is not { } started) return "生成中";
                var seconds = (int)Math.Max(0, (now - started).TotalSeconds);
                return $"生成中 {seconds}s";
            case BatchSlotStatus.Failed:
                return "失败";
            case BatchSlotStatus.Done:
                return "已出好";
            default:
                return "等待中";
        }
    }
}

/// <summary>
/// 一个节点上的一批候选图：同时出 N 张，用户挑一张保存，其余作废。
///
/// 为什么要有这个中间态：一次出 N 张的目的是**从里面挑**，所以这 N 张在挑之前都不该算「这个节点的内容」。
/// 直接挂上去的话，节点会先多出 3 个附件、再让用户去手工删 2 个——把挑选变成了清理。
///
/// 为什么放在内存里、不写进画布 JSON：它是**临时态**。写进文件的话，只要程序在挑选之前退出一次，
/// 画布文件里就会永远留着几张小图，而用户根本没做过那次挑选。
/// </summary>
public sealed class NodeImageBatch
{
    public Guid NodeId { get; set; }

    public string NodeTitle { get; set; } = string.Empty;

    public List<BatchSlot> Slots { get; set; } = new();

    /// <summary>
    /// 画布上被选中的那几张（多选）。
    /// 多选是为了「一次勾掉好几张」：出了 6 张、其中 5 张明显不行时，
    /// 一张一张删要删 5 次，而用户心里想的是「就留那一张」。
    /// </summary>
    public HashSet<int> SelectedIndices { get; } = new();

    /// <summary>这一批还在跑（跑着时不能保存，也不能重做）。</summary>
    public bool IsRunning { get; set; }

    /// <summary>这一批的开始时刻：预览窗口上方那行字要报总进度，这里留着判断「这一批已经跑了多久」。</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// 这一批用的池子（含站点）。重做要按**同一家、同一把密钥、同一个模型**再来一次，
    /// 所以留的是完整的池子而不是一个显示用的字符串——靠字符串反推站点，改一次显示文案就会失效。
    /// </summary>
    [JsonIgnore]
    public SitePoolChoice? Pool { get; set; }

    /// <summary>显示在卡片上的一行（「示例站 · gpt-image-2(池6) · 2K」）。</summary>
    [JsonIgnore]
    public string PoolLabel => Pool is { } pool ? $"{pool.Site.Label} · {pool.Pool.Label}" : string.Empty;

    /// <summary>重做时按同一套参数再出一批，所以把请求本身留在这里。</summary>
    [JsonIgnore]
    public ImageGenerationRequest? Request { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string Negative { get; set; } = string.Empty;

    /// <summary>这一批算「怎么来的」（图生图 / 池子身份），保存时写进附件的来源。</summary>
    public string ModeNote { get; set; } = string.Empty;

    /// <summary>
    /// 这一批是**哪个动作**发起的（例如「Agent 协助 · 出角色图（正面全身）」）。
    /// 与 <see cref="ModeNote"/> 分开：那个说的是「这一张怎么生成的」，这个说的是「谁让它生成的」，
    /// 事后翻附件时两件事都要能查到。
    /// </summary>
    public string SourceLabel { get; set; } = string.Empty;

    /// <summary>格子的总数（含已删除的——编号要稳定，见 <see cref="BatchSlot.Removed"/>）。</summary>
    public int Count => Slots.Count;

    /// <summary>还留着的格子数（画布上真正会画出来的那些）。</summary>
    public int LiveCount => Slots.Count(slot => !slot.Removed);

    public int DoneCount => Slots.Count(slot => !slot.Removed && slot.Status == BatchSlotStatus.Done);

    public int FailedCount => Slots.Count(slot => !slot.Removed && slot.Status == BatchSlotStatus.Failed);

    public int RunningCount => Slots.Count(slot => !slot.Removed && slot.Status == BatchSlotStatus.Running);

    public int WaitingCount => Slots.Count(slot => !slot.Removed && slot.Status == BatchSlotStatus.Waiting);

    /// <summary>跑完了且至少有一张能挑。跑着的时候不让挑——挑到一半的图是没有意义的。</summary>
    public bool CanPick => !IsRunning && DoneCount > 0;

    /// <summary>一张都没成：这时该说的是「全都没出来，可以重做」，而不是摆一排失败。</summary>
    public bool AllFailed => !IsRunning && DoneCount == 0 && FailedCount > 0;

    /// <summary>用户把这一批全删光了：画布上不该再留一排空位，主窗口据此撤掉这一批。</summary>
    public bool IsEmpty => LiveCount == 0;

    /// <summary>进度（0-100），只算还留着的格子。</summary>
    public int Percent => LiveCount == 0 ? 0 : (int)Math.Round(100.0 * (DoneCount + FailedCount) / LiveCount);

    /// <summary>
    /// 跑着的时候那行字里的总进度（由**真实计数**算出来，不是编的）。
    /// 单张那批也有意义：「已完成 0/1」比只有「正在出 1 张」多告诉人一件事——还没有东西出来。
    /// </summary>
    public string ProgressLine() => LiveCount == 0
        ? string.Empty
        : $"已完成 {DoneCount + FailedCount}/{LiveCount} · {Percent}%";

    public BatchSlot? SlotAt(int index) => index >= 0 && index < Count ? Slots[index] : null;

    public string StatusLine()
    {
        if (IsRunning)
            return $"正在出 {Count} 张：{ProgressLine()}"
                + (FailedCount > 0 ? $"，失败 {FailedCount}" : string.Empty);
        if (AllFailed) return $"{Count} 张都没出来：可以右键重做";
        if (SelectedIndices.Count > 0) return $"{DoneCount} 张可选 · 已选中 {SelectedIndices.Count} 张";
        return CanPick ? $"出了 {DoneCount} 张：双击看大图，右键选用或删除" : $"{Count} 张候选";
    }

    /// <summary>
    /// 除第 <paramref name="keepIndex"/> 张之外，**已出好的**那些要移入回收站的路径。
    ///
    /// 这是「采用一张、其余作废」的关键一步：出图链路是「生成即落盘」的，只做「不挂到节点上」的话，
    /// 那几张图还躺在资产目录里——用户以为删了，其实没删。还没出好的格子没有文件，不该出现在这里。
    /// </summary>
    public IReadOnlyList<string> PathsExcept(int keepIndex)
    {
        // 序号越界时**什么都不抛弃**：失败要往「什么都不动」那边倒，
        // 不能因为一个算错的序号就把用户还没挑的那几张全删了。
        if (keepIndex < 0 || keepIndex >= Count) return Array.Empty<string>();
        return Slots
            .Where((slot, index) => !slot.Removed && index != keepIndex && slot.Status == BatchSlotStatus.Done && slot.Path.Length > 0)
            .Select(slot => slot.Path)
            .ToList();
    }

    /// <summary>选中的那些里已经出好的路径（可移入回收站的）。还没出好、已经删过的都不算在内。</summary>
    public IReadOnlyList<string> SelectedFinishedPaths() => Slots
        .Where((slot, index) => !slot.Removed && SelectedIndices.Contains(index)
            && slot.Status == BatchSlotStatus.Done && slot.Path.Length > 0)
        .Select(slot => slot.Path)
        .ToList();

    /// <summary>还有没有没出好的格子（有的话「重做」要等它跑完）。</summary>
    public bool HasUnfinished => Slots.Any(slot => !slot.Removed && slot.Status is BatchSlotStatus.Waiting or BatchSlotStatus.Running);

    /// <summary>「全部不要」时要移入回收站的路径（这一批还留着的、已出好的图）。</summary>
    public IReadOnlyList<string> AllFinishedPaths() => Slots
        .Where(slot => !slot.Removed && slot.Status == BatchSlotStatus.Done && slot.Path.Length > 0)
        .Select(slot => slot.Path)
        .ToList();
}
