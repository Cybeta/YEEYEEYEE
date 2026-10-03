namespace YEEYEEYEE.Desktop;

/// <summary>
/// 「这一轮要不要把改动自动推给服务端」的判断（纯函数，界面只负责照做）。
///
/// 为什么要有这个开关、而且**默认是关的**：桌面端一直是「要不要落盘由『保存修订』决定」——
/// 那是刻意的设计，不是没来得及做自动保存。边改边同步把落盘的时机拿走了，所以它得由用户
/// 显式打开，不能悄悄改掉默认行为。
///
/// 关掉的时候这个函数永远回 false，而 false 不是「失败」，是「这事不归我管」。
/// </summary>
public static class AutoSync
{
    /// <param name="enabled">设置里那个开关。</param>
    /// <param name="configured">配了服务器地址。</param>
    /// <param name="signedIn">登录着（没登录时推过去只会被拒）。</param>
    /// <param name="dirty">有未落盘的改动（干净的时候推过去只是白写一遍）。</param>
    /// <param name="busy">正在保存、或者正退避中。</param>
    public static bool ShouldPush(bool enabled, bool configured, bool signedIn, bool dirty, bool busy) =>
        enabled && configured && signedIn && dirty && !busy;
}
