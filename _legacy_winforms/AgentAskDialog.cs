namespace DreamForge.Desktop;

/// <summary>
/// 模型反问时的选择弹窗：列选项让用户点，也随时可以直接输入。
///
/// 为什么要有它：没有这个通道时，模型只能把「你要加哪个画布？节点类型是？内容写什么？」
/// 写成一大段正文，让用户自己读、自己组织语言再回一遍——那是聊天，不是工具该有的样子。
/// </summary>
public sealed class AgentAskDialog : ScaledForm
{
    private readonly TextBox freeText = new();
    private readonly List<RadioButton> choices = new();
    /// <summary>「其他（在下面自己写）」那一项：选中它表示答案是用户自己写的。</summary>
    private readonly RadioButton otherChoice;
    /// <summary>内容容器提到字段上：定窗口尺寸要按它算，而算尺寸发生在构造之后。</summary>
    private readonly FlowLayoutPanel layout = new();
    private string? answer;

    private AgentAskDialog(AgentAsk ask)
    {
        Text = "模型需要你补充一点信息";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = Theme.UiFont;
        // 高度不能写死：选项数量随模型的提问变化，靠布局算完再定尺寸。
        ClientSize = new Size(470, 240);

        var question = new Label
        {
            Text = ask.Question,
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Font = Theme.TitleFont
        };

        layout.Dock = DockStyle.Fill;
        layout.FlowDirection = FlowDirection.TopDown;
        layout.WrapContents = false;
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.Padding = new Padding(18);
        layout.Margin = Padding.Empty;
        layout.Controls.Add(question);

        if (ask.Options.Count > 0)
        {
            layout.Controls.Add(new Label { Text = "选一个：", AutoSize = true, Margin = new Padding(0, 12, 0, 4) });
            foreach (var option in ask.Options)
            {
                var radio = new RadioButton
                {
                    Text = option,
                    AutoSize = true,
                    // 关键：AutoSize + MaximumSize 会让长选项**换行**，而不是被面板横向裁掉。
                    MaximumSize = new Size(426, 0),
                    Margin = new Padding(6, 2, 0, 2)
                };
                // 选项之间互斥：点任意一个就自动取消其它的。
                radio.CheckedChanged += (_, _) => { if (radio.Checked) freeText.Clear(); };
                choices.Add(radio);
                layout.Controls.Add(radio);
            }
        }

        // 「其他」永远要有：模型给的选项再全，也总有它没想到的答案。
        // 选中它就把光标送到下面的输入框——不选中而直接打字也照样算数。
        otherChoice = new RadioButton
        {
            Text = "其他（在下面自己写）",
            AutoSize = true,
            MaximumSize = new Size(426, 0),
            Margin = new Padding(6, 2, 0, 2)
        };
        otherChoice.CheckedChanged += (_, _) =>
        {
            if (!otherChoice.Checked) return;
            freeText.Clear();
            freeText.Focus();
        };
        layout.Controls.Add(otherChoice);
        choices.Add(otherChoice);
        if (ask.Options.Count == 0) otherChoice.Checked = true;
        else choices[0].Checked = true;

        var freeLabel = new Label
        {
            Text = ask.Options.Count > 0 ? "或者直接输入（会覆盖上面的选择）：" : "直接输入：",
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 4)
        };
        freeText.Width = 420;
        freeText.PlaceholderText = "例如：角色节点，名称「林砚」，简述「外门弟子，杂灵根」";
        freeText.TextChanged += (_, _) =>
        {
            if (freeText.TextLength == 0) return;
            foreach (var radio in choices) radio.Checked = false;
        };
        layout.Controls.Add(freeLabel);
        layout.Controls.Add(freeText);

        var ok = new Button { Text = "确定", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 14, 8, 0) };
        ok.Tag = ButtonRole.Primary;
        ok.Click += (_, _) => Submit();
        var cancel = new Button { Text = "跳过", Width = 96, Height = 32, FlatStyle = FlatStyle.Flat, Tag = ButtonRole.Secondary, Margin = new Padding(0, 14, 0, 0) };
        cancel.Click += (_, _) => { answer = null; DialogResult = DialogResult.Cancel; };
        layout.Controls.Add(ok);
        layout.Controls.Add(cancel);

        Controls.Add(layout);

        // 窗口尺寸不在这里定：要等缩放完成（OnLoad）才算得准。
        ClientSize = new Size(470, 240);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>
    /// 按内容真实需要的高度定窗口尺寸，**全程用实际像素**。
    /// 只在缩放完成之后调用：缩放前 DeviceDpi 还不是真值，算出来会被缩放再乘一次。
    /// </summary>
    private void FitToContent()
    {
        var scale = DpiScale;
        var width = Math.Max(320, (int)Math.Round(470 * scale));
        // 先定宽度再排版——否则内容按控件默认的 300px 宽换行，高度会算多。
        ClientSize = new Size(width, ClientSize.Height);
        layout.PerformLayout();
        var minHeight = (int)Math.Round(240 * scale);
        var available = Math.Max(minHeight, Screen.FromControl(this).WorkingArea.Height - 40);
        ClientSize = new Size(width, Math.Clamp(layout.PreferredSize.Height, minHeight, available));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);   // 里面完成按 DPI 缩放
        FitToContent();
    }

    /// <summary>弹出提问，返回用户的答案；用户点「跳过」时返回 null。</summary>
    public static string? Ask(IWin32Window? owner, AgentAsk ask)
    {
        using var dialog = new AgentAskDialog(ask);
        dialog.ShowDialog(owner);
        return dialog.answer;
    }

    private void Submit()
    {
        // 手输的优先：用户亲手打的字比点一个预设项更明确。
        var typed = freeText.Text.Trim();
        if (typed.Length > 0)
        {
            answer = typed;
            DialogResult = DialogResult.OK;
            return;
        }

        var picked = choices.FirstOrDefault(radio => radio.Checked);
        if (picked is null)
        {
            MessageBox.Show("请选一个选项，或直接输入内容。", "还需要一点信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // 选了「其他」就必须配上自己写的内容，否则等于没回答。
        if (ReferenceEquals(picked, otherChoice))
        {
            MessageBox.Show("你选了「其他」，请在下面的输入框里写出你的答案。", "还需要一点信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
            freeText.Focus();
            return;
        }
        answer = picked.Text;
        DialogResult = DialogResult.OK;
    }
}
