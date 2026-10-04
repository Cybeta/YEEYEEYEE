using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>「同步工作树」该怎么动手。</summary>
internal enum SyncChoice
{
    Cancel,

    /// <summary>只应用引擎认为可以直接应用的项。</summary>
    AutomaticOnly,

    /// <summary>连同「待确认」的一起应用（那些会覆盖已有内容）。</summary>
    IncludingPending
}

/// <summary>
/// 「同步工作树与画布」的那个预览与确认。
///
/// 引擎（<c>CanvasWorkTreeSync</c>）与网页端读的是同一份计划，但它此前**没有任何入口**：
/// 唯一的生产调用是导入摘要里数冲突条数。这个对话框是它的入口——先把「要改哪几项」摆出来，
/// 再由用户决定动手不动手；计划是只读预览，不动手就什么都没变。
/// </summary>
internal static class CanvasSyncDialog
{
    private const int DialogWidth = 620;
    private const int DialogHeight = 520;
    private const int NoticeWidth = 560;
    private const int NoticeHeight = 320;

    public static async Task<SyncChoice> ChooseAsync(Window owner, SyncPlan plan)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16, 20, 4) };
        body.Children.Add(AgentDialogUi.Header(
            $"画布与工作树有 {plan.Changes.Count} 项不一致"
            + $"（可直接应用 {plan.Automatic.Count}、待确认 {plan.PendingConfirmation.Count}）"));
        body.Children.Add(AgentDialogUi.Note(
            "同步是**双向补齐**：画布上改过的名字与章节顺序会写回工作树，工作树上的改动也会落到节点上。"
            + "它改的是结构，落盘要等你点「保存修订」——所以这之后还能撤销。"));
        if (plan.PendingConfirmation.Count > 0)
            body.Children.Add(AgentDialogUi.Note(
                $"「待确认」那 {plan.PendingConfirmation.Count} 项会覆盖已有内容，引擎自己不替你决定："
                + "要一起改就按第二个按钮。", AgentNoteLevel.Warning));
        // 引擎自己那句话原样摆出来：改了什么、拿什么换什么，它写得比这里再编一遍更准。
        body.Children.Add(AgentDialogUi.Note(plan.ToText()));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = AgentDialogUi.Secondary("取消");
        buttons.Children.Add(cancel);

        Button? automatic = null;
        if (plan.Automatic.Count > 0 && plan.PendingConfirmation.Count > 0)
        {
            automatic = AgentDialogUi.Secondary($"只应用可直接应用的 {plan.Automatic.Count} 项");
            buttons.Children.Add(automatic);
        }

        var all = AgentDialogUi.Primary(plan.PendingConfirmation.Count > 0
            ? $"连同待确认的一起改（{plan.Changes.Count} 项）"
            : $"应用这 {plan.Changes.Count} 项");
        buttons.Children.Add(all);

        var window = DialogShell.Create(
            "同步工作树", AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)), DialogWidth, DialogHeight);

        var choice = SyncChoice.Cancel;
        cancel.Click += (_, _) => window.Close();
        if (automatic is not null) automatic.Click += (_, _) => { choice = SyncChoice.AutomaticOnly; window.Close(); };
        all.Click += (_, _) => { choice = SyncChoice.IncludingPending; window.Close(); };

        await window.ShowDialog(owner);
        return choice;
    }

    /// <summary>只说一件事的小窗（被阻断 / 本来就没有可同步项 / 撤销结果）。</summary>
    public static async Task NoticeAsync(Window owner, string title, string message, bool warning)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16, 20, 4) };
        body.Children.Add(AgentDialogUi.Header(title));
        body.Children.Add(AgentDialogUi.Note(message, warning ? AgentNoteLevel.Warning : AgentNoteLevel.Info));

        var ok = AgentDialogUi.Primary("知道了");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(ok);

        var window = DialogShell.Create(
            title, AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)), NoticeWidth, NoticeHeight);
        ok.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
    }
}
