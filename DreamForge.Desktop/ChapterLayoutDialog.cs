namespace DreamForge.Desktop;

/// <summary>
/// 章节泳道布局对话框（批次 C / C-1、C-2、C-3）：桌面端可达的局部布局入口。
///
/// 语义：
/// · 「预览整画布」/「预览本章」只计算不改画布，结果区列出泳道顺序与位置改动；
/// · 「应用」在副本上写入，压入撤销快照；存在阻断冲突整批拒绝；
/// · 手动摆放的节点默认保留原位，必须勾选「自动布局覆盖」才会被移动；
/// · 「保存画布」走统一保存入口，按真实结果提示成功/失败；
/// · 引用区提供临时展开/收起、锁定版本缺失检查与资源反向定位（不写入画布）。
/// </summary>
public sealed class ChapterLayoutDialog : Form
{
    private readonly Action<WorkflowCanvasState>? apply;
    private readonly Func<Guid, string>? describeReferences;
    private readonly Func<Guid, bool>? focusNode;
    private readonly bool readOnly;
    private readonly ListView chapterList = new();
    private readonly ComboBox entityBox = new();
    private readonly CheckBox overrideBox = new();
    private readonly TextBox resultBox = new();
    private readonly Label statusLabel = new();
    private readonly List<Button> mutatingButtons = new();

    public ChapterLayoutDialog(
        WorkflowCanvasState state,
        bool readOnly,
        Func<WorkflowCanvasState, bool>? saveHandler = null,
        Action<WorkflowCanvasState>? apply = null,
        Func<Guid, string>? describeReferences = null,
        Func<Guid, bool>? focusNode = null)
    {
        Session = new CanvasLayoutSession(state, saveHandler);
        this.readOnly = readOnly;
        this.apply = apply;
        this.describeReferences = describeReferences;
        this.focusNode = focusNode;

        Text = readOnly ? "章节泳道布局 — 只读画布" : "章节泳道布局";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 740);
        Font = Theme.UiFont;
        BackColor = Theme.PanelBg;

        chapterList.Dock = DockStyle.Fill;
        chapterList.View = View.Details;
        chapterList.FullRowSelect = true;
        chapterList.MultiSelect = false;
        chapterList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        chapterList.BackColor = Theme.PanelBg;
        chapterList.ForeColor = Theme.Text;
        chapterList.Columns.Add("泳道", 260);
        chapterList.Columns.Add("节点", 60);
        chapterList.Columns.Add("手动摆放", 90);
        chapterList.Columns.Add("区块位置", 200);

        var layoutRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(6) };
        AddButton(layoutRow, "预览整画布", (_, _) => PreviewAll(), mutating: true);
        AddButton(layoutRow, "预览本章", (_, _) => PreviewChapter(), mutating: true);
        AddButton(layoutRow, "应用布局", (_, _) => ApplyPlan(), mutating: true);
        AddButton(layoutRow, "撤销上次布局", (_, _) => UndoLayout(), mutating: true);
        overrideBox.Text = "自动布局覆盖（移动手动摆放的节点）";
        overrideBox.AutoSize = true;
        overrideBox.ForeColor = Theme.TextMuted;
        overrideBox.Margin = new Padding(10, 8, 6, 0);
        layoutRow.Controls.Add(overrideBox);

        var referenceRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(6) };
        referenceRow.Controls.Add(new Label { Text = "资源", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(6, 9, 0, 0) });
        entityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        entityBox.Width = 240;
        entityBox.Margin = new Padding(4, 6, 8, 0);
        referenceRow.Controls.Add(entityBox);
        AddButton(referenceRow, "反向定位引用", (_, _) => LocateReferences(), mutating: false);
        AddButton(referenceRow, "检查锁定版本缺失", (_, _) => CheckMissingVersions(), mutating: false);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        var saveButton = new Button { Text = "保存画布", Width = 100, Height = 30 };
        saveButton.Click += (_, _) =>
        {
            if (readOnly) { statusLabel.ForeColor = Theme.Warning; statusLabel.Text = "只读画布：已禁用保存，不会覆盖原文件。"; return; }
            if (Session.SaveCurrent())
            {
                statusLabel.ForeColor = Theme.TextMuted;
                statusLabel.Text = "已通过统一保存入口写入（含备份与原子替换）。";
                resultBox.Text = "保存成功：画布已写入磁盘。";
            }
            else
            {
                statusLabel.ForeColor = Theme.Warning;
                statusLabel.Text = "保存失败：磁盘文件未改动，当前画布仍在内存中，可继续编辑或重试保存。";
                resultBox.Text = "保存未成功（只读拒绝、备份失败、权限或写入失败等）。画布未丢失，可继续编辑或重试。";
            }
        };
        actionRow.Controls.Add(saveButton);

        var close = new Button { Text = "关闭", Width = 70, Height = 30 };
        close.Click += (_, _) => Close();
        actionRow.Controls.Add(close);

        statusLabel.AutoSize = true;
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Margin = new Padding(12, 8, 0, 0);
        actionRow.Controls.Add(statusLabel);

        resultBox.Dock = DockStyle.Bottom;
        resultBox.Height = 240;
        resultBox.Multiline = true;
        resultBox.ReadOnly = true;
        resultBox.ScrollBars = ScrollBars.Both;
        resultBox.WordWrap = false;
        resultBox.BackColor = Color.White;
        resultBox.Font = new Font("Consolas", 9f);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        host.Controls.Add(chapterList);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
        layout.Controls.Add(layoutRow, 0, 0);
        layout.Controls.Add(host, 0, 1);
        layout.Controls.Add(referenceRow, 0, 2);
        layout.Controls.Add(resultBox, 0, 3);
        Controls.Add(layout);
        Controls.Add(actionRow);

        if (readOnly)
        {
            foreach (var button in mutatingButtons) button.Enabled = false;
            saveButton.Enabled = false;
            overrideBox.Enabled = false;
            statusLabel.ForeColor = Theme.Warning;
            statusLabel.Text = "只读画布（来源格式版本高于当前支持版本）：布局与保存已禁用。";
            resultBox.Text = "只读画布：可以查看泳道与引用信息，但不能改动画布，也不会覆盖原文件。";
        }

        RefreshChapterList();
        RefreshEntityList();
    }

    /// <summary>当前会话（供入口级回归与冒烟按真实按钮路径驱动）。</summary>
    public CanvasLayoutSession Session { get; }

    public WorkflowCanvasState CurrentState => Session.State;

    public string LastSummary => Session.LastSummary;

    public bool LastBlocked => Session.LastBlocked;

    private void AddButton(Control parent, string text, EventHandler onClick, bool mutating)
    {
        var button = new Button { Text = text, Width = 108, Height = 30 };
        button.Click += onClick;
        parent.Controls.Add(button);
        if (mutating) mutatingButtons.Add(button);
    }

    private Guid? SelectedChapterId() =>
        chapterList.SelectedItems.Count > 0 && chapterList.SelectedItems[0].Tag is ChapterInfo info ? info.Id : null;

    private void PreviewAll()
    {
        if (GuardReadOnly()) return;
        Session.PreviewAll(new CanvasLayoutOptions(OverrideManual: overrideBox.Checked));
        Publish("已生成整画布泳道布局预览（尚未写入画布）。");
    }

    private void PreviewChapter()
    {
        if (GuardReadOnly()) return;
        if (SelectedChapterId() is not { } id) { Blocked("请先在列表里选择一个章节泳道。"); return; }
        Session.PreviewChapter(id, new CanvasLayoutOptions(OverrideManual: overrideBox.Checked));
        Publish("已生成本章局部重排预览（其他章节不动）。");
    }

    private void ApplyPlan()
    {
        if (GuardReadOnly()) return;
        if (!Session.HasPreview && Session.PendingPlan is null) { Blocked("请先预览布局，再应用。"); return; }
        if (Session.Apply(confirmManual: overrideBox.Checked))
        {
            Publish("已应用布局改动（磁盘未变，保存后写入）。", forceApply: true);
            return;
        }

        if (Session.LastBlocked)
        {
            statusLabel.ForeColor = Theme.Warning;
            statusLabel.Text = "布局被拒绝，画布未改动。";
            resultBox.Text = Session.LastSummary;
            RefreshChapterList(keep: SelectedChapterId());
            return;
        }

        Blocked(Session.LastSummary);
    }

    private void UndoLayout()
    {
        if (GuardReadOnly()) return;
        if (!Session.Undo()) { statusLabel.ForeColor = Theme.Warning; statusLabel.Text = "没有可撤销的布局改动。"; resultBox.Text = "没有可撤销的布局改动。"; return; }
        Publish("已撤销最近一次布局改动（磁盘未变，保存后写入）。", forceApply: true);
    }

    private void LocateReferences()
    {
        if (entityBox.SelectedItem is not EntityChoice choice) { Blocked("请先选择一个资源。"); return; }
        var text = describeReferences?.Invoke(choice.Id) ?? "（未接入反向定位）";
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = "已按稳定实体 ID 反向定位引用节点。";
        resultBox.Text = text;
        focusNode?.Invoke(choice.Id);
    }

    private void CheckMissingVersions()
    {
        var missing = CanvasSwimlaneLayout.Lanes(Session.State)
            .SelectMany(lane => lane.NodeIds)
            .Select(id => Session.State.Nodes.FirstOrDefault(node => node.Id == id))
            .Where(node => node is not null)
            .Select(node => node!)
            .SelectMany(node => Session.State.ResolveReferencePairs(node)
                .Where(pair => pair.Reference.VariantVersionId is not null && pair.Content is not null && pair.Content.Version is null)
                .Select(pair => $"{node.Title} → {(pair.Content!.Entity.Name)} · 锁定版本已缺失"))
            .ToList();

        statusLabel.ForeColor = missing.Count > 0 ? Theme.Warning : Theme.TextMuted;
        statusLabel.Text = missing.Count > 0
            ? $"发现 {missing.Count} 条锁定版本缺失的引用（阻断状态，未写入画布）。"
            : "没有锁定版本缺失的引用。";
        resultBox.Text = missing.Count > 0
            ? string.Join(Environment.NewLine, missing) + Environment.NewLine + Environment.NewLine + "（这些引用在画布上以红色「锁定版本缺失」徽标标出）"
            : "所有锁定引用都能解析到具体版本。";
    }

    private bool GuardReadOnly()
    {
        if (!readOnly) return false;
        statusLabel.ForeColor = Theme.Warning;
        statusLabel.Text = "只读画布：布局操作已禁用。";
        return true;
    }

    private void Blocked(string reason)
    {
        statusLabel.ForeColor = Theme.Warning;
        statusLabel.Text = reason;
        resultBox.Text = reason + Environment.NewLine + "（画布未改动）";
    }

    private void Publish(string status, bool forceApply = false)
    {
        statusLabel.ForeColor = Session.LastBlocked ? Theme.Warning : Theme.TextMuted;
        statusLabel.Text = status;
        resultBox.Text = Session.LastSummary;
        if (forceApply) apply?.Invoke(Session.State);
        RefreshChapterList(keep: SelectedChapterId());
    }

    private void RefreshChapterList(Guid? keep = null)
    {
        chapterList.BeginUpdate();
        chapterList.Items.Clear();
        var selectedRow = -1;
        foreach (var lane in CanvasSwimlaneLayout.Lanes(Session.State))
        {
            if (lane.NodeIds.Count == 0) continue;
            var manual = lane.NodeIds.Count(id => Session.State.Nodes.FirstOrDefault(node => node.Id == id)?.ManualPosition == true);
            var row = new ListViewItem(CanvasLayoutPlan.LaneLabel(lane));
            row.SubItems.Add(lane.NodeIds.Count.ToString());
            row.SubItems.Add(manual > 0 ? $"{manual} 个" : "—");
            var bounds = CanvasSwimlaneLayout.LaneBounds(Session.State, new[] { lane });
            row.SubItems.Add(bounds.Count > 0 ? $"({bounds[0].Bounds.X:0},{bounds[0].Bounds.Y:0}) {bounds[0].Bounds.Width:0}×{bounds[0].Bounds.Height:0}" : "—");
            row.Tag = lane;
            chapterList.Items.Add(row);
            if (keep is { } id && lane.ChapterId == id) selectedRow = row.Index;
        }

        chapterList.EndUpdate();
        if (selectedRow >= 0 && selectedRow < chapterList.Items.Count) chapterList.Items[selectedRow].Selected = true;
    }

    private void RefreshEntityList()
    {
        var referenced = Session.State.Nodes
            .SelectMany(node => node.References)
            .Select(reference => reference.EntityId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Select(id => Session.State.FindEntity(id))
            .Where(entity => entity is not null)
            .Select(entity => entity!)
            .OrderBy(entity => entity.Name, StringComparer.Ordinal)
            .ToList();

        entityBox.BeginUpdate();
        entityBox.Items.Clear();
        foreach (var entity in referenced)
            entityBox.Items.Add(new EntityChoice(entity.Id, $"{WorkflowEntity.KindName(entity.Kind)}「{entity.Name}」"));
        entityBox.EndUpdate();
        if (entityBox.Items.Count > 0) entityBox.SelectedIndex = 0;
    }

    /// <summary>资源下拉项：Id 是稳定实体 ID，显示文本仅供人读。</summary>
    private sealed record EntityChoice(Guid Id, string Text)
    {
        public override string ToString() => Text;
    }
}
