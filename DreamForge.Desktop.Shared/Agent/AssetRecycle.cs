namespace DreamForge.Desktop;

/// <summary>一次资产移出：原路径与移入的回收路径（撤销时按它原样移回）。</summary>
public sealed record AssetMove(string OriginalPath, string RecycledPath)
{
    /// <summary>
    /// 这一项是否已经成功移回原位（返工 R16-4）。
    /// 恢复失败后用户重试时，整份记录会被重放：没有这个标记的话，**已经恢复好的项**会因为
    /// 回收目录里已经没有对应文件而报「回收目录里已找不到该文件」，于是记录永远清不掉、
    /// 账本永远停在「待恢复」。标记成功后重试只处理真正还没完成的项。
    /// </summary>
    public bool Restored { get; set; }
}

/// <summary>
/// 应用管理的资产回收目录（返工 U1）。
///
/// 为什么不用系统回收站：`AssetStore.MoveToRecycleBin` 把文件交给操作系统的回收站，我们既拿不到目标路径、
/// 也无法在撤销时把它取回来——用户清空回收站就永久丢失，而界面还会报「已撤销」。
/// 提交期的资产移出因此改成**可逆移动**：移入资产目录下的 <c>_recycle</c>，并记录原路径，
/// 撤销时移回原位；移不回来就算失败，保留恢复记录而不是谎报成功。
/// </summary>
public static class AssetRecycle
{
    /// <summary>回收目录（在资产目录下，保证与资产同卷、移动是瞬时的）。</summary>
    public static string Directory => Path.Combine(AssetStore.EnsureDirectory(), "_recycle");

    /// <summary>
    /// 把画布里的资产引用（<c>asset://文件名</c> 或本机路径）移入回收目录（返工 V1）。
    ///
    /// 为什么要单独有这个方法：无引用扫描给出的是**可移植引用**（<c>asset://x.png</c>），
    /// 它不是文件路径——直接交给 <see cref="Move"/> 会因 <c>File.Exists</c> 不成立而返回 null，
    /// 结果是「界面说清理了、其实一个文件都没动」，撤销记录里也就没有可恢复的东西。
    /// 这里统一经 <see cref="AssetStore.Resolve"/> 解析成本机路径后再移动。
    /// </summary>
    public static AssetMove? MoveReference(string? reference) =>
        AssetStore.Resolve(reference) is { } path ? Move(path) : null;

    /// <summary>
    /// 把资产移入回收目录，返回移动记录；失败返回 null（调用方据此如实报告，不要静默当作已清理）。
    /// </summary>
    public static AssetMove? Move(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = Path.GetFileName(sourcePath);
            var target = Path.Combine(Directory, $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..18] + "-" + name);
            File.Move(sourcePath, target);
            return new AssetMove(sourcePath, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 按移动记录把资产移回原位；返回空表示成功（或这一项此前已经恢复过），否则返回可读原因。
    /// 原位置若已被别的文件占用则如实失败，不覆盖。
    /// 返工 R16-4：成功后把这一项标记为已完成，重试时直接跳过——否则已恢复的项会被反复报成
    /// 「回收目录里已找不到该文件」，恢复永远收敛不了。
    /// </summary>
    public static string? Restore(AssetMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        if (move.Restored) return null;
        try
        {
            if (!File.Exists(move.RecycledPath)) return $"{Path.GetFileName(move.OriginalPath)}：回收目录里已找不到该文件";
            if (File.Exists(move.OriginalPath)) return $"{Path.GetFileName(move.OriginalPath)}：原位置已有同名文件，未覆盖";

            var folder = Path.GetDirectoryName(move.OriginalPath);
            if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);
            File.Move(move.RecycledPath, move.OriginalPath);
            move.Restored = true;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{Path.GetFileName(move.OriginalPath)}：{error.Message}";
        }
    }

    /// <summary>
    /// 按移动记录批量还原，返回**仍未恢复**的说明（已恢复过的项不再重复处理，见 <see cref="Restore"/>）。
    /// </summary>
    public static IReadOnlyList<string> RestoreAll(IEnumerable<AssetMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var failures = new List<string>();
        foreach (var move in moves)
            if (Restore(move) is { } failure) failures.Add(failure);
        return failures;
    }
}
