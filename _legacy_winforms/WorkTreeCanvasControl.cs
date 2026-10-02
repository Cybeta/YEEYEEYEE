namespace DreamForge.Desktop;

/// <summary>Owner-painted tree with native text editing for the selected item's details.</summary>
public sealed class WorkTreeCanvasControl : Control
{
    private readonly List<(WorkTreeItem Item, int Depth)> rows = new();
    private readonly List<WorkTreeItem> items = new();
    private readonly HashSet<Guid> collapsed = new();
    private readonly TextBox editor = new();
    private WorkflowCanvasState? state;
    private Point dragStart;
    private int scroll;
    private bool editingName;
    private bool editing;
    public WorkTreeItem? SelectedItem { get; private set; }
    public event EventHandler<WorkTreeItem?>? ItemSelected;
    public event Action<WorkTreeItem>? ItemActivated;
    public event Action<string>? Command;
    public event Action<WorkTreeItem, string, bool>? Edited;
    public event Action<WorkTreeItem, WorkTreeItem>? ResourceDropped;
    private int TreeBottom => Math.Max(100, Height - 160);
    private Rectangle[] ToolbarBounds => Enumerable.Range(0, 4)
        .Select(i => new Rectangle(i * Width / 4, 0, (i + 1) * Width / 4 - i * Width / 4, 36)).ToArray();

    public WorkTreeCanvasControl()
    {
        DoubleBuffered = true; AllowDrop = true; TabStop = true; BackColor = Theme.PanelBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        editor.Multiline = true;
        editor.AcceptsReturn = true;
        editor.BorderStyle = BorderStyle.FixedSingle;
        editor.Visible = false;
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { CancelEdit(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter && (!editor.Multiline || !e.Shift))
            {
                CommitEdit();
                e.SuppressKeyPress = true;
            }
        };
        Controls.Add(editor);
        Resize += (_, _) => PositionEditor();
        MouseWheel += (_, e) => { scroll = Math.Clamp(scroll - e.Delta / 3, 0, Math.Max(0, rows.Count * 32 - TreeBottom + 44)); Invalidate(); };
        MouseDown += (_, e) =>
        {
            dragStart = e.Location;
            var toolbar = ToolbarBounds;
            var hitIndex = Array.FindIndex(toolbar, rect => rect.Contains(e.Location));
            if (hitIndex >= 0)
            {
                Command?.Invoke(new[] { "chapter", "resource", "lock", "latest" }[hitIndex]);
                return;
            }
            if (e.Y >= TreeBottom)
            {
                if (SelectedItem is null) return;
                editingName = e.Y < TreeBottom + 38;
                BeginEdit();
                return;
            }
            FinishEdit(false);
            var index = (e.Y - 40 + scroll) / 32;
            if (index < 0 || index >= rows.Count) return;
            var row = rows[index]; SelectedItem = row.Item;
            if (e.X < 24 + row.Depth * 16) { if (!collapsed.Add(row.Item.Id)) collapsed.Remove(row.Item.Id); Rebuild(); }
            ItemSelected?.Invoke(this, SelectedItem);
            if (e.Clicks >= 2 && SelectedItem is { Kind: WorkTreeKind.Resource })
                ItemActivated?.Invoke(SelectedItem);
            Invalidate();
        };
        MouseMove += (_, e) => { if (e.Button == MouseButtons.Left && SelectedItem is not null && dragStart.Y < TreeBottom && dragStart.Y > 36 && Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) > 10) { DoDragDrop(SelectedItem, DragDropEffects.Copy); dragStart = e.Location; } };
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(typeof(WorkTreeItem)) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { var p = PointToClient(new Point(e.X, e.Y)); var index = (p.Y - 40 + scroll) / 32; if (p.Y >= 40 && p.Y < TreeBottom && index >= 0 && index < rows.Count && e.Data?.GetData(typeof(WorkTreeItem)) is WorkTreeItem source) ResourceDropped?.Invoke(source, rows[index].Item); };
    }

    public void SetState(WorkflowCanvasState value)
    {
        state = value;
        items.Clear();
        items.AddRange(value.WorkTree);
        Rebuild();
    }

    public void SetItems(IEnumerable<WorkTreeItem> source, WorkTreeItem? keepSelected = null)
    {
        items.Clear();
        items.AddRange(source);
        if (state is not null)
        {
            state.WorkTree.Clear();
            state.WorkTree.AddRange(items);
        }
        var selectedId = keepSelected?.Id ?? SelectedItem?.Id;
        SelectedItem = selectedId is { } id ? items.FirstOrDefault(x => x.Id == id) : null;
        Rebuild();
    }

    public void Select(WorkTreeItem? item)
    {
        SelectedItem = item is null ? null : items.FirstOrDefault(x => x.Id == item.Id);
        Invalidate();
    }

    private void BeginEdit()
    {
        if (SelectedItem is null) return;
        editing = true;
        editor.Multiline = !editingName;
        editor.AcceptsReturn = !editingName;
        editor.Text = editingName ? SelectedItem.Name : SelectedItem.Kind == WorkTreeKind.Appearance ? SelectedItem.LocalState : SelectedItem.Prompt;
        PositionEditor();
        editor.Visible = true;
        editor.BringToFront();
        editor.Focus();
        editor.SelectAll();
    }

    private void PositionEditor()
    {
        if (!editing || editor.IsDisposed) return;
        editor.Bounds = editingName
            ? new Rectangle(10, TreeBottom + 6, Math.Max(40, Width - 20), 30)
            : new Rectangle(10, TreeBottom + 38, Math.Max(40, Width - 20), Math.Max(40, Height - TreeBottom - 72));
    }

    private void CommitEdit()
    {
        if (!editing || SelectedItem is null) return;
        var value = editor.Text;
        FinishEdit(false);
        Edited?.Invoke(SelectedItem, value, editingName);
    }

    private void CancelEdit() => FinishEdit(true);

    private void FinishEdit(bool cancel)
    {
        if (!editing) return;
        editing = false;
        editor.Visible = false;
        if (cancel) editor.Clear();
        Focus();
        Invalidate();
    }

    private void Rebuild()
    {
        var validIds = items.Select(item => item.Id).ToHashSet();
        collapsed.RemoveWhere(id => !validIds.Contains(id));
        rows.Clear();
        var seen = new HashSet<Guid>();
        void Add(WorkTreeItem item, int depth)
        {
            if (!seen.Add(item.Id)) return;
            rows.Add((item, depth));
            if (collapsed.Contains(item.Id)) return;
            foreach (var child in items.Where(x => x.ParentId == item.Id).OrderBy(x => x.Order)) Add(child, depth + 1);
        }
        foreach (var item in items.Where(x => x.ParentId is null).OrderBy(x => x.Kind == WorkTreeKind.Resource ? 1 : 0).ThenBy(x => x.Order)) Add(item, 0);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics;
        void Text(string text, Rectangle rect, Color color) => TextRenderer.DrawText(g, text, Theme.SmallFont, rect, color, TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
        var labels = new[] { "+ 章节", "+ 资源", "锁定当前版", "跟随最新" };
        var toolbar = ToolbarBounds;
        for (var i = 0; i < toolbar.Length; i++) Text(labels[i], Rectangle.Inflate(toolbar[i], -5, 5), Theme.Text);
        g.SetClip(new Rectangle(0, 38, Width, Math.Max(0, TreeBottom - 38)));
        for (var i = 0; i < rows.Count; i++) { var (item, depth) = rows[i]; var y = 40 + i * 32 - scroll; if (y + 32 < 38 || y > TreeBottom) continue; if (SelectedItem?.Id == item.Id) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 0, y, Width, 31); Text((collapsed.Contains(item.Id) ? "+ " : "− ") + item.Name, new Rectangle(8 + depth * 16, y + 5, Math.Max(30, Width - 112 - depth * 16), 26), Theme.Text); Text(item.Kind == WorkTreeKind.Appearance ? item.SourceVersionId is null ? "出场 · 最新" : "出场 · 已锁版" : WorkTreeItem.KindName(item.Kind), new Rectangle(Width - 100, y + 5, 96, 26), Theme.TextMuted); }
        g.ResetClip(); using (var b = new SolidBrush(Theme.EditorBg)) g.FillRectangle(b, 0, TreeBottom, Width, Height - TreeBottom);
        if (SelectedItem is not { } selected) { Text("选择条目；拖资源到章节创建出场，拖出场到画布。", new Rectangle(10, TreeBottom + 10, Width - 20, 100), Theme.TextMuted); return; }
        if (!editing || editingName) Text(selected.Name, new Rectangle(10, TreeBottom + 8, Width - 20, 28), Theme.Text);
        if (!editing || !editingName) Text(selected.Kind == WorkTreeKind.Appearance ? selected.LocalState : selected.Prompt, new Rectangle(10, TreeBottom + 40, Width - 20, 82), Theme.Text);
        Text("点击编辑 · Enter 保存 · Escape 取消 · Shift+Enter 换行", new Rectangle(10, Height - 30, Width - 20, 26), Theme.TextMuted);
    }
}
