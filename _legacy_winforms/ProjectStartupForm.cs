using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace DreamForge.Desktop;

public sealed class ProjectStartupForm : ScaledForm
{
    private ProjectContext? selectedProject;
    private readonly Panel center = new TransparentPanel();
    private readonly Label status = new();
    private static string StartupMediaPath => AppPaths.CombineProgram("startup-media.txt");

    private ProjectStartupForm()
    {
        Text = "YeeYeeYee";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1100, 700);
        BackColor = Theme.EditorBg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        KeyPreview = true;

        var media = new StartupMediaSurface { Dock = DockStyle.Fill };
        center.Dock = DockStyle.Fill;
        center.BackColor = Color.FromArgb(2, 4, 12);
        center.Paint += (_, e) => e.Graphics.Clear(Color.FromArgb(2, 4, 12));
        media.Controls.Add(center);
        status.Text = string.Empty;
        status.AutoSize = false;
        status.Dock = DockStyle.Bottom;
        status.Height = 1;
        status.ForeColor = Color.Transparent;
        status.BackColor = Color.Transparent;
        media.Controls.Add(status);
        Controls.Add(media);
        Shown += (_, _) => ShowHome();
    }

    public static ProjectContext? ShowStartup()
    {
        using var form = new ProjectStartupForm();
        return form.ShowDialog() == DialogResult.OK ? form.selectedProject : null;
    }

    private void ShowHome()
    {
        var content = new TransparentPanel { Size = new Size(640, 500), BackColor = Color.FromArgb(2, 4, 12), Anchor = AnchorStyles.None };
        center.Resize += (_, _) => content.Location = new Point((center.ClientSize.Width - content.Width) / 2, (center.ClientSize.Height - content.Height) / 2);

        var logo = new PictureBox
        {
            Image = BrandAssets.Logo,
            Size = new Size(520, 260),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(2, 4, 12),
            Location = new Point(60, 0),
            Anchor = AnchorStyles.Top
        };
        content.Controls.Add(logo);

        var title = new Label
        {
            Text = "在AI时代您只需要 YES",
            Size = new Size(640, 48),
            Font = new Font("Microsoft YaHei UI", 21F, FontStyle.Bold),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(2, 4, 12),
            Location = new Point(0, 268)
        };
        content.Controls.Add(title);

        var actions = new TransparentFlowLayoutPanel
        {
            Location = new Point(80, 350),
            Size = new Size(480, 70),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(2, 4, 12),
            Padding = new Padding(0)
        };
        actions.Controls.Add(CreateHomeAction("打开项目", "历史项目", false, (_, _) => ShowOpenProject()));
        actions.Controls.Add(CreateHomeAction("新建项目", "开始创作", true, (_, _) => ShowCreateProject()));
        content.Controls.Add(actions);
        SetCenterContent(content);
    }

    private Control CreateHomeAction(string title, string subtitle, bool primary, EventHandler click)
    {
        var button = new RoundedButton
        {
            Title = title,
            Subtitle = subtitle,
            Width = 220,
            Height = 64,
            Radius = 14,
            FillColor = primary ? Theme.PrimaryButton : Theme.FieldBg,
            HoverColor = primary ? Theme.PrimaryButtonHover : Theme.Hover,
            PressedColor = primary ? Theme.Accent : Theme.Selected,
            ForeColor = Color.White,
            Font = Theme.UiFontBold
        };
        button.Click += click;
        return button;
    }

    private void ShowOpenProject()
    {
        var panel = CreateCenterPanel(760, 500);
        panel.Controls.Add(CreateBackButton());
        panel.Controls.Add(new Label { Text = "打开项目", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Theme.Text, Dock = DockStyle.Top, Height = 54, TextAlign = ContentAlignment.MiddleCenter });

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.EditorBg,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            DisplayMember = nameof(ProjectItem.Display),
            IntegralHeight = false,
            ItemHeight = 34,
            Font = Theme.UiFont
        };
        foreach (var path in ProjectHistory.List())
        {
            var project = ProjectContext.Open(path);
            if (project is not null) list.Items.Add(new ProjectItem(project));
        }
        list.DoubleClick += (_, _) => OpenSelected(list);
        var empty = new Label
        {
            Dock = DockStyle.Fill,
            Text = "还没有最近项目\n选择项目文件夹，或先创建一个新项目",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.TextMuted,
            BackColor = Theme.EditorBg,
            Font = Theme.UiFont
        };
        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.EditorBg, Padding = new Padding(1) };
        listHost.Controls.Add(empty);
        listHost.Controls.Add(list);
        empty.Visible = list.Items.Count == 0;
        panel.Controls.Add(listHost);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
            BackColor = Theme.PanelBg
        };
        var openFolder = CreateDialogButton("选择项目文件夹", ButtonRole.Secondary);
        openFolder.Click += (_, _) => OpenExisting();
        var open = CreateDialogButton("进入项目", ButtonRole.Primary);
        open.Enabled = list.Items.Count > 0;
        open.Click += (_, _) => OpenSelected(list);
        actions.Controls.Add(openFolder);
        actions.Controls.Add(open);
        panel.Controls.Add(actions);
        SetCenterContent(panel);
        status.Text = "选择最近项目，或选择包含 project.json 的项目文件夹";
    }

    private void ShowCreateProject()
    {
        var panel = CreateCenterPanel(620, 500);
        panel.Controls.Add(CreateBackButton());
        panel.Controls.Add(new Label { Text = "新建项目", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Theme.Text, Dock = DockStyle.Top, Height = 54, TextAlign = ContentAlignment.MiddleCenter });

        var defaultProjectDirectory = AppPaths.DefaultProjectsRoot;
        var form = new TableLayoutPanel { Dock = DockStyle.Top, Height = 230, ColumnCount = 2, RowCount = 4, Padding = new Padding(30, 12, 30, 0), BackColor = Color.FromArgb(20, 22, 28) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var name = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "例如：第一季短剧", BackColor = Color.FromArgb(31, 34, 43), ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        var location = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = defaultProjectDirectory, BackColor = Color.FromArgb(31, 34, 43), ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        var choose = CreateDialogButton("选择", ButtonRole.Secondary);
        choose.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择项目存放位置" }; if (dialog.ShowDialog(this) == DialogResult.OK) location.Text = dialog.SelectedPath; };
        var locationRow = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 22, 28) };
        locationRow.Controls.Add(location);
        locationRow.Controls.Add(choose);
        choose.Dock = DockStyle.Right;
        var type = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        type.Items.AddRange(new object[] { "视频", "图片", "小说", "其他" });
        type.SelectedIndex = 0;
        AddField(form, 0, "项目名称", name);
        AddField(form, 1, "项目位置", locationRow);
        AddField(form, 2, "项目类型", type);
        panel.Controls.Add(form);

        var create = CreateDialogButton("创建并进入项目", ButtonRole.Primary);
        create.Dock = DockStyle.Bottom;
        create.Height = 42;
        create.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(this, "请输入项目名称。", "新建项目", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (string.IsNullOrWhiteSpace(location.Text) || !Directory.Exists(location.Text.Trim())) { MessageBox.Show(this, "请选择一个已存在且可访问的项目存放文件夹。", "新建项目", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var parentDirectory = location.Text.Trim();
            var projectPath = Path.Combine(parentDirectory, AppPaths.SanitizeDirectoryName(name.Text));
            if (Directory.Exists(projectPath)) { MessageBox.Show(this, "该位置已存在同名文件夹，请更改项目名称或选择其他位置。", "新建项目", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try
            {
                selectedProject = ProjectContext.Create(parentDirectory, name.Text, (ProjectType)type.SelectedIndex);
            var historySaved = ProjectHistory.Add(selectedProject.RootPath);
            DialogResult = DialogResult.OK;
            Close();
            if (!historySaved)
                MessageBox.Show("项目已创建并进入，但最近项目记录无法保存。请检查当前用户对应用数据目录的写入权限。", "最近项目记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var hint = error is UnauthorizedAccessException
                    ? "\n\n该位置当前账户没有写入权限，或写入被系统/安全策略（沙箱、受控文件夹访问等）拦截。请检查文件夹权限，或改选其他存放位置。"
                    : string.Empty;
                MessageBox.Show(this, $"项目创建失败：{error.Message}{hint}", "新建项目失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        panel.Controls.Add(create);
        SetCenterContent(panel);
        status.Text = "填写项目名称、位置和类型后创建项目";
    }

    private void OpenSelected(ListBox list)
    {
        if (list.SelectedItem is not ProjectItem item) return;
        selectedProject = ProjectContext.Open(item.Project.RootPath);
        if (selectedProject is null)
        {
            MessageBox.Show(this, "项目无法读取。请检查文件夹权限，或确认项目文件未损坏。", "打开项目失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var historySaved = ProjectHistory.Add(selectedProject.RootPath);
        Finish();
        if (!historySaved)
            MessageBox.Show("项目已打开，但最近项目记录无法保存。请检查当前用户对应用数据目录的写入权限。", "最近项目记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenExisting()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 project.json 的项目文件夹" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            selectedProject = ProjectContext.Open(dialog.SelectedPath);
            if (selectedProject is null) { MessageBox.Show(this, "所选文件夹不是 YeeYeeYee 项目，或当前账户没有读取权限。", "打开项目失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var historySaved = ProjectHistory.Add(selectedProject.RootPath);
            Finish();
            if (!historySaved)
                MessageBox.Show("项目已打开，但最近项目记录无法保存。请检查当前用户对应用数据目录的写入权限。", "最近项目记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"无法打开项目：{error.Message}", "打开项目失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Finish()
    {
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddField(TableLayoutPanel table, int row, string label, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        table.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.TextMuted, Anchor = AnchorStyles.Left }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private Button CreateBackButton()
    {
        var back = CreateDialogButton("返回", ButtonRole.Secondary);
        back.BackColor = Color.Transparent;
        back.FlatAppearance.BorderSize = 0;
        back.ForeColor = Theme.TextMuted;
        back.Dock = DockStyle.Top;
        back.Click += (_, _) => ShowHome();
        return back;
    }

    private static Button CreateDialogButton(string text, ButtonRole role)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            Tag = role,
            Padding = new Padding(14, 0, 14, 0),
            Margin = new Padding(6, 0, 0, 0),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = role == ButtonRole.Secondary ? 1 : 0;
        button.FlatAppearance.BorderColor = Theme.Border;
        button.BackColor = role == ButtonRole.Primary ? Theme.PrimaryButton : Theme.FieldBg;
        button.ForeColor = role == ButtonRole.Primary ? Theme.OnAccent : Theme.Text;
        return button;
    }

    private Button CreateMainButton(string title, string subtitle, EventHandler click)
    {
        var button = new Button { Text = $"{title}\n{subtitle}", Dock = DockStyle.Fill, Height = 110, Margin = new Padding(12), FlatStyle = FlatStyle.Flat, BackColor = Theme.ChromeBg, ForeColor = Theme.Text, Font = Theme.UiFontBold };
        button.FlatAppearance.BorderColor = Theme.Border;
        button.Click += click;
        return button;
    }

    private Panel CreateCenterPanel(int width, int height)
    {
        var panel = new Panel { Size = new Size(width, height), BackColor = Theme.PanelBg, Padding = new Padding(32), Anchor = AnchorStyles.None };
        center.Resize += (_, _) => panel.Location = new Point((center.ClientSize.Width - panel.Width) / 2, (center.ClientSize.Height - panel.Height) / 2);
        return panel;
    }

    private void SetCenterContent(Control content)
    {
        center.Controls.Clear();
        center.Controls.Add(content);
        content.Location = new Point((center.ClientSize.Width - content.Width) / 2, (center.ClientSize.Height - content.Height) / 2);
    }

    private void SelectStartupMedia()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择启动背景媒体",
            Filter = "图片或动图|*.gif;*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Directory.CreateDirectory(Path.GetDirectoryName(StartupMediaPath)!);
        File.WriteAllText(StartupMediaPath, dialog.FileName);
        MessageBox.Show(this, "启动媒体已设置，下次启动时显示。", "启动背景", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private sealed record ProjectItem(ProjectContext Project)
    {
        public string Display => $"{Project.Descriptor.Name}    [{TypeName(Project.Descriptor.Type)}]    {Project.RootPath}";
        private static string TypeName(ProjectType type) => type switch { ProjectType.Video => "视频", ProjectType.Image => "图片", ProjectType.Novel => "小说", _ => "其他" };
    }

    private sealed class TransparentPanel : Panel
    {
        public TransparentPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Parent is null) return;
            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(-Left, -Top);
            using var args = new PaintEventArgs(e.Graphics, Parent.ClientRectangle);
            InvokePaintBackground(Parent, args);
            InvokePaint(Parent, args);
            e.Graphics.Restore(state);
        }
    }

    private sealed class TransparentFlowLayoutPanel : FlowLayoutPanel
    {
        public TransparentFlowLayoutPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Parent is null) return;
            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(-Left, -Top);
            using var args = new PaintEventArgs(e.Graphics, Parent.ClientRectangle);
            InvokePaintBackground(Parent, args);
            InvokePaint(Parent, args);
            e.Graphics.Restore(state);
        }
    }

    private sealed class LineDrawLogo : Control
    {
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 35 };
        private float progress;

        public LineDrawLogo()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            timer.Tick += (_, _) =>
            {
                progress = Math.Min(1F, progress + 0.018F);
                Invalidate();
                if (progress >= 1F) timer.Stop();
            };
            timer.Start();
            Disposed += (_, _) => timer.Dispose();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var brush = new SolidBrush(Color.FromArgb(2, 4, 12));
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var width = Math.Max(1, ClientSize.Width);
            var height = Math.Max(1, ClientSize.Height);
            var centerX = width / 2F;
            var centerY = 64F;
            var iconRadius = 45F;
            var drawn = Math.Clamp(progress * 1.55F, 0F, 1F);
            using var glow = new Pen(Color.FromArgb(80, 116, 198, 255), 2F);
            using var line = new Pen(Color.FromArgb(235, 220, 238, 255), 2.2F);
            var ring = new RectangleF(centerX - iconRadius, centerY - iconRadius, iconRadius * 2, iconRadius * 2);
            e.Graphics.DrawArc(glow, ring, -90F, 360F * drawn);
            e.Graphics.DrawArc(line, ring, -90F, 270F * drawn);

            using var star = new SolidBrush(Color.FromArgb(230, 226, 241, 255));
            var rays = new[] { (0F, -1F), (0.707F, -0.707F), (1F, 0F), (0.707F, 0.707F), (0F, 1F), (-0.707F, 0.707F), (-1F, 0F), (-0.707F, -0.707F) };
            for (var i = 0; i < rays.Length; i++)
            {
                if (drawn < (i + 1) / 9F) continue;
                var (dx, dy) = rays[i];
                var inner = iconRadius + 7F;
                var outer = iconRadius + (i % 2 == 0 ? 20F : 14F);
                e.Graphics.DrawLine(line, centerX + dx * inner, centerY + dy * inner, centerX + dx * outer, centerY + dy * outer);
            }

            var textProgress = Math.Clamp((progress - 0.2F) / 0.8F, 0F, 1F);
            var text = "YEEYEEYEE";
            using var font = new Font("Microsoft YaHei UI", 25F, FontStyle.Bold);
            var textSize = e.Graphics.MeasureString(text, font);
            using var textBrush = new SolidBrush(Color.FromArgb((int)(240 * textProgress), 232, 240, 255));
            e.Graphics.DrawString(text, font, textBrush, centerX - textSize.Width / 2F, 112F);

            if (progress < 1F)
            {
                using var sweep = new Pen(Color.FromArgb(175, 150, 205, 255), 1F);
                var sweepX = 20F + (width - 40F) * progress;
                e.Graphics.DrawLine(sweep, sweepX, 14F, sweepX, height - 12F);
            }
        }
    }

    private sealed class RoundedPanel : Panel
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius { get; set; } = 16;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (BackColor == Color.Transparent)
            {
                if (Parent is not null)
                {
                    var state = e.Graphics.Save();
                    e.Graphics.TranslateTransform(-Left, -Top);
                    using var args = new PaintEventArgs(e.Graphics, Parent.ClientRectangle);
                    InvokePaintBackground(Parent, args);
                    InvokePaint(Parent, args);
                    e.Graphics.Restore(state);
                }
                return;
            }

            using var path = CreatePath(ClientRectangle, Radius);
            using var brush = new SolidBrush(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var path = CreatePath(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            using var pen = new Pen(Color.FromArgb(55, 255, 255, 255), 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        }

        private static GraphicsPath CreatePath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(2, radius * 2);
            var safe = new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
            path.AddArc(safe.Left, safe.Top, diameter, diameter, 180, 90);
            path.AddArc(safe.Right - diameter, safe.Top, diameter, diameter, 270, 90);
            path.AddArc(safe.Right - diameter, safe.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(safe.Left, safe.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class RoundedButton : Control
    {
        private bool hovered;
        private bool pressed;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title { get; set; } = string.Empty;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Subtitle { get; set; } = string.Empty;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius { get; set; } = 12;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color FillColor { get; set; } = Color.FromArgb(42, 45, 56);
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color HoverColor { get; set; } = Color.FromArgb(55, 59, 72);
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color PressedColor { get; set; } = Color.FromArgb(35, 38, 48);

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = false;
            pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var brush = new SolidBrush(Color.FromArgb(2, 4, 12));
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = CreatePath(bounds, Radius);
            using var brush = new SolidBrush(pressed ? PressedColor : hovered ? HoverColor : FillColor);
            using var border = new Pen(Color.FromArgb(70, 255, 255, 255), 1);
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(border, path);

            var titleFont = new Font(Font.FontFamily, Font.Size + 0.5F, FontStyle.Bold);
            using (titleFont)
            using (var titleBrush = new SolidBrush(ForeColor))
            using (var subtitleBrush = new SolidBrush(Color.FromArgb(185, ForeColor.R, ForeColor.G, ForeColor.B)))
            {
                var left = 16;
                var titleY = string.IsNullOrWhiteSpace(Subtitle) ? (Height - titleFont.Height) / 2 : 11;
                e.Graphics.DrawString(Title.Length > 0 ? Title : Text, titleFont, titleBrush, left, titleY);
                if (!string.IsNullOrWhiteSpace(Subtitle))
                    e.Graphics.DrawString(Subtitle, Theme.SmallFont, subtitleBrush, left, titleY + titleFont.Height + 1);
            }
        }

        private static GraphicsPath CreatePath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(2, radius * 2);
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class StartupMediaSurface : Panel
    {
        private readonly Star[] stars;
        private readonly Random random = new(271828);
        private Image? startupArt;

        private readonly record struct Star(float X, float Y, float Radius, float Brightness, float TwinkleSpeed, float Drift);

        public StartupMediaSurface()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            LoadStartupArt();
            Disposed += (_, _) => startupArt?.Dispose();
            stars = Enumerable.Range(0, 150)
                .Select(_ => new Star(
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    0.45F + (float)random.NextDouble() * 1.65F,
                    0.35F + (float)random.NextDouble() * 0.65F,
                    0.08F + (float)random.NextDouble() * 0.38F,
                    (float)(random.NextDouble() - 0.5) * 0.00025F))
                .ToArray();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(Color.FromArgb(14, 15, 18));
            if (startupArt is not null)
            {
                DrawCoverImage(g, startupArt, ClientRectangle);
                using var veil = new LinearGradientBrush(ClientRectangle,
                    Color.FromArgb(190, 2, 4, 12),
                    Color.FromArgb(105, 2, 8, 18),
                    0F);
                g.FillRectangle(veil, ClientRectangle);
                using var bottom = new LinearGradientBrush(ClientRectangle,
                    Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(175, 2, 4, 12),
                    90F);
                g.FillRectangle(bottom, ClientRectangle);
            }
            else
            {
                DrawStarfield(g);
            }
        }

        private void LoadStartupArt()
        {
            var candidate = AppPaths.CombineProgram(Path.Combine("Assets", "dreamforge-startup-art.png"));
            if (!File.Exists(candidate)) return;
            try
            {
                using var source = Image.FromFile(candidate);
                startupArt = new Bitmap(source);
            }
            catch
            {
                startupArt = null;
            }
        }

        private static void DrawCoverImage(Graphics g, Image image, Rectangle bounds)
        {
            if (image.Width <= 0 || image.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
            var scale = Math.Max((float)bounds.Width / image.Width, (float)bounds.Height / image.Height);
            var width = image.Width * scale;
            var height = image.Height * scale;
            var destination = new RectangleF(
                bounds.X + (bounds.Width - width) / 2F,
                bounds.Y + (bounds.Height - height) / 2F,
                width,
                height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, destination);
        }

        private void DrawStarfield(Graphics g)
        {
            var bounds = ClientRectangle;
            using var background = new LinearGradientBrush(
                bounds,
                Color.FromArgb(7, 12, 28),
                Color.FromArgb(2, 4, 12),
                28F);
            g.FillRectangle(background, bounds);

            using var haze = new LinearGradientBrush(
                new Rectangle(0, bounds.Height / 3, bounds.Width, Math.Max(1, bounds.Height * 2 / 3)),
                Color.FromArgb(26, 48, 72, 118),
                Color.FromArgb(0, 12, 20, 38),
                90F);
            g.FillRectangle(haze, 0, bounds.Height / 3, bounds.Width, Math.Max(1, bounds.Height * 2 / 3));

            foreach (var star in stars)
            {
                var x = star.X * bounds.Width;
                var y = star.Y * bounds.Height;
                var alpha = (int)Math.Clamp(255 * star.Brightness, 45, 210);
                var radius = star.Radius;
                using var brush = new SolidBrush(Color.FromArgb(alpha, 205, 225, 255));
                g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
                if (star.Radius > 1.55F)
                {
                    using var glow = new Pen(Color.FromArgb(Math.Min(80, alpha / 3), 150, 200, 255), 1);
                    g.DrawLine(glow, x - radius * 2.4F, y, x + radius * 2.4F, y);
                    g.DrawLine(glow, x, y - radius * 2.4F, x, y + radius * 2.4F);
                }
            }
        }

        private void OnFrameChanged(object? sender, EventArgs e) => Invalidate();
    }
}
