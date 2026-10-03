using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using YEEYEEYEE.Desktop;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 设置窗口（左页签四页：模型接入 / 生图生视频 / 技能管理 / 协作）。
///
/// 为什么不继续用「一扇门一个弹窗」：模型接入原本只有 Agent 面板右上角那一个入口，
/// 生图 / 生视频、技能则各自散在别处。用户问「我要配个出图接口该去哪」时，
/// 答案是「先点 Agent 面板的 ⚙，但那是聊天模型的设置」——这种答案本身就是问题。
/// 现在左下角一个「设置」把四类配置收进同一个窗口，用页签分开，谁都不挡谁。
///
/// 落盘只在这里做一次：四页各自把「界面 → 内存 config」的转换交出来，
/// 外壳在保存时先依次 Commit 再写盘。分开写盘会出现「切页丢改动」和半截配置落盘。
/// </summary>
internal static class SettingsWindow
{
    private const double ShellWidth = 960;
    private const double ShellHeight = 660;

    /// <summary>左栏宽度。够放下「生图与生视频」这种六字标题加两行说明。</summary>
    private const double NavWidth = 208;

    /// <summary>
    /// 打开设置。返回是否**成功写盘**：调用方据此决定要不要刷新界面上的「当前模型」之类的展示。
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, int initialPage = 0)
    {
        var config = AiProviderSettings.Load();
        var saved = false;

        // 四页的 Commit 收集到一起。注意 list 在 Build 之后才填满，
        // 但 SaveAll 只在用户点击时执行，那时列表已经完整。
        var commits = new List<Action>();
        bool SaveAll()
        {
            foreach (var commit in commits) commit();
            return AiProviderSettings.Save(config);
        }

        var status = Note(string.Empty);
        status.FontSize = 11;
        status.TextWrapping = TextWrapping.Wrap;
        status.IsVisible = false;

        void Report(string text, AgentNoteLevel level = AgentNoteLevel.Info)
        {
            status.Text = text;
            status.Foreground = Brush(level switch
            {
                AgentNoteLevel.Success => "DfSuccess",
                AgentNoteLevel.Warning => "DfWarning",
                AgentNoteLevel.Error => "DfError",
                _ => "DfInk3"
            });
            status.IsVisible = !string.IsNullOrWhiteSpace(text);
        }

        var pages = new List<SettingsPageSection>();

        // 「提交所有页」与「重新回显所有页」都写成局部函数、闭包捕获上面那两个集合：
        // 集合在 Build 之后才填满，但这些函数只在用户点击时执行，那时列表已经完整。
        void CommitAll()
        {
            foreach (var commit in commits) commit();
        }

        void RefreshAll()
        {
            foreach (var page in pages) page.Reload?.Invoke();
        }

        var context = new SettingsPageContext
        {
            Owner = owner,
            CommitAll = CommitAll,
            SaveAll = SaveAll,
            RefreshAll = RefreshAll,
            Report = Report
        };

        // ---------- 四页 ----------

        var modelPage = SettingsModelPage.Build(config, context);
        pages.Add(modelPage);
        commits.Add(modelPage.Commit);

        var media = SettingsMediaPage.Build(config, context);
        pages.Add(new SettingsPageSection
        {
            Title = "生图与生视频",
            Glyph = "✦",
            Summary = "图像 / ComfyUI / 视频三条链路，以及出图观感",
            Root = media.Root,
            Commit = media.Commit,
            Reload = media.Reload
        });
        commits.Add(media.Commit);

        var skillPage = SettingsSkillsPage.Build(config, context);
        pages.Add(skillPage);
        commits.Add(skillPage.Commit);

        var serverPage = SettingsServerPage.Build(config, context);
        pages.Add(serverPage);
        commits.Add(serverPage.Commit);

        // ---------- 左栏页签 ----------

        var nav = new StackPanel { Spacing = 4 };
        var navButtons = new List<Button>();
        var roots = new List<Control>();

        void Select(int index)
        {
            for (var i = 0; i < pages.Count; i++)
            {
                roots[i].IsVisible = i == index;
                navButtons[i].Classes.Set("active", i == index);
            }
            Report(string.Empty);
        }

        foreach (var page in pages)
        {
            var index = navButtons.Count;
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = page.Glyph,
                                    FontSize = 13,
                                    VerticalAlignment = VerticalAlignment.Center,
                                    Foreground = Brush("DfPrimary")
                                },
                                new TextBlock
                                {
                                    Text = page.Title,
                                    FontSize = 12,
                                    FontWeight = FontWeight.SemiBold,
                                    Foreground = Brush("DfInk"),
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            }
                        },
                        new TextBlock
                        {
                            Text = page.Summary,
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brush("DfInk3")
                        }
                    }
                }
            };
            button.Classes.Add("navButton");
            button.Click += (_, _) => Select(index);
            navButtons.Add(button);
            nav.Children.Add(button);

            page.Root.IsVisible = false;
            roots.Add(page.Root);
        }

        var navPane = new StackPanel { Spacing = 12, Margin = new Thickness(12, 14) };
        navPane.Children.Add(nav);
        // 配置文件位置放在左栏底部：这是「我改的东西到底存哪了」的答案，
        // 每页都重复一遍太吵，收到一处又一直看得见。
        navPane.Children.Add(Note($"配置文件：\n{AiProviderSettings.ConfigFilePath}"));

        // 版本与「检查更新」也收在左栏底部：这里是「我现在跑的是哪一版、能不能升」的唯一落点。
        // 版本号读程序集，不在界面上写死。
        var updateButton = new Button
        {
            Content = "检查更新",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        updateButton.Classes.Add("miniButton");
        navPane.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "当前版本 " + AppVersion.Display, FontSize = 11, Foreground = Brush("DfInk2") },
                updateButton
            }
        });

        var contentHost = new Panel();
        foreach (var root in roots) contentHost.Children.Add(root);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions($"{NavWidth},*") };
        columns.Children.Add(new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = Brush("DfLine"),
            Child = new ScrollViewer
            {
                Content = navPane,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        });
        var contentScroll = new ScrollViewer
        {
            Content = contentHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 横向滚动必须关掉：开着的话里面 TextBox 的整段宽度会被当成期望宽度报上去，
            // 窗口会被撑到比设定宽度宽一大截（与 AgentDialogUi.Layout 里同一条理由）。
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetColumn(contentScroll, 1);
        columns.Children.Add(contentScroll);

        // ---------- 底部：状态 + 保存 / 关闭 ----------

        var save = Primary("保存");
        var close = Secondary("关闭");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { close, save }
        };
        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footerRow.Children.Add(status);
        Grid.SetColumn(buttons, 1);
        footerRow.Children.Add(buttons);

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(columns);
        var footer = Footer(footerRow);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);

        var window = DialogShell.Create("设  置", layout, ShellWidth, ShellHeight);

        updateButton.Click += async (_, _) => await UpdateFlow.CheckManuallyAsync(window);

        save.Click += (_, _) =>
        {
            if (!SaveAll())
            {
                Report($"保存失败：配置文件不可写（{AiProviderSettings.ConfigFilePath}）。本次修改没有写入磁盘，请检查目录权限。",
                    AgentNoteLevel.Error);
                return;
            }
            saved = true;
            // 保存后重新回显：停用「当前那一份」时 ResolveSelected 会自动切到另一份，
            // 表单与列表要跟着变，否则界面上显示的「当前」与真正生效的不是同一个。
            foreach (var page in pages) page.Reload?.Invoke();
            Report("已保存。改动对之后的消息 / 生成生效，正在跑的任务不受影响。", AgentNoteLevel.Success);
        };

        close.Click += (_, _) => window.Close(saved);

        // 关窗（✕ / Esc）也要保存：设置页上没有任何「取消」语义的按钮，
        // 让用户改完关掉却什么都没存下来，是这类窗口最常见的一种白折腾。
        var closing = false;
        var giveUpSaving = false;
        window.Closing += (_, e) =>
        {
            if (closing) return;
            if (!giveUpSaving && !SaveAll())
            {
                // 写不进盘时**先不关**：这时关掉等于把用户刚填的密钥悄悄扔掉。
                // 但也不能把人锁在窗口里，所以下一次关闭就按「放弃这次修改」处理。
                e.Cancel = true;
                giveUpSaving = true;
                Report($"保存失败：配置文件不可写（{AiProviderSettings.ConfigFilePath}）。" +
                       "本次修改还没有写入磁盘——再点一次关闭就会放弃这次修改。",
                    AgentNoteLevel.Error);
                return;
            }
            if (!giveUpSaving) saved = true;
            closing = true;
            window.Close(saved);
        };

        Select(Math.Clamp(initialPage, 0, pages.Count - 1));
        return await window.ShowDialog<bool>(owner);
    }
}
