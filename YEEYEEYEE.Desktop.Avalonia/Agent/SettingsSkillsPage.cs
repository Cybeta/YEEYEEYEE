using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using YEEYEEYEE.Desktop;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 设置第三页：技能管理。
///
/// 两类技能，管法**故意不一样**：
/// ①**内置技能**是编译进程序的一张静态表（章节拆解、图片生成、角色设定…），
///   只能"停用"，不能删——删了下次启动又回来，那个删除按钮就是个谎。停用名单记在配置里（见 <see cref="AiProviderConfig.DisabledBuiltInSkills"/>）。
/// ②**已装入的技能**是技能目录下的 JSON，可以逐个启停（<see cref="SkillLibrary.TrySetEnabled"/> 只改 Enabled 一个键），
///   也可以删（<see cref="SkillLibrary.TryDelete"/> 只删这一个文件）。
///
/// 停用与删除的差别在界面上逐条写清：停用是"先别用它，配置和文件都留着"，删除是"连文件一起没了"。
/// 这两件事被混为一谈时，用户会因为"只是想暂时不用"而把辛苦导入的技能删掉。
/// </summary>
internal static class SettingsSkillsPage
{
    public static SettingsPageSection Build(AiProviderConfig config, SettingsPageContext context)
    {
        // 页内的写法保持原样：外壳的能力只在开头取一次别名，正文不必到处写 context.xxx。
        var owner = context.Owner;
        var report = context.Report;
        Func<bool> requestSave = context.SaveAll;
        // 页内回报一律走它：绝大多数是普通提示，写全两个参数只会让调用点变得很吵。
        void Say(string text, AgentNoteLevel level = AgentNoteLevel.Info) => report(text, level);

        config.DisabledBuiltInSkills ??= new List<string>();

        var builtInHost = new StackPanel { Spacing = 6 };
        var installedHost = new StackPanel { Spacing = 6 };
        var sitesHost = new StackPanel { Spacing = 6 };

        // ---------- 站点与池子 ----------

        void RefreshSites()
        {
            sitesHost.Children.Clear();
            var (sites, errors) = SiteCatalog.Load();
            foreach (var error in errors)
                sitesHost.Children.Add(Note($"读不了站点：{error}", AgentNoteLevel.Warning));

            if (sites.Count == 0)
            {
                sitesHost.Children.Add(Note(
                    "还没有登记任何站点。到「生图与生视频」页顶端用「智能导入」给一个接口说明网页，"
                    + "导入一次就会登记成站点，并把这家能用的池子（模型 × 档位）一并记下来；"
                    + "贴 ComfyUI 地址的话，登记出来的站点下面挂的是那台服务器的工作流。"));
                return;
            }

            foreach (var site in sites)
            {
                var captured = site;
                var detail = new StackPanel { Spacing = 4, IsVisible = false };

                var toggle = Secondary(captured.IsComfyUi ? "展开工作流" : "展开池子");
                toggle.FontSize = 10;
                toggle.VerticalAlignment = VerticalAlignment.Top;
                toggle.Click += (_, _) =>
                {
                    detail.IsVisible = !detail.IsVisible;
                    toggle.Content = detail.IsVisible
                        ? (captured.IsComfyUi ? "收起工作流" : "收起池子")
                        : (captured.IsComfyUi ? "展开工作流" : "展开池子");
                };

                var rename = Secondary("改名");
                rename.FontSize = 10;
                rename.VerticalAlignment = VerticalAlignment.Top;
                rename.Click += async (_, _) =>
                {
                    var text = await PromptAsync(context.Owner, "给站点改个名字", "当前用的是主机名，改成你顺口的名字（例如 my-site）", captured.DisplayName);
                    if (text is null) return;
                    captured.DisplayName = text.Trim();
                    if (!SiteCatalog.Save(captured, out var failure))
                    {
                        Say($"站点改名没能写盘：{failure}", AgentNoteLevel.Error);
                        return;
                    }
                    Say($"站点已改名为「{captured.Label}」。");
                    RefreshSites();
                };

                var remove = Secondary("删除");
                remove.FontSize = 10;
                remove.VerticalAlignment = VerticalAlignment.Top;
                remove.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync(context.Owner, "删除站点",
                            $"要删掉站点「{captured.Label}」吗？"
                            + (captured.IsComfyUi
                                ? $"它下面 {captured.Workflows.Count} 份工作流的正文会一起删掉。\n"
                                : $"它下面 {captured.Pools.Count} 个池子的登记会一起删掉。\n")
                            + "已经出过的图和画布上的附件不受影响；要再建回来重新导入一次即可。",
                            "删除"))
                        return;
                    if (!SiteCatalog.TryDelete(captured, out var failure))
                    {
                        Say($"站点删不掉：{failure}", AgentNoteLevel.Error);
                        return;
                    }
                    Say($"已删除站点「{captured.Label}」。");
                    RefreshSites();
                };

                // 密钥状态放在**卡片上永远看得见**的位置：用户上次找不到它，就是因为唯一的入口
                // 藏在「生图与生视频」那一页的字段里，而那一页跟「我刚导入的这家站」看不出关系。
                var keyStatus = new TextBlock
                {
                    Text = "密钥：" + captured.DescribeApiKey(),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    // 只用调色板里确实有的键：编一个不存在的颜色键，出来的可能是抛异常或透明，
                    // 而「设了没设」这件事文字上已经说清了（已设置 / 未设置 / 解不开）。
                    Foreground = Brush(captured.HasApiKey ? "DfInk2" : "DfInk3")
                };

                var info = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        Row(captured.Label, Chip(captured.Id, "DfPrimary"),
                            captured.IsComfyUi
                                ? captured.VideoWorkflows.Count > 0 ? Chip("含视频工作流", "DfInk3") : null
                                : captured.HasVideoPools ? Chip("含视频池", "DfInk3") : null),
                        new TextBlock { Text = captured.Describe(), FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = Brush("DfInk3") },
                        keyStatus
                    }
                };

                // 填密钥：**一家站一把账号**，所以填在这一家站上，而不是设置里那一个全局字段。
                // 全局只有一把的话，导入第二家站会把第一家的密钥覆盖掉，第一家那批池子从此全部 401。
                var keyBox = new TextBox
                {
                    Watermark = "粘贴这一家的 API Key（保存后加密落盘，界面不回显）",
                    PasswordChar = '●',
                    FontSize = 11,
                    Background = Brush("DfSurface2"),
                    Foreground = Brush("DfInk")
                };
                var saveKey = Secondary("保存密钥");
                saveKey.FontSize = 10;
                saveKey.Click += (_, _) =>
                {
                    var typed = keyBox.Text?.Trim() ?? string.Empty;
                    if (typed.Length == 0)
                    {
                        Say("没输入密钥。要清掉已保存的那把，点「清除」。", AgentNoteLevel.Warning);
                        return;
                    }
                    captured.ApiKey = typed;
                    if (!SiteCatalog.Save(captured, out var failure))
                    {
                        Say($"密钥没能写盘：{failure}", AgentNoteLevel.Error);
                        return;
                    }
                    // 存完就地清空：界面上不留明文，也不回显。
                    keyBox.Text = string.Empty;
                    Say($"站点「{captured.Label}」的密钥已加密保存（{SecretProtector.Describe(typed)}）。");
                    RefreshSites();
                };
                var clearKey = Secondary("清除");
                clearKey.FontSize = 10;
                clearKey.Click += (_, _) =>
                {
                    captured.ApiKey = string.Empty;
                    if (!SiteCatalog.Save(captured, out var failure))
                    {
                        Say($"清除没能写盘：{failure}", AgentNoteLevel.Error);
                        return;
                    }
                    Say($"站点「{captured.Label}」的密钥已清除：调用时会退回设置里的「图像接口密钥」。");
                    RefreshSites();
                };
                detail.Children.Add(new TextBlock
                {
                    Text = "这一家的 API Key",
                    FontSize = 10,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brush("DfInk2")
                });
                detail.Children.Add(keyBox);
                detail.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { saveKey, clearKey }
                });

                // 池子按「种类 + 模型」聚合着列：同一个模型的三个档位是一行，不是一个池子一行。
                foreach (var group in captured.Pools
                             .GroupBy(pool => $"{(pool.IsVideo ? "视频" : "图像")} · {pool.Model}")
                             .OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    var tiers = string.Join("、", group.Select(pool => pool.Tier.Length == 0 ? "（未声明档位）" : pool.Tier));
                    var sample = group.First();
                    detail.Children.Add(new TextBlock
                    {
                        Text = $"· {group.Key}：{tiers}"
                            + (sample.Price.Length > 0 ? $"｜{sample.Price}" : string.Empty)
                            + (sample.Enabled ? string.Empty : "｜清单里标为下线"),
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush(sample.Enabled ? "DfInk2" : "DfInk3")
                    });
                }

                // ComfyUI 站点的子项是工作流，不是池子——只列池子的话这一台下面三百多份在这儿一份都看不见。
                // 按服务器上的顶层文件夹（= 模型家族）分组，一份一行：推荐的那份标出来，
                // 没转成的写明原因，被停用的压暗（它们还在，只是不进选择器的候选）。
                foreach (var group in captured.Workflows
                             .GroupBy(workflow => workflow.Folder.Length == 0 ? "（根目录）" : workflow.Folder)
                             .OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    var kinds = group.Select(workflow => workflow.Kind).Distinct(StringComparer.Ordinal).ToList();
                    var kindLabel = kinds.Count == 1
                        ? kinds[0] switch { "video" => "视频", "image" => "图像", "audio" => "声音", _ => "用途未知" }
                        : "混合";
                    detail.Children.Add(new TextBlock
                    {
                        Text = $"· {group.Key}（{group.Count()} 份 · {kindLabel}）",
                        FontSize = 10,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("DfInk2")
                    });

                    foreach (var workflow in group.OrderBy(item => item.Title, StringComparer.Ordinal))
                    {
                        var marks = new List<string>();
                        if (workflow.Recommended) marks.Add("推荐");
                        if (!workflow.Enabled) marks.Add("已停用");
                        detail.Children.Add(new TextBlock
                        {
                            Text = $"    {workflow.Title}"
                                + (marks.Count > 0 ? $"｜{string.Join("、", marks)}" : string.Empty)
                                + (workflow.Error.Length > 0
                                    ? $"｜{workflow.Error}"
                                    : workflow.Note.Length > 0 ? $"｜{workflow.Note}" : string.Empty),
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap,
                            // 转不成的用告警色：这是一件要人去处理的事（多半是这份工作流引用了服务器上没装的节点）。
                            // 色板里只有三档墨色，所以「已停用」靠行里的字样标出来，不靠再压暗一级。
                            Foreground = Brush(workflow.Converted ? "DfInk3" : "DfError")
                        });
                    }
                }

                detail.Children.Add(Note(captured.IsComfyUi
                    ? $"地址：{captured.BaseUrl}｜工作流正文目录：{SiteCatalog.PayloadDirectory(captured.Id)}"
                      + $"｜清单：{Path.Combine(SiteCatalog.Directory, captured.Id + ".json")}"
                    : $"接口：{captured.BaseUrl}｜文件：{Path.Combine(SiteCatalog.Directory, captured.Id + ".json")}"));

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Top,
                    Children = { toggle, rename, remove }
                };

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                grid.Children.Add(info);
                Grid.SetColumn(actions, 1);
                grid.Children.Add(actions);

                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(grid);
                card.Children.Add(detail);
                sitesHost.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush("DfLine"),
                    Background = Brush("DfSurface2"),
                    Child = card
                });
            }
        }

        // ---------- 内置技能 ----------

        void RefreshBuiltIn()
        {
            builtInHost.Children.Clear();
            foreach (var skill in BuiltInSkills.All)
            {
                var captured = skill;
                var enabled = !config.DisabledBuiltInSkills.Contains(captured.Id);

                var toggle = new CheckBox
                {
                    Content = "启用",
                    IsChecked = enabled,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                toggle.IsCheckedChanged += (_, _) =>
                {
                    var wanted = toggle.IsChecked == true;
                    config.DisabledBuiltInSkills.Remove(captured.Id);
                    if (!wanted) config.DisabledBuiltInSkills.Add(captured.Id);
                    // 立刻落盘：Agent 每次问"有哪些技能可用"读的是磁盘上这份配置，
                    // 停在内存里的停用名单对正在跑的会话不起作用。
                    if (!requestSave())
                        Say("改动没能写盘：Agent 这次仍会看到旧的技能清单。", AgentNoteLevel.Warning);
                    Say(wanted
                        ? $"已启用内置技能「{captured.Name}」，Agent 会重新把它列入可调用技能。"
                        : $"已停用内置技能「{captured.Name}」：它不会出现在 Agent 的技能清单里，整段说明也不再进提示词。");
                };

                var toggleColumn = new StackPanel { Margin = new Thickness(0, 1, 0, 0), Children = { toggle } };

                var info = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        Row(captured.Name, Chip("内置", "DfPrimary"), enabled ? null : Chip("已停用", "DfInk3")),
                        new TextBlock
                        {
                            Text = captured.Description,
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brush("DfInk2")
                        },
                        new TextBlock
                        {
                            // 输出格式是"这个技能到底会产出什么"的正经答案，但很长，
                            // 所以给它单独一行、更淡的颜色，不跟描述抢注意力。
                            Text = "输出：" + captured.OutputFormat.Replace('\n', ' '),
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brush("DfInk3")
                        }
                    }
                };

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                grid.Children.Add(toggleColumn);
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);

                builtInHost.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush("DfLine"),
                    Background = Brush(enabled ? "DfSurface2" : "DfSurface1"),
                    Child = grid
                });
            }
        }

        // ---------- 已装入的技能 ----------

        void RefreshInstalled()
        {
            installedHost.Children.Clear();
            var (skills, errors) = SkillLibrary.Load();

            foreach (var error in errors)
                installedHost.Children.Add(Note($"读不了：{error}", AgentNoteLevel.Warning));

            if (skills.Count == 0 && errors.Count == 0)
            {
                installedHost.Children.Add(Note(
                    "技能目录里还没有技能文件。可以从「API 导入」把接口文档导成技能，或直接把技能 JSON 放进下面的目录。"));
                return;
            }

            foreach (var skill in skills)
            {
                var captured = skill;
                var toggle = new CheckBox
                {
                    Content = "启用",
                    IsChecked = captured.Enabled,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                toggle.IsCheckedChanged += (_, _) =>
                {
                    var wanted = toggle.IsChecked == true;
                    if (!SkillLibrary.TrySetEnabled(captured, wanted, out var failure))
                    {
                        Say($"改不了「{captured.Name}」：{failure}", AgentNoteLevel.Error);
                        // 写不进去就把勾选状态摆回去，否则界面在说谎。
                        toggle.IsChecked = captured.Enabled;
                        return;
                    }
                    Say(wanted
                        ? $"已启用「{captured.Name}」。"
                        : $"已停用「{captured.Name}」：文件与配置都留着，只是不再被调用。");
                };

                var chips = new List<Control> { Chip(captured.IsImported ? "导入" : "手写", "DfPrimary") };
                if (captured.IsPlannedOnly) chips.Add(Chip("不可执行", "DfWarning"));
                if (captured.HasUnsupportedCapability) chips.Add(Chip("含未知能力", "DfError"));
                if (!captured.Enabled) chips.Add(Chip("已停用", "DfInk3"));

                var header = Row(captured.Name, chips.ToArray());

                var detail = new TextBlock
                {
                    Text = Describe(captured),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("DfInk3")
                };

                var info = new StackPanel { Spacing = 3 };
                info.Children.Add(header);
                info.Children.Add(detail);
                if (!string.IsNullOrWhiteSpace(captured.Description))
                    info.Children.Add(new TextBlock
                    {
                        Text = captured.Description,
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("DfInk2")
                    });

                var remove = Secondary("删除");
                remove.FontSize = 10;
                remove.VerticalAlignment = VerticalAlignment.Top;
                remove.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync(owner, "删除技能",
                            $"要删除技能「{captured.Name}」吗？会删掉它的技能文件：\n{captured.FilePath}\n" +
                            "如果只是暂时不想用它，把「启用」勾选框去掉即可——文件与配置都会留着。",
                            "删除"))
                        return;
                    if (!SkillLibrary.TryDelete(captured, out var failure))
                    {
                        Say($"删不掉「{captured.Name}」：{failure}", AgentNoteLevel.Error);
                        return;
                    }
                    RefreshInstalled();
                    Say($"已删除技能「{captured.Name}」及其文件。");
                };

                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                grid.Children.Add(toggle);
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);

                installedHost.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brush("DfLine"),
                    Background = Brush(captured.Enabled ? "DfSurface2" : "DfSurface1"),
                    Child = grid
                });
            }
        }

        var openFolder = Secondary("打开技能目录");
        openFolder.HorizontalAlignment = HorizontalAlignment.Left;
        openFolder.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(SkillLibrary.EnsureDirectory()) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                // 打不开文件管理器不是错到要中断的事：把目录路径原样告诉用户，他自己去看也行。
                Say($"打不开文件管理器（{error.Message}）。技能目录：{SkillLibrary.Directory}", AgentNoteLevel.Warning);
            }
        };

        var rescan = Secondary("重新扫描");
        rescan.HorizontalAlignment = HorizontalAlignment.Left;
        rescan.Margin = new Thickness(6, 0, 0, 0);
        rescan.Click += (_, _) =>
        {
            RefreshInstalled();
            Say("已重新扫描技能目录。");
        };

        var scanRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { openFolder, rescan }
        };

        // ---------- 组装 ----------

        var root = new StackPanel { Spacing = 8, Margin = new Thickness(20) };

        root.Children.Add(Header("站点与池子"));
        root.Children.Add(Note(
            "一个站点一家：它下面「能用的池子」（模型 × 档位）都记在这个站点里。"
            + "池子不是技能、也不各占一个文件——一家站几十个池子，逐个建文件只会把技能目录刷满；"
            + "真正要回答的是「这家有哪些能用的」，那是一个站点的问题。"
            + "调用时会先问一次用哪个池子（价格与画幅差别很大，不该替你挑）。"));
        root.Children.Add(sitesHost);

        root.Children.Add(Header("内置技能"));
        root.Children.Add(Note(
            "随程序发布的技能，用来告诉 Agent「这类要求该按什么套路产出」。内置技能**只能停用、不能删除**——"
            + "它是代码里的一张表，删掉下次启动又会回来。停用会把它从 Agent 的可调用技能清单与提示词里一并去掉。"));
        root.Children.Add(builtInHost);

        root.Children.Add(Header("已装入的技能"));
        root.Children.Add(Note(
            "技能目录里的一步步出图 / 出视频流程。停用只改技能文件里的一个开关（配置与文件都留着）；"
            + "删除会把技能文件删掉——这两件事不一样，想「暂时不用」请去停用，别用删除。"));
        root.Children.Add(scanRow);
        root.Children.Add(installedHost);

        root.Children.Add(Header("技能目录"));
        root.Children.Add(Note(SkillLibrary.Directory));

        RefreshSites();
        RefreshBuiltIn();
        RefreshInstalled();

        return new SettingsPageSection
        {
            Title = "技能管理",
            Glyph = "✧",
            Summary = "内置与已装入技能",
            Root = root,
            // 内置技能的停用名单写在 config 上、由外壳统一落盘；已装入技能的启停是各自文件里的开关，
            // 点下去就立刻写了。所以这里没有额外要提交的东西。
            Commit = () => { },
            Reload = () =>
            {
                RefreshSites();
                RefreshBuiltIn();
                RefreshInstalled();
            }
        };
    }

    private static string Describe(SkillDefinition skill)
    {
        var kind = string.IsNullOrWhiteSpace(skill.TargetKind) ? "Any" : skill.TargetKind;
        var steps = skill.Steps.Count == 0 ? "无步骤" : $"{skill.Steps.Count} 步";
        var text = $"id {skill.Id} · v{skill.Version} · 适用 {kind} · {steps} · 产出到 {skill.OutputTarget}";
        if (skill.IsPlannedOnly && !string.IsNullOrWhiteSpace(skill.PlannedReason))
            text += $"\n不可执行的原因：{skill.PlannedReason}";
        if (!string.IsNullOrWhiteSpace(skill.FilePath))
            text += $"\n文件：{System.IO.Path.GetFileName(skill.FilePath)}";
        return text;
    }

    /// <summary>一行标题 + 若干小标签（"内置" / "导入" / "已停用"…），标签用来回答"这是哪一种"。</summary>
    private static StackPanel Row(string title, params Control?[] chips)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("DfInk"),
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (var chip in chips)
            if (chip is not null) row.Children.Add(chip);
        return row;
    }

    private static Border Chip(string text, string foreground) => new()
    {
        Padding = new Thickness(6, 0),
        CornerRadius = new CornerRadius(6),
        Background = Brush("DfSurface3"),
        BorderBrush = Brush("DfLine"),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9, Foreground = Brush(foreground) }
    };
}

