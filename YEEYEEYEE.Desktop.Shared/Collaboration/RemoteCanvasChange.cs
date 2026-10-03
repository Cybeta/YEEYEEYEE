namespace YEEYEEYEE.Desktop;

/// <summary>别人改了画布之后该怎么办。</summary>
public enum RemoteChangeAction
{
    /// <summary>自己那次保存的回声：什么都不做。</summary>
    Ignore,

    /// <summary>只说，不动。手上还有东西，重载会把它吞掉。</summary>
    NotifyOnly,

    /// <summary>本地是干净的：直接重新载入，别让人自己去点。</summary>
    Reload
}

/// <summary>
/// 别人改了画布之后的判断（纯函数：界面只负责照做，判断本身能被钉住）。
///
/// 为什么重载要这么多条件：重载会**替换内存里的整张画布**。手上还有未保存的改动、
/// 或者正在编辑某个节点（编辑框里那半句还没「应用修改」，它不算进画布，所以
/// 「没有未保存改动」并**不**代表「手上没东西」），重载就会把它们吞掉。
/// 这种时候只提示，让人自己决定。
/// </summary>
public static class RemoteCanvasChange
{
    /// <param name="isOwnEcho">这条推送是不是我自己那次保存的回声。</param>
    /// <param name="hasCanvas">当前有没有打开着的画布。</param>
    /// <param name="dirty">当前标签有没有未保存的改动。</param>
    /// <param name="busy">正在编辑（编辑框有焦点 / 手上占着锁）或正在保存。</param>
    public static RemoteChangeAction Decide(bool isOwnEcho, bool hasCanvas, bool dirty, bool busy)
    {
        if (isOwnEcho) return RemoteChangeAction.Ignore;
        if (!hasCanvas) return RemoteChangeAction.NotifyOnly;
        return dirty || busy ? RemoteChangeAction.NotifyOnly : RemoteChangeAction.Reload;
    }
}
