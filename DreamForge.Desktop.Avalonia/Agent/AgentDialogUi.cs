using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace DreamForge.Desktop.Avalonia;

/// <summary>
/// Agent 相关对话框共用的界面零件。
///
/// 抽出来的原因很实际：模型设置与接入引导是两扇门，但都长在同一个工作台上，
/// 序号、说明、字段的样式一旦各写一份，两扇门很快就不像同一个应用了。
/// </summary>
internal static class AgentDialogUi
{
    /// <summary>一个带说明的输入行。</summary>
    public static (TextBlock Label, TextBox Box) Field(string label, string value, string? watermark = null) =>
        (new TextBlock { Text = label, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush("DfInk2") },
         new TextBox
         {
             Text = value,
             FontSize = 12,
             Background = Brush("DfSurface2"),
             Foreground = Brush("DfInk"),
             Watermark = watermark
         });

    public static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("DfInk"),
        Margin = new Thickness(0, 10, 0, 0)
    };

    /// <summary>说明文字的语气。用颜色分开，避免「失败」和「提示」长得一样。</summary>
    public static TextBlock Note(string text, AgentNoteLevel level = AgentNoteLevel.Info) => new()
    {
        Text = text,
        FontSize = 10,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(level switch
        {
            AgentNoteLevel.Success => "DfSuccess",
            AgentNoteLevel.Warning => "DfWarning",
            AgentNoteLevel.Error => "DfError",
            _ => "DfInk3"
        })
    };

    public static Border Footer(Control content) => new()
    {
        Padding = new Thickness(20, 12),
        BorderThickness = new Thickness(0, 1, 0, 0),
        BorderBrush = Brush("DfLine"),
        Child = content
    };

    /// <summary>把内容区 + 底部按钮栏组装成对话框内容。</summary>
    public static Grid Layout(Control body, Control footer)
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        // 横向滚动**必须关掉**：开着的话 ScrollViewer 会把里面 TextBox 的「不换行整段文字宽度」
        // 当成自己的期望宽度报上去，窗口会被撑到比指定宽度宽一大截（长句子的对话框尤其明显）。
        // 关掉之后内容按窗口宽度换行，对话框才会老老实实待在 Width 里。
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        root.Children.Add(footer);
        Grid.SetRow(footer, 1);
        return root;
    }

    public static Button Primary(string content)
    {
        var button = new Button { Content = content };
        button.Classes.Add("primary");
        return button;
    }

    public static Button Secondary(string content)
    {
        var button = new Button { Content = content };
        button.Classes.Add("miniButton");
        return button;
    }

    /// <summary>
    /// 问一句文本（例如给站点改个名字）。取消时返回 null——
    /// 与「返回空串」区分开：空串是用户真的想把它清空，null 是他改主意了。
    /// </summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string hint, string initial, bool secret = false)
    {
        var box = new TextBox
        {
            Text = initial,
            FontSize = 12,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };
        // 密钥用密码字符挡一下：不防谁，只是不让它在投屏、截图、旁边有人时明晃晃地躺着。
        if (secret) box.PasswordChar = '●';
        var confirm = Primary("确定");
        var cancel = Secondary("取消");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, confirm }
        };

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var text = Note(hint);
        text.FontSize = 11;
        body.Children.Add(text);
        body.Children.Add(box);

        var dialog = DialogShell.Create(title, Layout(body, Footer(buttons)), 460);
        string? result = null;
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) =>
        {
            result = box.Text ?? string.Empty;
            dialog.Close();
        };
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// 二次确认。用在「不可逆且容易被误解」的选择上（例如选了本地模拟却以为在用大模型）。
    /// </summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText)
    {
        var confirm = Primary(confirmText);
        var cancel = Secondary("取消");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, confirm }
        };

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var text = Note(message);
        text.FontSize = 11;
        body.Children.Add(text);

        var dialog = DialogShell.Create(title, Layout(body, Footer(buttons)), 460);

        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// 三选一确认：用在「有没保存的内容，但确实要走」的地方。
    /// 只给「确定 / 取消」等于逼用户在丢内容与不走之间二选一，而「先保存再走」才是多数人真正想要的。
    /// </summary>
    public static async Task<AgentUnsavedChoice> AskUnsavedAsync(
        Window owner, string title, string message, string saveText, string discardText)
    {
        var save = Primary(saveText);
        var discard = Secondary(discardText);
        var cancel = Secondary("取消");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, discard, save }
        };

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var text = Note(message);
        text.FontSize = 11;
        body.Children.Add(text);

        var dialog = DialogShell.Create(title, Layout(body, Footer(buttons)), 480);

        cancel.Click += (_, _) => dialog.Close(AgentUnsavedChoice.Cancel);
        discard.Click += (_, _) => dialog.Close(AgentUnsavedChoice.Discard);
        save.Click += (_, _) => dialog.Close(AgentUnsavedChoice.SaveFirst);
        return await dialog.ShowDialog<AgentUnsavedChoice>(owner);
    }

    public static IBrush Brush(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;
}

/// <summary>说明文字的语气。</summary>
internal enum AgentNoteLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>「还有没保存的内容」时用户的选择。</summary>
internal enum AgentUnsavedChoice
{
    Cancel,
    Discard,
    SaveFirst
}
