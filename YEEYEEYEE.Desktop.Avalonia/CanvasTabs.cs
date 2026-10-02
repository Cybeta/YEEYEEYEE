using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 一个画布标签（浏览器标签式的多画布）。
///
/// 规矩照旧端的 <c>MainForm.CanvasTabState</c>：标签持有的是画布内容的**独立副本**。
/// 为什么必须是副本：切走一张画布时要把它的内容存下来，如果存的是「当前正在编辑的同一个对象」，
/// 切到 B 之后继续编辑，A 的那份快照会跟着一起变——「切走再切回」就变成把 B 的内容盖到 A 上。
/// </summary>
internal sealed class CanvasTab
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>标签标题（就是画布标题，也是画布库里的文件名）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>画布库里的文件路径；还没落盘的画布为 null（只读项目下新建的画布就是这种）。</summary>
    public string? Path { get; set; }

    /// <summary>这张标签的画布内容（独立副本）。当前活动标签的这份会在离开它时被最新编辑覆盖。</summary>
    public RecentCanvasState Snapshot { get; set; }

    /// <summary>有未保存的编辑。标签上会带一个 *，免得用户以为已经存过了。</summary>
    public bool Dirty { get; set; }

    public CanvasTab(RecentCanvasState snapshot)
    {
        Snapshot = snapshot;
        Title = snapshot.Title;
    }
}

/// <summary>
/// 标签记录的落盘：文件名与 JSON 字段和旧端 <c>canvas-tabs.json</c> 一致，
/// 于是两端可以互相读对方的标签记录，不会出现「旧端打开过就丢了新端的标签」这种事。
///
/// 记录里连**每张标签的画布内容**一起写（旧端就是这么做的）：标签里可能带着还没落盘的编辑，
/// 只记路径的话，这些编辑在关窗口后就没了。
/// </summary>
internal static class CanvasTabStore
{
    public const string FileName = "canvas-tabs.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed class TabsFile
    {
        public Guid ActiveTabId { get; set; }
        public List<TabItem> Tabs { get; set; } = new();
    }

    private sealed class TabItem
    {
        public Guid Id { get; set; }
        public string? Path { get; set; }
        public RecentCanvasState? Snapshot { get; set; }
    }

    /// <summary>读上次的标签记录。读不到（没记录 / 记录损坏 / 没有项目）一律返回 false，交给调用方走「找最近的画布」。</summary>
    public static bool TryLoad(out List<CanvasTab> tabs, out Guid activeTabId)
    {
        tabs = new List<CanvasTab>();
        activeTabId = Guid.Empty;
        try
        {
            var path = AppPaths.Combine(FileName);
            if (!File.Exists(path)) return false;

            var file = JsonSerializer.Deserialize<TabsFile>(File.ReadAllText(path));
            if (file?.Tabs is not { Count: > 0 }) return false;

            foreach (var item in file.Tabs)
            {
                // 快照优先：它可能比画布库里的文件更新（带着未保存的编辑）。快照缺失才回落到画布文件。
                var snapshot = item.Snapshot;
                if (snapshot is null && !string.IsNullOrWhiteSpace(item.Path)) CanvasLibrary.TryLoad(item.Path, out snapshot);
                if (snapshot is null) continue;

                tabs.Add(new CanvasTab(snapshot)
                {
                    Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                    Path = item.Path
                });
            }

            activeTabId = file.ActiveTabId;
            return tabs.Count > 0;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            tabs.Clear();
            return false;
        }
    }

    /// <summary>写标签记录。失败把原因交出来（由调用方决定要不要说），不吞掉。</summary>
    public static bool TrySave(IReadOnlyList<CanvasTab> tabs, Guid activeTabId, out string? error)
    {
        error = null;
        try
        {
            var file = new TabsFile { ActiveTabId = activeTabId };
            file.Tabs = tabs
                .Select(tab => new TabItem { Id = tab.Id, Path = tab.Path, Snapshot = tab.Snapshot })
                .ToList();
            File.WriteAllText(AppPaths.Combine(FileName), JsonSerializer.Serialize(file, Options));
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = failure.Message;
            return false;
        }
    }
}
