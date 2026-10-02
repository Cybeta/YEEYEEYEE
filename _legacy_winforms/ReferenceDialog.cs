namespace DreamForge.Desktop;

/// <summary>
/// 引用与回收站（目标 4）：把「谁引用了这个设定」跨画布/草稿列清楚，
/// 提供版本锁定改写、删除保护（解除引用或回收站）与还原。
///
/// 只读画布下所有写入动作（版本改写、解除引用、删除、还原）一律禁用并给出可见提示。
/// </summary>
public sealed class ReferenceDialog : ScaledForm
{
    /// <summary>只读画布的固定提示：不能被后续扫描结果覆盖，必须始终可见。</summary>
    private const string ReadOnlyNotice = "只读画布（来源格式版本高于当前支持版本）：引用可以查看，但版本改写、解除引用、删除与还原已禁用。";

    private readonly WorkflowCanvasState state;
    private readonly string? canvasPath;
    private readonly bool readOnly;
    private readonly Action<WorkflowCanvasState>? apply;
    private readonly Func<Guid, bool>? focusNode;

    /// <summary>危险操作前的确认；默认弹窗，可由调用方（回归/冒烟）注入以便自动确认。</summary>
    private readonly Func<string, bool> confirm;

    private readonly CheckBox includeLibrary = new();
    private readonly CheckBox includeDraft = new();
    private readonly Label summaryLabel = new();
    private readonly ListView variantList = new();
    private readonly ListView hitList = new();
    private readonly ComboBox versionBox = new();
    private readonly ListView recycleList = new();
    private readonly TextBox resultBox = new();
    private readonly Label statusLabel = new();
    private readonly List<Button> mutatingButtons = new();

    private ReferenceScanReport report = ReferenceScanReport.Empty;

    public ReferenceDialog(
        WorkflowCanvasState state,
        string? canvasPath,
        bool readOnly,
        Action<WorkflowCanvasState>? apply = null,
        Func<Guid, bool>? focusNode = null,
        Func<string, bool>? confirm = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.canvasPath = canvasPath;
        this.readOnly = readOnly;
        this.apply = apply;
        this.focusNode = focusNode;
        this.confirm = confirm ?? (message =>
            MessageBox.Show(message, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);

        Text = readOnly ? "引用与回收站 — 只读画布" : "引用与回收站";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1120, 740);
        Font = Theme.UiFont;
        BackColor = Theme.PanelBg;

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // ---- 引用与版本 ----
        var referencePage = new TabPage("引用与版本") { BackColor = Theme.PanelBg };
        var scanRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        AddButton(scanRow, "扫描引用", 92, (_, _) => Rescan(), mutating: false);
        includeLibrary.Text = "含画布库其它画布";
        includeLibrary.ForeColor = Theme.TextMuted;
        includeLibrary.AutoSize = true;
        includeLibrary.Checked = true;
        includeLibrary.Margin = new Padding(10, 9, 0, 0);
        scanRow.Controls.Add(includeLibrary);
        includeDraft.Text = "含草稿画布";
        includeDraft.ForeColor = Theme.TextMuted;
        includeDraft.AutoSize = true;
        includeDraft.Checked = true;
        includeDraft.Margin = new Padding(10, 9, 0, 0);
        scanRow.Controls.Add(includeDraft);
        summaryLabel.AutoSize = true;
        summaryLabel.ForeColor = Theme.TextMuted;
        summaryLabel.Margin = new Padding(14, 9, 0, 0);
        scanRow.Controls.Add(summaryLabel);

        variantList.Dock = DockStyle.Fill;
        variantList.View = View.Details;
        variantList.FullRowSelect = true;
        variantList.MultiSelect = false;
        variantList.BackColor = Theme.PanelBg;
        variantList.ForeColor = Theme.Text;
        variantList.Columns.Add("设定 · 变体", 300);
        variantList.Columns.Add("本画布", 70);
        variantList.Columns.Add("其它来源", 80);
        variantList.Columns.Add("版本", 140);
        variantList.SelectedIndexChanged += (_, _) => LoadSelection();

        hitList.Dock = DockStyle.Fill;
        hitList.View = View.Details;
        hitList.FullRowSelect = true;
        hitList.MultiSelect = false;
        hitList.BackColor = Theme.PanelBg;
        hitList.ForeColor = Theme.Text;
        hitList.Columns.Add("范围", 90);
        hitList.Columns.Add("来源", 200);
        hitList.Columns.Add("节点", 260);
        hitList.Columns.Add("版本", 120);

        var versionRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        versionRow.Controls.Add(new Label { Text = "版本", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(6, 9, 0, 0) });
        versionBox.DropDownStyle = ComboBoxStyle.DropDownList;
        versionBox.Width = 240;
        versionBox.Margin = new Padding(4, 6, 10, 0);
        versionRow.Controls.Add(versionBox);
        AddButton(versionRow, "应用版本", 92, (_, _) => ApplyVersion(), mutating: true);
        AddButton(versionRow, "定位引用节点", 110, (_, _) => LocateHit(), mutating: false);

        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        AddButton(actionRow, "解除本画布引用", 124, (_, _) => RemoveLocalReferences(), mutating: true);
        AddButton(actionRow, "删除变体", 92, (_, _) => DeleteVariant(), mutating: true);
        AddButton(actionRow, "删除实体", 92, (_, _) => DeleteEntity(), mutating: true);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240 };
        split.Panel1.Controls.Add(variantList);
        split.Panel2.Controls.Add(hitList);

        var referenceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        referenceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        referenceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        referenceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        referenceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        referenceLayout.Controls.Add(scanRow, 0, 0);
        referenceLayout.Controls.Add(split, 0, 1);
        referenceLayout.Controls.Add(versionRow, 0, 2);
        referenceLayout.Controls.Add(actionRow, 0, 3);
        referencePage.Controls.Add(referenceLayout);

        // ---- 回收站 ----
        var recyclePage = new TabPage("回收站") { BackColor = Theme.PanelBg };
        recycleList.Dock = DockStyle.Fill;
        recycleList.View = View.Details;
        recycleList.FullRowSelect = true;
        recycleList.MultiSelect = false;
        recycleList.BackColor = Theme.PanelBg;
        recycleList.ForeColor = Theme.Text;
        recycleList.Columns.Add("名称", 260);
        recycleList.Columns.Add("类型", 80);
        recycleList.Columns.Add("删除时间", 160);
        recycleList.Columns.Add("删除时被引用于", 400);

        var recycleRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        AddButton(recycleRow, "还原", 80, (_, _) => RestoreSelected(), mutating: true);
        AddButton(recycleRow, "彻底删除", 92, (_, _) => PurgeSelected(), mutating: true);
        AddButton(recycleRow, "清空回收站", 100, (_, _) => ClearRecycleBin(), mutating: true);
        recyclePage.Controls.Add(recycleList);
        recyclePage.Controls.Add(recycleRow);

        tabs.TabPages.Add(referencePage);
        tabs.TabPages.Add(recyclePage);

        statusLabel.AutoSize = true;
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Dock = DockStyle.Bottom;
        statusLabel.Padding = new Padding(10, 6, 10, 0);

        resultBox.Dock = DockStyle.Bottom;
        resultBox.Height = 130;
        resultBox.Multiline = true;
        resultBox.ReadOnly = true;
        resultBox.ScrollBars = ScrollBars.Both;
        resultBox.WordWrap = false;
        resultBox.BackColor = Color.White;
        resultBox.Font = new Font("Consolas", 9f);

        var close = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var closeButton = new Button { Text = "关闭", Width = 80, Height = 30 };
        closeButton.Click += (_, _) => Close();
        close.Controls.Add(closeButton);

        Controls.Add(tabs);
        Controls.Add(resultBox);
        Controls.Add(statusLabel);
        Controls.Add(close);

        if (readOnly)
        {
            foreach (var button in mutatingButtons) button.Enabled = false;
            versionBox.Enabled = false;
            statusLabel.ForeColor = Theme.Warning;
            statusLabel.Text = ReadOnlyNotice;
        }

        Rescan();
        RefreshRecycleBin();
    }

    /// <summary>最近一次扫描报告（供入口级回归与冒烟检查）。</summary>
    public ReferenceScanReport Report => report;

    public string LastSummary => statusLabel.Text;

    private void AddButton(Control parent, string text, int width, EventHandler onClick, bool mutating)
    {
        var button = new Button { Text = text, Width = width, Height = 30 };
        button.Click += onClick;
        parent.Controls.Add(button);
        if (mutating) mutatingButtons.Add(button);
    }

    private void Rescan()
    {
        report = CanvasReferenceScanner.Scan(state, canvasPath, includeLibrary.Checked, includeDraft.Checked);
        var skipped = report.Skipped.Count == 0 ? string.Empty : $"，{report.Skipped.Count} 个来源打不开";
        summaryLabel.Text = $"已扫描 {report.ScannedCanvases} 个来源，命中 {report.Hits.Count} 条引用{skipped}";
        RefreshVariantList();
        RefreshHits();
        if (report.Skipped.Count > 0) resultBox.Text = string.Join(Environment.NewLine, report.Skipped);
    }

    private void RefreshVariantList()
    {
        variantList.BeginUpdate();
        variantList.Items.Clear();
        foreach (var entity in state.Entities)
        foreach (var variant in entity.Variants)
        {
            var hits = CanvasReferenceScanner.ForVariant(report, entity.Id, variant.Id);
            var item = new ListViewItem($"{WorkflowEntity.KindName(entity.Kind)}「{entity.Name}」· {variant.Name}");
            item.SubItems.Add(hits.Count(hit => !hit.IsForeign).ToString());
            item.SubItems.Add(hits.Count(hit => hit.IsForeign).ToString());
            item.SubItems.Add(DescribeVersionState(hits));
            item.Tag = new VariantChoice(entity, variant);
            variantList.Items.Add(item);
        }

        variantList.EndUpdate();
        if (variantList.Items.Count > 0 && variantList.SelectedItems.Count == 0) variantList.Items[0].Selected = true;
    }

    private static string DescribeVersionState(IReadOnlyList<ReferenceHit> hits)
    {
        if (hits.Count == 0) return "无引用";
        if (hits.Any(hit => hit.LockedVersionMissing)) return "版本缺失（阻断）";
        return hits.All(hit => hit.VariantVersionId is null) ? "跟随最新" : "锁定版本";
    }

    private VariantChoice? Selected() =>
        variantList.SelectedItems.Count > 0 && variantList.SelectedItems[0].Tag is VariantChoice choice ? choice : null;

    private void LoadSelection()
    {
        if (Selected() is not { } selection) return;
        var (entity, variant) = (selection.Entity, selection.Variant);

        versionBox.BeginUpdate();
        versionBox.Items.Clear();
        versionBox.Items.Add(new VersionChoice(null, "跟随最新内容"));
        foreach (var version in variant.Versions.OrderBy(version => version.Number))
            versionBox.Items.Add(new VersionChoice(version.Id, version.Label + (string.IsNullOrWhiteSpace(version.Note) ? string.Empty : $" · {version.Note}")));
        versionBox.EndUpdate();
        versionBox.SelectedIndex = 0;

        RefreshHits();
    }

    private void RefreshHits()
    {
        if (Selected() is not { } selection) { hitList.Items.Clear(); return; }
        var (entity, variant) = (selection.Entity, selection.Variant);
        var hits = CanvasReferenceScanner.ForVariant(report, entity.Id, variant.Id);

        hitList.BeginUpdate();
        hitList.Items.Clear();
        foreach (var hit in hits)
        {
            var item = new ListViewItem(ScopeLabel(hit.Scope));
            item.SubItems.Add(hit.SourceLabel);
            item.SubItems.Add(hit.NodeTitle);
            item.SubItems.Add(hit.VersionLabel);
            item.Tag = hit;
            hitList.Items.Add(item);
        }

        hitList.EndUpdate();
        // 默认选中第一条，让「定位引用节点」等动作有明确对象。
        if (hitList.Items.Count > 0) hitList.Items[0].Selected = true;

        var blocked = hitList.Items.Cast<ListViewItem>().Count(item => item.Tag is ReferenceHit { LockedVersionMissing: true });
        var summary = hits.Count == 0
            ? $"「{entity.Name} · {variant.Name}」没有被任何画布引用。"
            : $"「{entity.Name} · {variant.Name}」共有 {hits.Count} 条引用（本画布 {hits.Count(hit => !hit.IsForeign)}、其它来源 {hits.Count(hit => hit.IsForeign)}）"
              + (blocked > 0 ? $"；其中 {blocked} 条锁定版本缺失，属于阻断状态。" : "。");

        // 只读提示不能被扫描结果覆盖：只读时状态行固定显示原因，摘要放到结果区。
        statusLabel.ForeColor = readOnly || blocked > 0 ? Theme.Warning : Theme.TextMuted;
        statusLabel.Text = readOnly ? ReadOnlyNotice : summary;
        if (readOnly) resultBox.Text = summary;
    }

    private static string ScopeLabel(ReferenceScopeKind scope) => scope switch
    {
        ReferenceScopeKind.Canvas => "当前画布",
        ReferenceScopeKind.Draft => "草稿",
        _ => "画布库"
    };

    private void LocateHit()
    {
        if (hitList.SelectedItems.Count == 0 || hitList.SelectedItems[0].Tag is not ReferenceHit hit) { Warn("请先选择一条引用。"); return; }
        if (hit.IsForeign) { Warn($"这条引用在「{hit.SourceLabel}」里，请先打开那个画布再定位。"); return; }
        if (focusNode?.Invoke(hit.NodeId) == true) { statusLabel.ForeColor = Theme.TextMuted; statusLabel.Text = $"已定位到节点「{hit.NodeTitle}」。"; return; }
        Warn("在当前画布中找不到这个节点（可能已被删除）。");
    }

    private void ApplyVersion()
    {
        if (readOnly) { Warn("只读画布：版本改写已禁用。"); return; }
        if (Selected() is not { } selection || versionBox.SelectedItem is not VersionChoice choice) { Warn("请先选择一条设定引用。"); return; }
        var (entity, variant) = (selection.Entity, selection.Variant);

        var applied = 0;
        var errors = new List<string>();
        foreach (var node in state.Nodes)
        {
            // 只改真正引用这条设定的节点；其余节点跳过。
            if (!node.References.Any(reference => reference.EntityId == entity.Id && reference.VariantId == variant.Id)) continue;
            if (!CanvasReferenceVersions.TrySetVersion(state, node, entity.Id, variant.Id, choice.Id, out var error))
            {
                errors.Add($"{node.Title}：{error}");
                continue;
            }

            applied++;
        }

        if (applied == 0 && errors.Count == 0) { Warn("本画布没有节点引用这条设定，没有可改写的引用。"); return; }
        if (errors.Count > 0) { Warn("部分引用未改写：" + Environment.NewLine + string.Join(Environment.NewLine, errors)); return; }

        apply?.Invoke(state);
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = $"已把 {applied} 条引用改为「{choice.Text}」（磁盘未变，保存后写入）。";
        Rescan();
    }

    private void RemoveLocalReferences()
    {
        if (readOnly) { Warn("只读画布：解除引用已禁用。"); return; }
        if (Selected() is not { } selection) { Warn("请先选择一条设定。"); return; }
        var (entity, variant) = (selection.Entity, selection.Variant);
        var removed = CanvasDeletionGuard.RemoveReferencesIn(state, entity.Id, variant.Id);
        if (removed == 0) { Warn("本画布没有引用这条设定。"); return; }
        apply?.Invoke(state);
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = $"已在本画布解除 {removed} 条对「{entity.Name} · {variant.Name}」的引用（磁盘未变，保存后写入）。";
        Rescan();
    }

    private void DeleteVariant()
    {
        if (readOnly) { Warn("只读画布：删除已禁用。"); return; }
        if (Selected() is not { } selection) { Warn("请先选择一条设定。"); return; }
        var (entity, variant) = (selection.Entity, selection.Variant);
        if (entity.Variants.Count <= 1) { Warn("每个实体至少保留一个变体。"); return; }

        var guard = CanvasDeletionGuard.CheckVariant(report, entity, variant);
        if (guard.HardBlocked) { Warn(guard.Message); return; }
        var prompt = guard.HasReferences
            ? guard.Message + "\n\n是否解除本画布引用并把这个变体移入回收站？"
            : $"把变体「{variant.Name}」移入回收站？";
        if (!confirm(prompt)) return;

        // 先写回收站快照，成功后才解除引用并移除：写不进去就整体取消，内存与磁盘都保持原样。
        if (!CanvasDeletionGuard.TryDeleteVariant(state, entity, variant, guard.Hits, out var error))
        {
            Warn($"未能删除变体「{variant.Name}」：{error}（画布未改动）");
            return;
        }

        apply?.Invoke(state);
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = $"变体「{variant.Name}」已移入回收站（可还原），并解除本画布 {guard.Hits.Count} 条引用。";
        Rescan();
        RefreshRecycleBin();
    }

    private void DeleteEntity()
    {
        if (readOnly) { Warn("只读画布：删除已禁用。"); return; }
        if (Selected() is not { } selection) { Warn("请先选择一条设定。"); return; }
        var entity = selection.Entity;

        var guard = CanvasDeletionGuard.CheckEntity(report, entity);
        if (guard.HardBlocked) { Warn(guard.Message); return; }
        var prompt = guard.HasReferences
            ? guard.Message + $"\n\n是否解除本画布引用并把「{entity.Name}」及其 {entity.Variants.Count} 个变体移入回收站？"
            : $"把「{entity.Name}」及其 {entity.Variants.Count} 个变体移入回收站？";
        if (!confirm(prompt)) return;

        // 先写回收站快照，成功后才解除引用并移除：写不进去就整体取消，内存与磁盘都保持原样。
        if (!CanvasDeletionGuard.TryDeleteEntity(state, entity, guard.Hits, out var error))
        {
            Warn($"未能删除「{entity.Name}」：{error}（画布未改动）");
            return;
        }

        apply?.Invoke(state);
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = $"实体「{entity.Name}」已移入回收站（可还原），并解除本画布 {guard.Hits.Count} 条引用。";
        Rescan();
        RefreshRecycleBin();
    }

    private void RefreshRecycleBin()
    {
        var entries = CanvasRecycleBin.List();
        recycleList.BeginUpdate();
        recycleList.Items.Clear();
        foreach (var entry in entries)
        {
            var item = new ListViewItem(entry.DisplayName);
            item.SubItems.Add(entry.Kind == RecycleEntryKind.Entity ? "实体" : "变体");
            item.SubItems.Add(entry.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add(entry.ReferencedBy.Count == 0 ? "（当时无引用）" : string.Join("、", entry.ReferencedBy));
            item.Tag = entry;
            recycleList.Items.Add(item);
        }

        recycleList.EndUpdate();
    }

    private RecycleEntry? SelectedEntry() =>
        recycleList.SelectedItems.Count > 0 && recycleList.SelectedItems[0].Tag is RecycleEntry entry ? entry : null;

    private void RestoreSelected()
    {
        if (readOnly) { Warn("只读画布：还原已禁用。"); return; }
        if (SelectedEntry() is not { } entry) { Warn("请先选择一条回收站记录。"); return; }
        if (!CanvasRecycleBin.Restore(entry.Id, state, out var message)) { Warn(message); return; }
        apply?.Invoke(state);
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = message;
        Rescan();
        RefreshRecycleBin();
    }

    private void PurgeSelected()
    {
        if (readOnly) { Warn("只读画布：彻底删除已禁用。"); return; }
        if (SelectedEntry() is not { } entry) { Warn("请先选择一条回收站记录。"); return; }
        if (!confirm($"彻底删除「{entry.DisplayName}」？此操作不可再还原（不会改动任何画布）。")) return;
        if (!CanvasRecycleBin.TryPurge(entry.Id, out var purgeError)) { Warn($"未能彻底删除：{purgeError}"); return; }
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = $"已从回收站彻底删除「{entry.DisplayName}」。";
        RefreshRecycleBin();
    }

    private void ClearRecycleBin()
    {
        if (readOnly) { Warn("只读画布：清空回收站已禁用。"); return; }
        if (CanvasRecycleBin.Count == 0) { Warn("回收站已经是空的。"); return; }
        if (!confirm("清空回收站？所有记录将被彻底删除，不可还原（不会改动任何画布）。")) return;
        if (!CanvasRecycleBin.TryClear(out var clearError)) { Warn($"未能清空回收站：{clearError}"); return; }
        statusLabel.ForeColor = Theme.TextMuted;
        statusLabel.Text = "回收站已清空。";
        RefreshRecycleBin();
    }

    private void Warn(string message)
    {
        statusLabel.ForeColor = Theme.Warning;
        statusLabel.Text = message;
        resultBox.Text = message;
    }

    /// <summary>设定列表项：携带实体与变体本身，界面文本只用于显示。</summary>
    private sealed record VariantChoice(WorkflowEntity Entity, WorkflowEntityVariant Variant);

    /// <summary>版本下拉项：Id 为空表示跟随最新。</summary>
    private sealed record VersionChoice(Guid? Id, string Text)
    {
        public override string ToString() => Text;
    }
}
