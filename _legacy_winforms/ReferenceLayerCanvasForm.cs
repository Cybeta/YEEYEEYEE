namespace DreamForge.Desktop;

using System.Drawing.Drawing2D;
using System.Text.Json;

/// <summary>引用资源画布。单击选择，双击进入资源，编辑以草稿保存。</summary>
public sealed class ReferenceLayerCanvasForm : Form
{
    private readonly WorkflowCanvasControl sourceCanvas;
    private readonly CanvasReferenceActivation root;
    private readonly Action<CanvasReferenceActivation> openMedia;
    private readonly Func<WorkflowEntity, WorkflowEntity, bool> saveCanvas;
    private readonly Func<bool> canEdit;
    private readonly Panel canvasHost = new();
    private readonly Label breadcrumb = new();
    private readonly Stack<LayerFrame> undoStack = new();
    private readonly Stack<LayerFrame> redoStack = new();
    private readonly Stack<LayerFrame> navigationUndo = new();
    private readonly Stack<LayerFrame> navigationRedo = new();
    private readonly HashSet<string> activePath = new(StringComparer.Ordinal);
    private Button? backButton;
    private Button? undoButton;
    private Button? redoButton;
    private Button? saveButton;
    private Button? addButton;
    private Button? removeButton;
    private LayerFrame current;
    private string savedFingerprint = "";
    private Button? editButton;
    private bool IsDirty => current.Fingerprint() != savedFingerprint;
    private static List<WorkflowAttachment> CloneAttachments(IEnumerable<WorkflowAttachment> items) => JsonSerializer.Deserialize<List<WorkflowAttachment>>(JsonSerializer.Serialize(items))!;

    private sealed class LayerFrame(string title, CanvasReferenceActivation? activation, List<CanvasReferenceActivation> items)
    {
        public string Title { get; } = title;
        public CanvasReferenceActivation? Activation { get; } = activation;
        public List<CanvasReferenceActivation> Items { get; } = items;
        public string Description { get; set; } = activation?.Content?.Description ?? "";
        public List<WorkflowAttachment> Attachments { get; set; } = CloneAttachments(activation?.Content?.Attachments ?? Array.Empty<WorkflowAttachment>());
        public LayerFrame Clone() => new(Title, Activation, Items.Select(CloneActivation).ToList()) { Description = Description, Attachments = CloneAttachments(Attachments) };
        public string Fingerprint() => JsonSerializer.Serialize(new { Description, Attachments, References = Items.Select(item => item.Reference).ToList() });
    }

    public ReferenceLayerCanvasForm(WorkflowCanvasControl sourceCanvas, CanvasReferenceActivation root, Action<CanvasReferenceActivation> openMedia, Func<WorkflowEntity, WorkflowEntity, bool> saveCanvas, Func<bool> canEdit)
    {
        this.sourceCanvas = sourceCanvas;
        this.root = root;
        this.openMedia = openMedia;
        this.saveCanvas = saveCanvas;
        this.canEdit = canEdit;
        activePath.Add(ReferenceKey(root.Reference));
        current = BuildFrame(root.Content?.Label ?? "引用目标不可用", root, root.Content?.References ?? Array.Empty<NodeReference>());
        Text = "引用层画布";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 520);
        Size = new Size(980, 680);
        BackColor = Theme.EditorBg;
        KeyPreview = true;
        KeyDown += OnKeyDown;
        savedFingerprint = current.Fingerprint();
        BuildUi();
        Render();
    }

    private void BuildUi()
    {
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, AutoScroll = true, BackColor = Theme.PanelBg, Padding = new Padding(14, 10, 14, 8) };
        backButton = Button("返回上一层", UndoNavigation);
        undoButton = Button("撤销", UndoEdit);
        redoButton = Button("重做", RedoEdit);
        addButton = Button("新增下级引用", AddReference);
        removeButton = Button("删除选中引用", RemoveReference);
        saveButton = Button("保存写回", SaveEdits);
        editButton = Button("编辑当前资源", EditResource);
        var buttons = new[] { backButton, undoButton, redoButton, addButton, removeButton, editButton, saveButton };
        var x = 0;
        foreach (var button in buttons) { button.Left = x; header.Controls.Add(button); x += button.Width + 8; }
        breadcrumb.Left = x + 8;
        breadcrumb.Top = 14;
        breadcrumb.AutoSize = true;
        breadcrumb.ForeColor = Theme.TextMuted;
        header.Controls.Add(breadcrumb);
        canvasHost.Dock = DockStyle.Fill;
        canvasHost.BackColor = Theme.EditorBg;
        Controls.Add(canvasHost);
        Controls.Add(header);
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action();
        return button;
    }

    private LayerFrame BuildFrame(string title, CanvasReferenceActivation? activation, IEnumerable<NodeReference> references)
    {
        var items = references.Select(reference => new CanvasReferenceActivation(
            activation?.Node ?? root.Node,
            CloneReference(reference),
            sourceCanvas.State.ResolveReferenceContent(reference))).ToList();
        return new LayerFrame(title, activation, items);
    }

    private void Render()
    {
        breadcrumb.Text = current.Title + (current.Items.Count == 0 ? " · 没有子引用" : $" · {current.Items.Count} 个引用");
        foreach (var control in canvasHost.Controls.Cast<Control>().ToArray()) control.Dispose();
        canvasHost.Controls.Clear();
        canvasHost.Controls.Add(new ReferenceLayerCanvasControl(current.Title, current.Activation, current.Items, EnterLayer, openMedia, SelectItem, PushEdit, () => CurrentWritableVariant() is not null));
        var writable = CurrentWritableVariant() is not null;
        if (saveButton is not null) saveButton.Enabled = writable && IsDirty;
        if (editButton is not null) editButton.Enabled = writable;
        if (addButton is not null) addButton.Enabled = writable;
        if (removeButton is not null) removeButton.Enabled = writable && ((ReferenceLayerCanvasControl)canvasHost.Controls[0]).Selected is not null;
        UpdateHistoryButtons();
    }

    private void SelectItem()
    {
        if (removeButton is not null) removeButton.Enabled = CurrentWritableVariant() is not null && canvasHost.Controls[0] is ReferenceLayerCanvasControl { Selected: not null };
        if (saveButton is not null) saveButton.Enabled = CurrentWritableVariant() is not null && IsDirty;
        UpdateHistoryButtons();
    }

    private void EnterLayer(CanvasReferenceActivation activation)
    {
        if (!FinishEditing()) return;
        var key = ReferenceKey(activation.Reference);
        if (!activePath.Add(key)) { MessageBox.Show(this, "检测到循环引用，已停止继续展开。", "引用层画布", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        navigationUndo.Push(current.Clone());
        navigationRedo.Clear();
        activation = new(activation.Node, activation.Reference, sourceCanvas.State.ResolveReferenceContent(activation.Reference));
        current = BuildFrame(activation.Content?.Label ?? "引用目标不可用", activation, activation.Content?.References ?? Array.Empty<NodeReference>());
        savedFingerprint = current.Fingerprint();
        undoStack.Clear(); redoStack.Clear();
        Render();
    }

    private void AddReference()
    {
        if (CurrentWritableVariant() is null) return;
        var candidates = sourceCanvas.State.Entities.Where(entity => !entity.IsProjectMissing)
            .SelectMany(entity => entity.Variants.Select(variant => (entity, variant)))
            .Where(item => !activePath.Contains(ReferenceKey(new NodeReference { EntityId = item.entity.Id, VariantId = item.variant.Id }))).ToList();
        if (candidates.Count == 0) return;
        using var dialog = new Form { Text = "新增下级引用", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(440, 120), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var list = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var item in candidates) list.Items.Add($"{WorkflowEntity.KindName(item.entity.Kind)} · {item.entity.Name} · {item.variant.Name}");
        list.SelectedIndex = 0;
        var ok = new Button { Text = "确定", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.OK };
        dialog.Controls.Add(list); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return;
        PushEdit();
        var selected = candidates[list.SelectedIndex];
        current.Items.Add(new CanvasReferenceActivation(current.Activation?.Node ?? root.Node, new NodeReference { EntityId = selected.entity.Id, VariantId = selected.variant.Id }, sourceCanvas.State.ResolveReferenceContent(new NodeReference { EntityId = selected.entity.Id, VariantId = selected.variant.Id })));
        Render();
    }

    private void RemoveReference()
    {
        if (canvasHost.Controls[0] is not ReferenceLayerCanvasControl control || control.Selected is null || CurrentWritableVariant() is null) return;
        PushEdit();
        current.Items.Remove(control.Selected);
        Render();
    }

    private void PushEdit()
    {
        undoStack.Push(current.Clone());
        redoStack.Clear();
    }

    private void UndoEdit()
    {
        if (undoStack.Count == 0 || CurrentWritableVariant() is null) return;
        redoStack.Push(current.Clone());
        current = undoStack.Pop();
        Render();
    }

    private void RedoEdit()
    {
        if (redoStack.Count == 0 || CurrentWritableVariant() is null) return;
        undoStack.Push(current.Clone());
        current = redoStack.Pop();
        Render();
    }

    private void UndoNavigation()
    {
        if (navigationUndo.Count == 0 || !FinishEditing()) return;
        if (current.Activation is not null) activePath.Remove(ReferenceKey(current.Activation.Reference));
        var previous = navigationUndo.Pop();
        var activation = previous.Activation!;
        activation = new(activation.Node, activation.Reference, sourceCanvas.State.ResolveReferenceContent(activation.Reference));
        current = BuildFrame(previous.Title, activation, activation.Content?.References ?? Array.Empty<NodeReference>());
        savedFingerprint = current.Fingerprint();
        undoStack.Clear(); redoStack.Clear();
        Render();
    }

    private bool FinishEditing()
    {
        if (!IsDirty) return true;
        var answer = MessageBox.Show(this, "当前资源有未保存编辑，是否保存？", "引用画布", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.No) return true;
        SaveEdits();
        return !IsDirty;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!FinishEditing()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    private void EditResource()
    {
        if (CurrentWritableVariant() is null) return;
        using var dialog = new Form { Text = "编辑当前资源", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(540, 360) };
        var description = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = current.Description };
        var attachments = CloneAttachments(current.Attachments);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42 };
        actions.Controls.Add(Button("替换媒体", () =>
        {
            using var picker = new OpenFileDialog { Filter = "媒体|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.mov;*.webm|所有文件|*.*" };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            try
            {
                var target = Path.Combine(AssetStore.EnsureDirectory(), $"{Guid.NewGuid():N}{Path.GetExtension(picker.FileName)}");
                File.Copy(picker.FileName, target);
                attachments = new() { new WorkflowAttachment { Reference = AssetStore.ToReference(target), Name = Path.GetFileName(picker.FileName), Kind = WorkflowAttachment.KindOf(target), Source = "引用画布编辑" } };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { MessageBox.Show(dialog, error.Message, "媒体导入失败"); }
        }));
        actions.Controls.Add(Button("清除媒体", () => attachments.Clear()));
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        actions.Controls.Add(ok);
        dialog.Controls.Add(description); dialog.Controls.Add(actions);
        if (dialog.ShowDialog(this) != DialogResult.OK || CurrentWritableVariant() is null) return;
        if (description.Text == current.Description && JsonSerializer.Serialize(attachments) == JsonSerializer.Serialize(current.Attachments)) return;
        PushEdit();
        current.Description = description.Text;
        current.Attachments = attachments;
        Render();
    }

    private void SaveEdits()
    {
        var variant = CurrentWritableVariant();
        if (variant is null || !IsDirty) return;
        var before = CloneEntity(FindCurrentEntity()!);
        variant.References = current.Items.Select(item => CloneReference(item.Reference)).ToList();
        variant.Description = current.Description;
        variant.Attachments = CloneAttachments(current.Attachments);
        var entity = FindCurrentEntity();
        if (entity is null) return;
        variant.Commit("引用画布编辑");
        if (!saveCanvas(entity, before))
        {
            ProjectEntityScope.RestoreInto(entity, before);
            return;
        }
        savedFingerprint = current.Fingerprint();
        undoStack.Clear(); redoStack.Clear();
        Render();
    }

    private WorkflowEntityVariant? CurrentWritableVariant()
    {
        var entity = FindCurrentEntity();
        var variant = entity?.Variants.FirstOrDefault(item => item.Id == (current.Activation?.Reference.VariantId ?? root.Reference.VariantId));
        return canEdit() && !root.Node.IsLocked && entity?.IsProjectMissing == false
            && current.Activation?.Content is { VersionMissing: false }
            && current.Activation.Reference.VariantVersionId is null
            && root.Reference.VariantVersionId is null
            && navigationUndo.All(frame => frame.Activation?.Reference.VariantVersionId is null) ? variant : null;
    }

    private WorkflowEntity? FindCurrentEntity() => sourceCanvas.State.Entities.FirstOrDefault(item => item.Id == (current.Activation?.Reference.EntityId ?? root.Reference.EntityId));

    private static bool HasPosition(CanvasReferenceActivation activation) => activation.Reference.X >= 0 && activation.Reference.Y >= 0;
    private static CanvasReferenceActivation CloneActivation(CanvasReferenceActivation activation) => new(activation.Node, CloneReference(activation.Reference), activation.Content);
    private static NodeReference CloneReference(NodeReference reference) => JsonSerializer.Deserialize<NodeReference>(JsonSerializer.Serialize(reference))!;
    private static WorkflowEntity CloneEntity(WorkflowEntity entity) => JsonSerializer.Deserialize<WorkflowEntity>(JsonSerializer.Serialize(entity))!;
    private static string ReferenceKey(NodeReference reference) => $"{reference.EntityId:N}/{reference.VariantId:N}/{reference.VariantVersionId?.ToString("N") ?? "latest"}";

    private void UpdateHistoryButtons()
    {
        if (backButton is not null) backButton.Enabled = navigationUndo.Count > 0;
        if (undoButton is not null) undoButton.Enabled = undoStack.Count > 0 && CurrentWritableVariant() is not null;
        if (redoButton is not null) redoButton.Enabled = redoStack.Count > 0 && CurrentWritableVariant() is not null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Z) { UndoEdit(); e.Handled = true; e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.Y) { RedoEdit(); e.Handled = true; e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Delete) { RemoveReference(); e.Handled = true; }
    }
}

internal sealed class ReferenceLayerCanvasControl : Control
{
    private const int CardWidth = 230, CardHeight = 188, ParentWidth = 300, ParentHeight = 92, Gap = 24;
    private readonly string title;
    private readonly CanvasReferenceActivation? parentActivation;
    private readonly List<CanvasReferenceActivation> items;
    private readonly Action<CanvasReferenceActivation> enter;
    private readonly Action<CanvasReferenceActivation> openMedia;
    private readonly Action selectedChanged;
    private readonly Action beforeMove;
    private readonly Func<bool> canMove;
    private bool moved;
    private readonly List<(Rectangle Bounds, CanvasReferenceActivation Activation)> hitRects = new();
    private CanvasReferenceActivation? selected;
    private CanvasReferenceActivation? dragging;
    private Point dragOrigin;
    private float originalX, originalY;
    public CanvasReferenceActivation? Selected => selected;

    public ReferenceLayerCanvasControl(string title, CanvasReferenceActivation? parentActivation, IReadOnlyList<CanvasReferenceActivation> items, Action<CanvasReferenceActivation> enter, Action<CanvasReferenceActivation> openMedia, Action selectedChanged, Action beforeMove, Func<bool> canMove)
    {
        this.beforeMove = beforeMove; this.canMove = canMove;
        this.title = title; this.parentActivation = parentActivation; this.items = items.ToList(); this.enter = enter; this.openMedia = openMedia; this.selectedChanged = selectedChanged;
        Dock = DockStyle.Fill; DoubleBuffered = true; BackColor = Theme.EditorBg; Cursor = Cursors.Hand;
        MouseClick += OnMouseClick; MouseDoubleClick += OnMouseDoubleClick; MouseDown += OnMouseDown; MouseMove += OnMouseMove; MouseUp += OnMouseUp;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); hitRects.Clear(); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var parent = new Rectangle(Math.Max(24, (ClientSize.Width - ParentWidth) / 2), 24, ParentWidth, ParentHeight);
        DrawParent(e.Graphics, parent, title, parentActivation);
        if (items.Count == 0) { using var brush = new SolidBrush(Theme.TextMuted); e.Graphics.DrawString("当前引用没有配置子引用。可新增下级引用或打开媒体。", Theme.UiFont, brush, 24, 150); return; }
        var columns = Math.Max(1, Math.Min(3, (ClientSize.Width - 48 + Gap) / (CardWidth + Gap)));
        var totalWidth = columns * CardWidth + (columns - 1) * Gap;
        var startX = Math.Max(24, (ClientSize.Width - totalWidth) / 2);
        var startY = parent.Bottom + 76;
        using var connector = new Pen(Theme.Border, 2f) { EndCap = LineCap.ArrowAnchor };
        foreach (var (activation, index) in items.Select((item, index) => (item, index)))
        {
            var x = activation.Reference.X >= 0 ? (int)activation.Reference.X : startX + (index % columns) * (CardWidth + Gap);
            var y = activation.Reference.Y >= 0 ? (int)activation.Reference.Y : startY + (index / columns) * (CardHeight + Gap);
            var bounds = new Rectangle(x, y, CardWidth, CardHeight); hitRects.Add((bounds, activation));
            var source = new Point(parent.Left + parent.Width / 2, parent.Bottom); var target = new Point(bounds.Left + bounds.Width / 2, bounds.Top);
            e.Graphics.DrawBezier(connector, source, new Point(source.X, source.Y + 34), new Point(target.X, source.Y + 34), target);
        }
        foreach (var (activation, index) in items.Select((item, index) => (item, index)))
        {
            var x = activation.Reference.X >= 0 ? (int)activation.Reference.X : startX + (index % columns) * (CardWidth + Gap);
            var y = activation.Reference.Y >= 0 ? (int)activation.Reference.Y : startY + (index / (Math.Max(1, columns)) * (CardHeight + Gap));
            DrawCard(e.Graphics, new Rectangle(x, y, CardWidth, CardHeight), activation, ReferenceEquals(selected, activation));
        }
    }

    private static void DrawParent(Graphics g, Rectangle bounds, string title, CanvasReferenceActivation? activation)
    {
        using var background = new SolidBrush(Theme.AccentSoft); using var border = new Pen(Theme.Accent, 2f); g.FillRectangle(background, bounds); g.DrawRectangle(border, bounds);
        using var titleBrush = new SolidBrush(Theme.Text); using var muted = new SolidBrush(Theme.TextMuted);
        var name = activation?.Content is { } content ? $"{WorkflowEntity.KindName(content.Entity.Kind)} · {content.Entity.Name}" : title;
        g.DrawString("当前引用资源", Theme.SmallFont, muted, bounds.X + 14, bounds.Y + 14); g.DrawString(name, Theme.UiFont, titleBrush, bounds.X + 14, bounds.Y + 38);
    }

    private static void DrawCard(Graphics g, Rectangle bounds, CanvasReferenceActivation activation, bool selected)
    {
        using var background = new SolidBrush(selected ? Theme.AccentSoft : Theme.PanelBg); using var border = new Pen(selected ? Theme.Accent : Theme.Border, selected ? 2 : 1); g.FillRectangle(background, bounds); g.DrawRectangle(border, bounds);
        var content = activation.Content; var kind = content is null ? "引用" : WorkflowEntity.KindName(content.Entity.Kind); var title = content is null ? "引用失效" : $"{kind} · {content.Entity.Name}"; var variant = content is null ? "无法解析实体或变体" : $"变体：{content.Variant.Name} · {content.VersionLabel}"; var child = content is null ? "" : content.References.Count > 0 ? $"可继续展开 {content.References.Count} 个引用" : "无下级引用 · 双击进入画布";
        using var titleBrush = new SolidBrush(content?.VersionMissing == true ? Theme.Danger : Theme.Text); using var mutedBrush = new SolidBrush(Theme.TextMuted); g.DrawString(title, Theme.UiFont, titleBrush, bounds.X + 12, bounds.Y + 14); g.DrawString(variant, Theme.SmallFont, mutedBrush, bounds.X + 12, bounds.Y + 42); g.DrawString(child, Theme.SmallFont, mutedBrush, bounds.X + 12, bounds.Y + 68);
        var media = new Rectangle(bounds.X + 12, bounds.Y + 94, bounds.Width - 24, 70); using var mediaBack = new SolidBrush(Theme.EditorBg); g.FillRectangle(mediaBack, media);
        var attachment = content?.Attachments.FirstOrDefault(item => item.Kind is AttachmentKind.Image or AttachmentKind.Video); if (attachment?.Kind == AttachmentKind.Image && AssetStore.Resolve(attachment.Reference) is { } path && File.Exists(path)) { try { using var image = Image.FromFile(path); g.DrawImage(image, media); return; } catch { } }
        using var placeholder = new SolidBrush(Theme.TextDim); g.DrawString(attachment?.Kind == AttachmentKind.Video ? "视频 · 双击查看" : "无媒体预览", Theme.SmallFont, placeholder, media.X + 10, media.Y + 26);
    }

    private CanvasReferenceActivation? Hit(Point p) => hitRects.LastOrDefault(item => item.Bounds.Contains(p)).Activation;
    private void OnMouseClick(object? sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; selected = Hit(e.Location); selectedChanged(); Invalidate(); }
    private void OnMouseDoubleClick(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && Hit(e.Location) is { } hit) enter(hit); }
    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || Hit(e.Location) is not { } hit) return;
        selected = hit; dragging = hit; moved = false; dragOrigin = e.Location;
        var bounds = hitRects.Last(item => ReferenceEquals(item.Activation, hit)).Bounds;
        originalX = bounds.X; originalY = bounds.Y; Capture = true; selectedChanged(); Invalidate();
    }
    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (dragging is null || e.Button != MouseButtons.Left || !canMove()) return;
        if (!moved && Math.Abs(e.X - dragOrigin.X) + Math.Abs(e.Y - dragOrigin.Y) < 4) return;
        if (!moved) { beforeMove(); moved = true; }
        dragging.Reference.X = Math.Max(24, originalX + e.X - dragOrigin.X);
        dragging.Reference.Y = Math.Max(24, originalY + e.Y - dragOrigin.Y); Invalidate();
    }
    private void OnMouseUp(object? sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; dragging = null; Capture = false; selectedChanged(); }
}
