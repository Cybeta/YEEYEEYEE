using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>
/// 章节结构操作的会话（返工 B-UI-01）：界面按钮与回归测试走同一条逻辑。
///
/// 规则：
/// · 每个操作都调用 <see cref="CanvasChapterOperations"/>，只在副本上执行；
/// · 存在阻断冲突时不替换当前状态，只记录冲突文本（界面据此提示，画布保持原样）；
/// · 成功应用后压入撤销快照，<see cref="Undo"/> 回到操作前；
/// · 保存由调用方注入（桌面端为统一保存入口 <c>CanvasSaveService.Save</c>），
///   保存结果必须原样返回并落入 <see cref="LastSaveSucceeded"/>，界面据此显示成功或失败（返工 B-UI-02）。
/// </summary>
public sealed class CanvasChapterStructureSession
{
    private static readonly JsonSerializerOptions Options = new();
    private readonly Stack<string> undo = new();

    /// <summary>最近一次成功保存（或初始载入）时的状态快照，用于判定是否有未落盘的改动。</summary>
    private string savedSnapshot;

    public CanvasChapterStructureSession(WorkflowCanvasState state, Func<WorkflowCanvasState, bool>? save = null)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Save = save;
        // 初始状态来自已载入的画布，视为与磁盘一致。
        savedSnapshot = Serialize(State);
    }

    /// <summary>当前画布状态；每次成功操作后替换为新状态（副本）。</summary>
    public WorkflowCanvasState State { get; private set; }

    /// <summary>保存回调：返回是否保存成功；为空表示没有接入统一保存入口，保存一律视为失败。</summary>
    public Func<WorkflowCanvasState, bool>? Save { get; }

    public ChapterOperationResult? LastResult { get; private set; }

    public bool CanUndo => undo.Count > 0;

    public string LastSummary { get; private set; } = string.Empty;

    public bool LastBlocked { get; private set; }

    /// <summary>最近一次保存是否成功；还没有点过保存时为 <c>null</c>。</summary>
    public bool? LastSaveSucceeded { get; private set; }

    /// <summary>相对最近一次成功保存（或初始载入）是否还有未落盘的改动。保存失败时保持为 true。</summary>
    public bool HasUnsavedChanges => !string.Equals(Serialize(State), savedSnapshot, StringComparison.Ordinal);

    public ChapterOperationResult Create(string name, Guid? parentChapterId = null) =>
        Apply(CanvasChapterOperations.Create(State, name, parentChapterId));

    public ChapterOperationResult Rename(Guid chapterId, string name) =>
        Apply(CanvasChapterOperations.Rename(State, chapterId, name));

    public ChapterOperationResult Reorder(Guid chapterId, int order) =>
        Apply(CanvasChapterOperations.Reorder(State, chapterId, order));

    public ChapterOperationResult Move(Guid chapterId, Guid? newParentChapterId) =>
        Apply(CanvasChapterOperations.Move(State, chapterId, newParentChapterId));

    public ChapterOperationResult Split(Guid chapterId, string newName, IReadOnlyList<Guid> nodeIds) =>
        Apply(CanvasChapterOperations.Split(State, chapterId, newName, nodeIds));

    public ChapterOperationResult Merge(Guid sourceChapterId, Guid targetChapterId) =>
        Apply(CanvasChapterOperations.Merge(State, sourceChapterId, targetChapterId));

    public ChapterOperationResult Delete(Guid chapterId, bool reassignNodesToParent) =>
        Apply(CanvasChapterOperations.Delete(State, chapterId, reassignNodesToParent));

    public ChapterOperationResult NormalizeOrder() => Apply(CanvasChapterOperations.NormalizeOrder(State));

    /// <summary>把一条操作结果落到会话里：阻断则不改状态，成功则替换并记录快照。</summary>
    private ChapterOperationResult Apply(ChapterOperationResult result)
    {
        LastResult = result;
        LastBlocked = result.HasBlockingConflicts;
        if (result.HasBlockingConflicts)
        {
            LastSummary = "操作被阻断，画布未改动。" + Environment.NewLine + result.ToText();
            return result;
        }

        if (result.Changed)
        {
            undo.Push(Serialize(State));
            State = result.Canvas;
        }

        LastSummary = result.ToText();
        return result;
    }

    /// <summary>撤销最近一次成功操作；返回是否真的撤销了。撤销只改内存，磁盘不变，因此会回到「有未保存改动」。</summary>
    public bool Undo()
    {
        if (!undo.TryPop(out var json)) return false;
        State = JsonSerializer.Deserialize<WorkflowCanvasState>(json, Options) ?? State;
        LastResult = null;
        LastBlocked = false;
        LastSummary = "已撤销最近一次章节结构操作。";
        return true;
    }

    /// <summary>
    /// 通过注入的保存回调保存当前状态（桌面端即统一保存入口）。返回真实的成功/失败结果，
    /// 并记录到 <see cref="LastSaveSucceeded"/>；只有成功时才刷新已保存快照，失败时内存画布与待保存状态都保持不变。
    /// </summary>
    public bool SaveCurrent()
    {
        if (Save is null) { LastSaveSucceeded = false; return false; }
        var ok = Save(State);
        LastSaveSucceeded = ok;
        if (ok) savedSnapshot = Serialize(State);
        return ok;
    }

    public void Reset()
    {
        undo.Clear();
        LastResult = null;
        LastBlocked = false;
        LastSaveSucceeded = null;
        LastSummary = string.Empty;
    }

    private static string Serialize(WorkflowCanvasState state) => JsonSerializer.Serialize(state, Options);
}
