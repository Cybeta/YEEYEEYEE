using Avalonia.Controls;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 设置页与外壳之间的通道。
///
/// 三页各自只认识自己那几个控件，但有三件事必须由外壳统一管：
/// ①「保存」= 提交所有页再写盘一次；②某一页把 <c>config</c> 改了之后，其它页要跟着重新回显
/// （智能导入就是这种：它写的是图像 / 视频字段，但第一页的表单也在看同一份 config）；
/// ③把结果回报到窗口底部那一行状态里。
///
/// 把这些收进一个上下文对象，而不是继续往 <c>Build</c> 上加参数：页多了之后
/// 四五个同类型的 lambda 排在一起，调用点会变得谁也读不懂谁是谁。
/// </summary>
internal sealed class SettingsPageContext
{
    public required Window Owner { get; init; }

    /// <summary>把每一页界面上的编辑写回内存里的 config（**不落盘**）。外部写入之前必须先调它。</summary>
    public required Action CommitAll { get; init; }

    /// <summary>CommitAll 之后把 config 写盘一次。返回是否写盘成功。</summary>
    public required Func<bool> SaveAll { get; init; }

    /// <summary>从 config 重新回显每一页（外部改过 config 之后调用，例如智能导入完成）。</summary>
    public required Action RefreshAll { get; init; }

    /// <summary>把一行结论回报到窗口底部的状态区。</summary>
    public required Action<string, AgentNoteLevel> Report { get; init; }
}
