using DreamForge.Desktop;

namespace DreamForge.Desktop.Avalonia;

/// <summary>
/// Agent 面板与工作台之间的契约，由 <see cref="MainWindow"/> 实现。
///
/// 与旧 WinForms 端的 <c>AgentPaneHost</c> 相比，这里刻意收窄了职责：
/// 面板只负责「对话、解析、列表展示」，凡是**碰画布和文件**的动作（预检、试算、提交、撤销、保存）
/// 全部交回工作台执行。这样 Agent 永远不可能绕开保存入口去改数据，也便于单测替换实现。
/// </summary>
public interface IAgentSessionHost
{
    /// <summary>当前项目是否可写。只读项目下不允许把改动写进画布。</summary>
    bool IsProjectEditable { get; }

    /// <summary>当前模型配置的可读标签，用于面板顶部展示（未接入时说明原因）。</summary>
    string ProviderLabel { get; }

    /// <summary>
    /// 面板模型选择器的候选：所有**已启用**的配置，以及当前正在用的是哪一份。
    ///
    /// 候选只给已启用的：停用的配置在设置里勾一下就能回来，但让它出现在"随手可切"的下拉里，
    /// 就变成了"点一下就悄悄换成一个没打算用的模型"，那是设置页该管的事。
    /// </summary>
    IReadOnlyList<AgentModelChoice> ModelChoices();

    /// <summary>把当前使用的模型切到某一份已启用的配置并落盘。返回 null 表示成功。</summary>
    string? SelectModel(string profileId);

    /// <summary>取对话用的模型能力；未接入真实模型时返回 null，面板据此给出接入提示。</summary>
    IAiChatProvider? CreateProvider();

    /// <summary>当前模型是否支持图片输入（多模态）。</summary>
    bool ImageInputEnabled { get; }

    /// <summary>上下文预算换算出的字符上限（0 表示模型未声明窗口、不截断）。发送前截断用这个。</summary>
    int ContextCharacterBudget { get; }

    /// <summary>
    /// 模型声明的上下文窗口（token；0 表示未声明）。
    /// 与字符预算是两件事：那个是**发送前**按比例估算用来截断的，这个是**收到服务端回传的真实 token 之后**
    /// 用来告诉用户"这一次占了多少窗口"的。前者是估的，后者是真的，不能拿一个冒充另一个。
    /// </summary>
    int ContextWindowTokens { get; }

    /// <summary>Agent 工作文件夹；write_file 动作只能落在这个目录内。</summary>
    string? WorkspacePath { get; }

    /// <summary>组装当前创作上下文（选中节点、画布节点、资源、工作树、工作文件夹）。</summary>
    AgentContext BuildContext();

    /// <summary>整批预检，返回每条动作的提示（null 表示没有需要提醒的后果）。</summary>
    IReadOnlyList<string?> Precheck(IReadOnlyList<AgentAction> actions);

    /// <summary>试算这批动作会产生什么差异；**不触碰真实画布，也不写文件**。</summary>
    CanvasPreview Preview(IReadOnlyList<AgentAction> actions);

    /// <summary>
    /// 把这批待审批的动作画成**画布虚影**（半透明卡片 + 虚线框）：只画不写，
    /// 审批后由 <see cref="ClearPendingPreview"/> 收掉、真实节点接上。传空集合等于清除。
    /// </summary>
    void ShowPendingPreview(IReadOnlyList<AgentAction> actions);

    /// <summary>收掉画布虚影（审批完成或放弃这批改动时调用）。</summary>
    void ClearPendingPreview();

    /// <summary>把动作提交到真实画布并保存。返回值把「应用了几条 / 失败原因 / 未生效的动作」分开报。</summary>
    AgentCommitReport Commit(IReadOnlyList<AgentAction> actions);

    /// <summary>撤销上一批已提交的改动（画布与文件一起回滚）。返回 null 表示成功。</summary>
    string? UndoLastCommit();

    /// <summary>是否还有可撤销的 Agent 批次。</summary>
    bool CanUndoLastCommit { get; }

    /// <summary>重绘画布与工作树列表（提交或撤销之后调用）。</summary>
    void RefreshCanvasSurface();

    /// <summary>打开模型接入设置。返回是否真的保存了配置——没保存就不该对用户说「已更新」。</summary>
    Task<bool> OpenModelSettingsAsync();

    /// <summary>
    /// 重新走一次接入引导（选服务商 → 密钥 → 测试连接）。
    /// 与首次启动弹的是同一个窗口，只是入口不同：面板上要能随时换一家，不必去翻设置里的每个字段。
    /// </summary>
    Task<bool> RunOnboardingAsync();
}

/// <summary>Agent 模型选择器里的一个候选。</summary>
/// <param name="Id">配置的 Id（切回来时用它，而不是拿显示名去认——名字可以重复）。</param>
/// <param name="Label">显示名（留空时退到模型名）。</param>
/// <param name="IsCurrent">是不是当前正在用的那一份。</param>
/// <param name="IsConfigured">地址与模型都填了。没填全的仍会列出来，但会标注，免得切过去发现聊不了。</param>
public sealed record AgentModelChoice(string Id, string Label, bool IsCurrent, bool IsConfigured);
