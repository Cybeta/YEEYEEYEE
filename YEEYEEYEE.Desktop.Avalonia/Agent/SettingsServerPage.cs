using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 设置第四页：协作（Web 服务）。
///
/// 这一页管的是「这台机器接不接服务器」。留空就是不接——桌面端照旧独立工作，
/// **没有服务器不是错误状态，是默认状态**。
///
/// 为什么必须登录而不是填一个访问令牌：编辑锁的意义是「显示谁在编辑」，
/// 而桌面桥那个 Bearer 令牌不带用户身份，服务端在锁接口上按设计拒绝它。
/// 所以桌面端要有自己的账号，登录后别人看到的是「林晚（桌面端）正在编辑」。
///
/// 密码**不落盘**、会话只在这一次打开设置期间有效：重启要重新登录。
/// 这是有意的第一步——把会话安全地存起来（开机就是在编辑）留到真正需要的时候再做。
/// </summary>
internal static class SettingsServerPage
{
    public static SettingsPageSection Build(AiProviderConfig config, SettingsPageContext context)
    {
        var report = context.Report;
        void Say(string text, AgentNoteLevel level = AgentNoteLevel.Info) => report(text, level);

        // 会话是**应用持有**的那一个（画布那边占编辑锁用的是同一个），本页只是它的界面。
        // 早先这里自己新建、关窗释放：那样登录状态活不过关窗，画布也就永远占不上锁。
        var session = context.Collaboration;

        var (urlLabel, urlBox) = AgentDialogUi.Field("服务器地址", config.CollaborationServerUrl, "例如 http://127.0.0.1:5000；留空 = 不接协作");
        var (accountLabel, accountBox) = AgentDialogUi.Field("账号", config.CollaborationAccount, "网页端登录用的同一个账号");
        var (passwordLabel, passwordBox) = AgentDialogUi.Field("密码", string.Empty, "只用于这次登录，不写进配置文件");
        passwordBox.PasswordChar = '•';

        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AgentDialogUi.Brush("DfInk2")
        };

        void ShowStatus()
        {
            var lines = new List<string>();
            if (session is { IsSignedIn: true, User: { } me })
            {
                lines.Add($"已登录：{CollaborationSession.Display(me)}（{me.RoleLabel}） @ {session.BaseUrl}");
                // 这一行是桌面端唯一能看见「我自己占着什么」的地方：画布上还没有锁的徽标。
                lines.Add(session.HeldNodeLease is { } held
                    ? $"你这台机器正占着节点 {held.TargetId?.ToString("N")[..8] ?? "—"}，别人这时候改不了它。"
                    : "你这台机器没有占着任何节点。");
                if (session.Leases.Count == 0) lines.Add("当前没有人正在编辑。");
                else
                {
                    lines.Add($"当前 {session.Leases.Count} 条编辑锁：");
                    foreach (var lease in session.Leases) lines.Add(Describe(lease));
                }
            }
            else
            {
                lines.Add(urlBox.Text is { Length: > 0 }
                    ? "未登录。填账号与密码后点「登录」——登录之后才能看到谁在编辑。"
                    : "未配置服务器：桌面端独立工作。要接协作就填上服务器地址。");
            }

            status.Text = string.Join("\n", lines);
        }

        var signIn = AgentDialogUi.Secondary("登录");
        signIn.Click += async (_, _) =>
        {
            var url = urlBox.Text?.Trim() ?? string.Empty;
            if (url.Length == 0)
            {
                Say("先把服务器地址填上（例如 http://127.0.0.1:5000）。", AgentNoteLevel.Warning);
                return;
            }

            // 地址可能刚被改过：换服务器会清掉旧身份与 cookie，这是有意的（旧会话对新服务器没有意义）。
            session.UseServer(url);
            var result = await session.SignInAsync(accountBox.Text?.Trim() ?? string.Empty, passwordBox.Text ?? string.Empty);
            if (result.Ok)
            {
                urlBox.Text = session.BaseUrl;      // 回显规范后的地址（去掉结尾斜杠这类差异）
                passwordBox.Text = string.Empty;    // 密码用完就清掉，它本来也不落盘
                accountBox.Text = session.User!.Username;
            }

            Say(result.Message, result.Ok ? AgentNoteLevel.Success : AgentNoteLevel.Error);
            ShowStatus();
        };

        var signOut = AgentDialogUi.Secondary("退出");
        signOut.Click += async (_, _) =>
        {
            if (!session.IsSignedIn)
            {
                Say("还没有登录。");
                return;
            }

            var result = await session.SignOutAsync();
            Say(result.Message, AgentNoteLevel.Success);
            ShowStatus();
        };

        var refresh = AgentDialogUi.Secondary("看谁在编辑");
        refresh.Click += async (_, _) =>
        {
            if (!session.IsSignedIn)
            {
                Say("先登录才能看谁在编辑。", AgentNoteLevel.Warning);
                return;
            }

            var result = await session.RefreshLeasesAsync();
            Say(result.Message, result.Ok ? AgentNoteLevel.Info : AgentNoteLevel.Error);
            ShowStatus();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { signIn, signOut, refresh }
        };

        // 默认关：桌面端一直是「要不要落盘由『保存修订』决定」，这是刻意设计。
        // 自动同步把落盘时机拿走了，所以只能显式打开。
        var autoSync = new CheckBox
        {
            Content = "改完自动同步（静默约 2 秒就推给服务端，不用点「保存修订」）",
            IsChecked = config.CollaborationAutoSync,
            FontSize = 11,
            Foreground = AgentDialogUi.Brush("DfInk2")
        };
        var autoSyncNote = AgentDialogUi.Note(
            "打开后：改动停下来约两秒就自动推给服务端，别人那边会自动跟上；被别人的编辑锁挡住时只提示一句、"
            + "改动留在本地等你点「保存修订」。关掉就是原来的样子：什么时候落盘由你决定。");

        var root = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                AgentDialogUi.Header("协作（Web 服务）"),
                AgentDialogUi.Note(
                    "填了服务器地址并登录之后，桌面端才会参与协作：别人在网页端编辑时会看到你，"
                    + "你也能在这里看到谁正拿着哪条编辑锁。留空就是不接协作，桌面端照旧独立工作。"),
                urlLabel, urlBox,
                accountLabel, accountBox,
                passwordLabel, passwordBox,
                buttons,
                autoSync,
                autoSyncNote,
                status
            }
        };

        void Commit()
        {
            config.CollaborationServerUrl = (urlBox.Text ?? string.Empty).Trim();
            config.CollaborationAccount = (accountBox.Text ?? string.Empty).Trim();
            config.CollaborationAutoSync = autoSync.IsChecked == true;
        }

        void Reload()
        {
            urlBox.Text = config.CollaborationServerUrl;
            accountBox.Text = config.CollaborationAccount;
            autoSync.IsChecked = config.CollaborationAutoSync;
            passwordBox.Text = string.Empty;
            ShowStatus();
        }

        Reload();

        return new SettingsPageSection
        {
            Title = "协作",
            Glyph = "⇄",
            Summary = "接哪台服务器、以谁的身份登录、谁在编辑",
            Root = root,
            Commit = Commit,
            Reload = Reload
        };
    }

    /// <summary>
    /// 一条锁说成一句人话。
    ///
    /// 节点用 ID 前 8 位而不是标题：这一页只与服务器打交道，手上没有画布，
    /// 硬去查标题就得把画布也读一遍——**宁可显示一个能对上的短 ID，也不猜一个可能错的标题**。
    /// </summary>
    private static string Describe(CollaborationLease lease)
    {
        var who = $"{lease.DisplayName}（{CollaborationSession.ClientLabel(lease.Client)}）";
        var target = lease.TargetId is { } id ? id.ToString("N")[..8] : "—";
        return lease.Scope == "tree" ? $"· {who} 正在编辑整棵画布（含结构）" : $"· {who} 正在编辑节点 {target}";
    }
}
