using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 「整理布局」碰上人工摆放的节点时的那一句确认。
///
/// 与网页端工具栏下那条「连手动摆放的 N 个一起动」是**同一个确认**：引擎默认绕开手动摆放的节点，
/// 要动它们必须显式点头——引擎自己也会在没点头时拒绝，这里只是把那个拒绝变成一个能回答的问题。
/// </summary>
internal static class CanvasLayoutDialog
{
    private const int DialogWidth = 480;
    private const int DialogHeight = 340;

    public static async Task<bool> ConfirmOverrideManualAsync(Window owner, int protectedCount, string summary)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16, 20, 4) };
        body.Children.Add(AgentDialogUi.Header($"有 {protectedCount} 个手动摆放的节点会被移动"));
        body.Children.Add(AgentDialogUi.Note(
            "手动摆放的节点默认留在原位。这次整理会把它们一起排进泳道，"
            + "整理之后它们身上的「手动摆放」标记会被清掉——下一次整理就会照常动它们。"));
        body.Children.Add(AgentDialogUi.Note(summary));

        var cancel = AgentDialogUi.Secondary("取消");
        var confirm = AgentDialogUi.Primary("连手动摆放的一起动");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var window = DialogShell.Create(
            "整理布局", AgentDialogUi.Layout(body, AgentDialogUi.Footer(buttons)), DialogWidth, DialogHeight);
        var confirmed = false;
        cancel.Click += (_, _) => window.Close();
        confirm.Click += (_, _) => { confirmed = true; window.Close(); };

        await window.ShowDialog(owner);
        return confirmed;
    }
}
