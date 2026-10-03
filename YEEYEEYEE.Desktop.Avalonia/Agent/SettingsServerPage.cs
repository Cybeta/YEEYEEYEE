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

        // 会话活到这个设置窗口关上为止：登录状态不跨窗口共享，也就不会悄悄留在内存里没人管。
        CollaborationSession? session = null;
        context.Owner.Closed += (_, _) => session?.Dispose();

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
            if (session is { IsSignedIn: true } signedIn)
            {
                lines.Add($"已登录：{CollaborationSession.Display(signedIn.User!)}（{signedIn.User!.RoleLabel}） @ {signedIn.BaseUrl}");
                if (signedIn.Leases.Count == 0) lines.Add("当前没有人正在编辑。");
                else
                {
                    lines.Add($"当前 {signedIn.Leases.Count} 条编辑锁：");
                    foreach (var lease in signedIn.Leases) lines.Add(Describe(lease));
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

            // 地址可能刚被改过，而会话的基地址在构造时就定了：每次登录按当前地址新建一个。
            session?.Dispose();
            session = new CollaborationSession(url);
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
            if (session is null)
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
            if (session is not { IsSignedIn: true })
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
                status
            }
        };

        void Commit()
        {
            config.CollaborationServerUrl = (urlBox.Text ?? string.Empty).Trim();
            config.CollaborationAccount = (accountBox.Text ?? string.Empty).Trim();
        }

        void Reload()
        {
            urlBox.Text = config.CollaborationServerUrl;
            accountBox.Text = config.CollaborationAccount;
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
        var who = $"{lease.DisplayName}（{ClientLabel(lease.Client)}）";
        var target = lease.TargetId is { } id ? id.ToString("N")[..8] : "—";
        return lease.Scope == "tree" ? $"· {who} 正在编辑整棵画布（含结构）" : $"· {who} 正在编辑节点 {target}";
    }

    /// <summary>来源端文案与服务端 <c>EditClient.Label</c> 同一套说法：认不出的算网页端。</summary>
    private static string ClientLabel(string client) => client == "desktop" ? "桌面端" : "网页端";
}
