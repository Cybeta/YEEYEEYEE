namespace DreamForge.Desktop;

/// <summary>
/// 章节结构编辑对话框（返工 B-UI-01）：桌面端可达的章节建立、改名、排序、移动、拆分、合并、删除入口。
///
/// 约束（与任务书一致）：
/// · 全部按钮调用 <see cref="CanvasChapterOperations"/>（经 <see cref="CanvasChapterStructureSession"/>），按 ID 操作，不按名称猜身份；
/// · 有阻断冲突时不替换画布，只在结果区与状态行显示冲突（画布保持原样）；
/// · 操作成功即通过 <c>apply</c> 回写界面画布，保存只走调用方注入的统一保存入口；
/// · <paramref name="readOnly"/>（未知高版本格式的只读画布）下所有结构按钮禁用并显示可见提示。
/// </summary>
public sealed class ChapterStructureDialog : Form
{
    private readonly Action<WorkflowCanvasState>? apply;
    private readonly bool readOnly;
    private readonly ListView chapterList = new();
    private readonly TextBox nameBox = new();
    private readonly NumericUpDown orderBox = new();
    private readonly ComboBox targetBox = new();
    private readonly CheckedListBox nodeBox = new();
    private readonly List<Guid> nodeIds = new();
    private readonly TextBox resultBox = new();
    private readonly Label statusLabel = new();
    private readonly List<Button> structureButtons = new();
    private bool refreshing;

    public ChapterStructureDialog(
        WorkflowCanvasState state,
        bool readOnly,
        Action<WorkflowCanvasState>? apply = null,
        Func<WorkflowCanvasState, bool>? saveHandler = null)
    {
        Session = new CanvasChapterStructureSession(state, saveHandler);
        this.readOnly = readOnly;
        this.apply = apply;

        Text = readOnly ? "章节结构 — 只读画布" : "章节结构";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 720);
        Font = Theme.UiFont;
        BackColor = Theme.PanelBg;

        chapterList.Dock = DockStyle.Fill;
        chapterList.View = View.Details;
        chapterList.FullRowSelect = true;
        chapterList.MultiSelect = false;
        chapterList.GridLines = false;
        chapterList.BackColor = Theme.PanelBg;
        chapterList.ForeColor = Theme.Text;
        chapterList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        chapterList.Columns.Add("顺序", 70);
        chapterList.Columns.Add("章节", 240);
        chapterList.Columns.Add("父章节", 170);
        chapterList.Columns.Add("节点", 60);
        chapterList.SelectedIndexChanged += (_, _) => LoadSelection();

        var inputs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        inputs.Controls.Add(new Label { Text = "名称", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(6, 8, 0, 0) });
        nameBox.Width = 190; nameBox.Margin = new Padding(4, 5, 10, 0);
        inputs.Controls.Add(nameBox);
        inputs.Controls.Add(new Label { Text = "顺序", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(6, 8, 0, 0) });
        orderBox.Minimum = 1; orderBox.Maximum = 100000; orderBox.Value = CanvasChapters.OrderStep; orderBox.Width = 80; orderBox.Margin = new Padding(4, 5, 10, 0);
        inputs.Controls.Add(orderBox);
        inputs.Controls.Add(new Label { Text = "目标章节", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(6, 8, 0, 0) });
        targetBox.DropDownStyle = ComboBoxStyle.DropDownList; targetBox.Width = 220; targetBox.Margin = new Padding(4, 5, 6, 0);
        inputs.Controls.Add(targetBox);

        nodeBox.Dock = DockStyle.Top;
        nodeBox.Height = 96;
        nodeBox.CheckOnClick = true;
        nodeBox.BackColor = Theme.PanelBg;
        nodeBox.ForeColor = Theme.Text;

        var structureRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 76, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(6) };
        AddButton(structureRow, "新建章节", (_, _) => Run(() => Session.Create(nameBox.Text, SelectedTargetId())), mutating: true);
        AddButton(structureRow, "改名", (_, _) => Run(() => SelectedId() is { } id ? Session.Rename(id, nameBox.Text) : Blocked("请先选择一个章节。")), mutating: true);
        AddButton(structureRow, "上移", (_, _) => Run(() => ShiftOrder(-1)), mutating: true);
        AddButton(structureRow, "下移", (_, _) => Run(() => ShiftOrder(1)), mutating: true);
        AddButton(structureRow, "移动到", (_, _) => Run(() => SelectedId() is { } id ? Session.Move(id, SelectedTargetId()) : Blocked("请先选择一个章节。")), mutating: true);
        AddButton(structureRow, "按顺序排序", (_, _) => Run(() => SelectedId() is { } id ? Session.Reorder(id, (int)orderBox.Value) : Blocked("请先选择一个章节。")), mutating: true);
        AddButton(structureRow, "拆分", (_, _) => Run(() => Split()), mutating: true);
        AddButton(structureRow, "合并到目标", (_, _) => Run(() => Merge()), mutating: true);
        AddButton(structureRow, "删除", (_, _) => Run(() => SelectedId() is { } id ? Session.Delete(id, reassignNodesToParent: false) : Blocked("请先选择一个章节。")), mutating: true);
        AddButton(structureRow, "删除并改挂上级", (_, _) => Run(() => SelectedId() is { } id ? Session.Delete(id, reassignNodesToParent: true) : Blocked("请先选择一个章节。")), mutating: true);
        AddButton(structureRow, "顺序归一化", (_, _) => Run(() => Session.NormalizeOrder()), mutating: true);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        var undo = new Button { Text = "撤销上次操作", Width = 120, Height = 30 };
        undo.Click += (_, _) =>
        {
            if (!Session.Undo()) { statusLabel.Text = "没有可撤销的操作。"; return; }
            // 撤销后的状态必须回写界面画布：这里强制回写，不依赖「上一次操作是否 Changed」。
            Publish("已撤销最近一次章节结构操作（磁盘文件未变，保存后才会写入）。", forceApply: true);
        };
        actionRow.Controls.Add(undo);

        var saveButton = new Button { Text = "保存画布", Width = 100, Height = 30 };
        saveButton.Click += (_, _) =>
        {
            if (readOnly)
            {
                statusLabel.ForeColor = Theme.Warning;
                statusLabel.Text = "只读画布：已禁用保存，不会覆盖原文件。";
                return;
            }

            // 保存结果取自统一保存入口的真实返回值，失败时不得显示成功（返工 B-UI-02）。
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
                resultBox.Text = "保存未成功（只读拒绝、备份失败、权限或写入失败等）。画布未丢失，可继续编辑或重试。"
                    + Environment.NewLine + "（磁盘文件未改动，撤销仍只影响内存）";
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
        resultBox.Height = 190;
        resultBox.Multiline = true;
        resultBox.ReadOnly = true;
        resultBox.ScrollBars = ScrollBars.Both;
        resultBox.WordWrap = false;
        resultBox.BackColor = Color.White;
        resultBox.Font = new Font("Consolas", 9f);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        host.Controls.Add(chapterList);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        layout.Controls.Add(inputs, 0, 0);
        layout.Controls.Add(host, 0, 1);
        layout.Controls.Add(nodeBox, 0, 2);
        layout.Controls.Add(structureRow, 0, 3);
        layout.Controls.Add(resultBox, 0, 4);
        Controls.Add(layout);
        Controls.Add(actionRow);

        if (readOnly)
        {
            foreach (var button in structureButtons) button.Enabled = false;
            undo.Enabled = false;
            saveButton.Enabled = false;
            statusLabel.ForeColor = Theme.Warning;
            statusLabel.Text = "只读画布（来源格式版本高于当前支持版本）：结构操作与保存已禁用。";
            resultBox.Text = "只读画布：可以查看章节结构，但不能修改，也不会覆盖原文件。";
        }

        RefreshChapterList();
    }

    /// <summary>当前会话（供入口级回归与冒烟按真实按钮路径驱动）。</summary>
    public CanvasChapterStructureSession Session { get; }

    public WorkflowCanvasState CurrentState => Session.State;

    /// <summary>最近一次操作的展示文本（改动与冲突）。</summary>
    public string LastOperationSummary => Session.LastSummary;

    private void AddButton(Control parent, string text, EventHandler onClick, bool mutating)
    {
        var button = new Button { Text = text, Width = 92, Height = 30 };
        button.Click += onClick;
        parent.Controls.Add(button);
        if (mutating) structureButtons.Add(button);
    }

    private Guid? SelectedId() =>
        chapterList.SelectedItems.Count > 0 && chapterList.SelectedItems[0].Tag is ChapterInfo info ? info.Id : null;

    private Guid? SelectedTargetId() =>
        targetBox.SelectedItem is ChapterChoice choice ? choice.Id : null;

    private void LoadSelection()
    {
        if (refreshing) return;
        if (SelectedId() is not { } id) return;
        var chapter = CanvasChapters.List(Session.State).First(info => info.Id == id);
        nameBox.Text = chapter.Name;
        orderBox.Value = Math.Clamp(chapter.Order == 0 ? CanvasChapters.OrderStep : chapter.Order, (int)orderBox.Minimum, (int)orderBox.Maximum);
        RefreshNodeList(id);
    }

    private void RefreshNodeList(Guid chapterId)
    {
        nodeBox.BeginUpdate();
        nodeBox.Items.Clear();
        nodeIds.Clear();
        foreach (var node in Session.State.Nodes.Where(node => CanvasChapters.ResolveChapterId(Session.State, node) == chapterId))
        {
            nodeBox.Items.Add($"{node.Title}（锚点 {node.WorkTreeItemId?.ToString() ?? "无"}）", false);
            nodeIds.Add(node.Id);
        }

        nodeBox.EndUpdate();
        if (nodeIds.Count == 0) nodeBox.Items.Add("（该章节还没有节点，可先勾选其它章节的节点前先确认归属）", false);
    }

    /// <summary>勾选项对应的节点 ID，与 <see cref="nodeBox"/> 的项一一对应。</summary>
    private IReadOnlyList<Guid> CheckedNodeIds()
    {
        var result = new List<Guid>();
        for (var index = 0; index < nodeBox.Items.Count && index < nodeIds.Count; index++)
        {
            if (nodeBox.GetItemChecked(index)) result.Add(nodeIds[index]);
        }

        return result;
    }

    private ChapterOperationResult Split()
    {
        if (SelectedId() is not { } id) return Blocked("请先选择一个章节。");
        var ids = CheckedNodeIds();
        if (ids.Count == 0) return Blocked("请先在上方勾选要拆到新章节的节点。");
        return Session.Split(id, nameBox.Text, ids);
    }

    private ChapterOperationResult Merge()
    {
        if (SelectedId() is not { } sourceId) return Blocked("请先选择要合并掉的章节。");
        if (SelectedTargetId() is not { } targetId) return Blocked("请选择合并的目标章节。");
        return Session.Merge(sourceId, targetId);
    }

    private ChapterOperationResult ShiftOrder(int direction)
    {
        if (SelectedId() is not { } id) return Blocked("请先选择一个章节。");
        var chapters = CanvasChapters.List(Session.State);
        var index = chapters.ToList().FindIndex(info => info.Id == id);
        var sibling = chapters.Where(info => info.ParentChapterId == chapters[index].ParentChapterId).ToList();
        var position = sibling.FindIndex(info => info.Id == id);
        var target = position + direction;
        if (target < 0 || target >= sibling.Count) return Blocked("已经是同级里的第一/最后一个章节。");
        return Session.Reorder(id, sibling[target].Order);
    }

    private ChapterOperationResult Blocked(string reason)
    {
        statusLabel.ForeColor = Theme.Warning;
        statusLabel.Text = reason;
        resultBox.Text = reason + Environment.NewLine + "（画布未改动）";
        return Session.LastResult ?? new ChapterOperationResult(Session.State, Array.Empty<ChapterChange>(), Array.Empty<ChapterConflict>());
    }

    private void Run(Func<ChapterOperationResult> operation)
    {
        if (readOnly)
        {
            statusLabel.Text = "只读画布：结构操作已禁用。";
            return;
        }

        var result = operation();
        if (result.HasBlockingConflicts)
        {
            statusLabel.ForeColor = Theme.Warning;
            statusLabel.Text = $"操作被阻断（{result.Conflicts.Count(conflict => conflict.Blocking)} 项阻断冲突），画布未改动。";
            resultBox.Text = Session.LastSummary;
            RefreshChapterList(keepSelection: SelectedId(), refreshNodes: false);
            return;
        }

        Publish(result.Changed ? "操作已应用（尚未保存）。" : "操作没有产生改动。");
    }

    private void Publish(string status, bool forceApply = false)
    {
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = status;
        resultBox.Text = Session.LastSummary;
        if (forceApply || Session.LastResult?.Changed == true) apply?.Invoke(Session.State);
        RefreshChapterList(keepSelection: SelectedId(), refreshNodes: true);
    }

    private void RefreshChapterList(Guid? keepSelection = null, bool refreshNodes = true)
    {
        refreshing = true;
        chapterList.BeginUpdate();
        chapterList.Items.Clear();
        var selectedRow = -1;
        foreach (var chapter in CanvasChapters.List(Session.State))
        {
            var row = new ListViewItem(chapter.Order == 0 ? "（未指定）" : chapter.Order.ToString());
            row.SubItems.Add((chapter.ParentChapterId is null ? string.Empty : "    └ ") + chapter.Name);
            row.SubItems.Add(chapter.ParentChapterId is { } parentId
                ? Session.State.WorkTree.FirstOrDefault(item => item.Id == parentId)?.Name ?? "（缺失）"
                : "（顶层）");
            row.SubItems.Add((chapter.NodeCount + chapter.UnanchoredNodeCount).ToString());
            row.Tag = chapter;
            chapterList.Items.Add(row);
            if (keepSelection is { } id && chapter.Id == id) selectedRow = row.Index;
        }

        chapterList.EndUpdate();
        if (selectedRow >= 0) chapterList.Items[selectedRow].Selected = true;

        targetBox.BeginUpdate();
        var previous = SelectedTargetId();
        targetBox.Items.Clear();
        targetBox.Items.Add(new ChapterChoice(null, "（顶层 / 不指定）"));
        foreach (var chapter in CanvasChapters.List(Session.State))
            targetBox.Items.Add(new ChapterChoice(chapter.Id, chapter.Order == 0 ? chapter.Name : $"#{chapter.Order} {chapter.Name}"));
        targetBox.SelectedIndex = 0;
        if (previous is { } keep)
        {
            for (var index = 0; index < targetBox.Items.Count; index++)
            {
                if (targetBox.Items[index] is ChapterChoice choice && choice.Id == keep) { targetBox.SelectedIndex = index; break; }
            }
        }

        targetBox.EndUpdate();
        refreshing = false;

        if (chapterList.SelectedItems.Count > 0 && chapterList.SelectedItems[0].Tag is ChapterInfo current)
        {
            nameBox.Text = current.Name;
            orderBox.Value = Math.Clamp(current.Order == 0 ? CanvasChapters.OrderStep : current.Order, (int)orderBox.Minimum, (int)orderBox.Maximum);
            if (refreshNodes) RefreshNodeList(current.Id);
        }
    }

    /// <summary>目标章节下拉项：Id 为空表示顶层。</summary>
    private sealed record ChapterChoice(Guid? Id, string Text)
    {
        public override string ToString() => Text;
    }
}
