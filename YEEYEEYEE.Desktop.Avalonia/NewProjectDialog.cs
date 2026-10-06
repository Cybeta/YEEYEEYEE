using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>新建项目要定下来的两件事：叫什么、放在哪。</summary>
internal sealed record NewProjectRequest(string Name, string ParentDirectory);

/// <summary>
/// 新建项目：问一个名字，再问一个放哪儿。
///
/// **为什么要一个对话框**：以前这两个都不问——名字按时间戳自动起，位置固定落在默认项目根目录。
/// 用户想把它叫成「第三集」、想放进自己整理的那块盘里，都做不到，只能建完再去资源管理器里改名与搬迁，
/// 而搬完那条最近项目记录还会指向旧路径。
///
/// 启动页那一栏与工作台顶栏**共用这一个对话框**：它们是同一件事，各自实现一份必然慢慢走偏。
/// </summary>
internal static class NewProjectDialog
{
    public static async Task<NewProjectRequest?> ShowAsync(Window owner, string defaultParent, string? initialName = null)
    {
        var name = new TextBox
        {
            FontSize = 12,
            Text = initialName ?? string.Empty,
            Watermark = "项目名称，例如：雾港来信（留空则按时间命名）",
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk")
        };

        // 位置这一格**只读**：它是「浏览…」选出来的结果，手打路径既容易写错，
        // 也要额外判一遍目录是否存在、是否可写。选，比输入可靠。
        var parent = new TextBox
        {
            FontSize = 12,
            Text = defaultParent,
            IsReadOnly = true,
            Background = Brush("DfSurface2"),
            Foreground = Brush("DfInk2")
        };

        var preview = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("DfInk3")
        };

        // 把「最后会落在哪个文件夹」提前算出来给用户看。名字里的非法字符在这里就被换掉，
        // 而不是等到建完发现名字变了——那时他已经不记得自己填过什么。
        void RefreshPreview()
        {
            var trimmed = name.Text?.Trim() ?? string.Empty;
            var folder = trimmed.Length > 0
                ? AppPaths.SanitizeDirectoryName(trimmed)
                : $"未命名项目-{DateTime.Now:yyyyMMdd-HHmmss}";
            var target = Path.Combine(parent.Text ?? string.Empty, folder);
            preview.Text = Directory.Exists(target)
                ? $"将创建：{target}{Environment.NewLine}（这个文件夹已存在，会自动加序号——不会写进已有项目）"
                : $"将创建：{target}";
        }

        var browse = Secondary("浏览…");
        browse.Click += async (_, _) =>
        {
            var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择项目放在哪个文件夹",
                AllowMultiple = false
            });
            var path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            parent.Text = path;
            RefreshPreview();
        };

        var locationRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        locationRow.Children.Add(parent);
        Grid.SetColumn(browse, 1);
        locationRow.Children.Add(browse);

        name.TextChanged += (_, _) => RefreshPreview();

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        body.Children.Add(Header("项目名称"));
        body.Children.Add(name);
        body.Children.Add(Header("存放位置"));
        body.Children.Add(locationRow);
        body.Children.Add(preview);

        var cancel = Secondary("取消");
        var create = Primary("创建项目");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);

        var dialog = DialogShell.Create("新建项目", Layout(body, Footer(buttons)), 560);
        // 建完窗口就没了的那个值：用局部变量带出去，而不是靠 ShowDialog 的返回值——
        // 标题条的 ✕ 与 Esc 走的是 Close()（不带值），那条路就等于取消。
        NewProjectRequest? request = null;
        create.Click += (_, _) =>
        {
            request = new NewProjectRequest(name.Text?.Trim() ?? string.Empty, parent.Text ?? defaultParent);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => name.Focus();

        RefreshPreview();
        await dialog.ShowDialog(owner);
        return request;
    }
}
