using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>
/// 保存被主动中止：目标文件已存在但备份失败，或来源格式版本高于当前支持版本。
/// 调用方必须把消息显示给用户，不得静默忽略（返工 R7）。
/// </summary>
public sealed class CanvasSaveAbortedException : IOException
{
    public CanvasSaveAbortedException(string message) : base(message) { }

    public CanvasSaveAbortedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>深拷贝工具：在隔离副本上做迁移、校验与写入，避免改动调用方持有的对象图（返工 R8）。</summary>
public static class CanvasCloner
{
    private static readonly JsonSerializerOptions Options = new();

    public static WorkflowCanvasState Clone(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        return JsonSerializer.Deserialize<WorkflowCanvasState>(JsonSerializer.Serialize(canvas, Options), Options)
            ?? throw new InvalidDataException("无法复制画布内容。");
    }

    public static RecentCanvasState Clone(RecentCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Canvas is null ? state : state with { Canvas = Clone(state.Canvas) };
    }
}

/// <summary>
/// 画布文件写入前的可恢复备份（目标 1.3）。备份放在画布库目录下的 backups 子目录，
/// 不参与 <see cref="CanvasLibrary.List"/>（只枚举顶层 *.json），也不进版本库。
/// </summary>
public static class CanvasBackup
{
    public const string FolderName = "backups";

    /// <summary>备份目录：画布库目录下的 backups。</summary>
    public static string Directory => Path.Combine(CanvasLibrary.Directory, FolderName);

    /// <summary>
    /// 备份现有文件；源文件不存在时返回 null（无需备份）。失败不抛出，返回 null 并给出原因，
    /// 由调用方决定是否继续写入。
    /// </summary>
    public static string? TryBackup(string path, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var backupDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, FolderName);
            System.IO.Directory.CreateDirectory(backupDirectory);
            var name = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(backupDirectory, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.{Guid.NewGuid():N}.json");
            File.Copy(path, target, overwrite: false);
            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>列出备份，按写入时间倒序（最新在前）。</summary>
    public static IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<string>();
        try
        {
            return System.IO.Directory.EnumerateFiles(Directory, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>在备份目录里找到某个画布的备份（按文件名前缀），用于“恢复备份”。</summary>
    public static IReadOnlyList<string> ListFor(string canvasPath)
    {
        var name = Path.GetFileNameWithoutExtension(canvasPath);
        return List().Where(path => Path.GetFileName(path).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>每个画布只保留最新的若干份备份，避免长期使用后无限增长；返回删除数量。</summary>
    public static int Prune(int keepPerCanvas = 10)
    {
        var removed = 0;
        foreach (var group in List().GroupBy(BackupGroupKey))
        {
            foreach (var path in group.Skip(keepPerCanvas))
            {
                try { File.Delete(path); removed++; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }

        return removed;
    }

    /// <summary>备份文件名去掉时间戳及可选唯一后缀后的分组键。</summary>
    private static string BackupGroupKey(string backupPath)
    {
        var name = Path.GetFileNameWithoutExtension(backupPath);
        var match = System.Text.RegularExpressions.Regex.Match(name,
            @"\.\d{8}-\d{6}-\d{3}(?:\.[0-9a-fA-F]{32})?$");
        return match.Success ? name[..match.Index] : name;
    }

    /// <summary>
    /// 用备份替换目标画布文件。恢复前会先给当前文件再存一份备份；这份备份失败时不覆盖当前文件。
    /// </summary>
    public static string Restore(string backupPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!File.Exists(backupPath)) throw new FileNotFoundException("备份文件不存在。", backupPath);

        // 恢复前的当前文件也留一份，避免恢复本身成为不可逆操作。
        if (File.Exists(targetPath))
        {
            var safety = TryBackup(targetPath, out var error);
            if (safety is null)
                throw new CanvasSaveAbortedException($"恢复前无法为当前文件创建备份，已中止恢复：{error}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrWhiteSpace(directory)) System.IO.Directory.CreateDirectory(directory);
        File.Copy(backupPath, targetPath, overwrite: true);
        return targetPath;
    }
}

/// <summary>画布文件的原子写入：先写临时文件再替换，失败时删除临时文件并保留源文件。</summary>
public static class CanvasFileWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// 写入画布文件。目标已存在时必须先由调用方完成备份（见 <see cref="CanvasBackup.TryBackup"/>）；
    /// 这里只负责「临时文件 + 替换」，任何失败都不影响原文件。
    /// </summary>
    public static byte[] Write(string path, RecentCanvasState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(directory)) System.IO.Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory ?? string.Empty, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Options));
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
            return bytes;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}

/// <summary>打开画布的结果：迁移与校验都在读取阶段完成，任何校验错误都不阻止打开。</summary>
public sealed record CanvasOpenOutcome(
    RecentCanvasState State,
    MigrationReport Migration,
    CanvasIdentityReport Validation,
    string Path)
{
    /// <summary>打开时是否执行了确定性迁移改动。</summary>
    public bool Migrated => Migration.Changed;

    /// <summary>来源格式版本高于当前支持版本：只能只读查看，保存会被拒绝。</summary>
    public bool UnsupportedFormat => Migration.UnsupportedFormat;

    /// <summary>打开后是否需要提示用户查看问题。</summary>
    public bool NeedsAttention => Migration.Changed || Migration.Ambiguities.Count > 0 || Validation.HasErrors || UnsupportedFormat;
}

/// <summary>
/// 打开画布的真实入口（目标 1.3 / A.4）：读取 → 内存迁移（不写盘）→ 只读校验。
/// 旧数据先能被查看，问题以报告形式返回，不因校验错误拒绝访问；
/// 更高格式版本只做只读查看并给出可读诊断（返工 R11）。
/// </summary>
public static class CanvasOpenService
{
    public static bool TryOpen(string path, out CanvasOpenOutcome outcome, out string error)
    {
        if (!TryRead(path, out var state, out error)) { outcome = null!; return false; }
        return TryOpenState(state, path, out outcome, AssetStore.Exists);
    }

    /// <summary>从调用方已读取的一份字节快照打开，避免投影与修订来自不同文件版本。</summary>
    public static bool TryOpenBytes(byte[] bytes, string path, out CanvasOpenOutcome outcome, out string error,
        Func<string, bool>? assetAccessible = null)
    {
        outcome = null!;
        error = string.Empty;
        try
        {
            var state = JsonSerializer.Deserialize<RecentCanvasState>(bytes);
            if (state is null) { error = "画布文件内容为空。"; return false; }
            return TryOpenState(state, path, out outcome, assetAccessible);
        }
        catch (JsonException exception) { error = "画布文件不是有效的 JSON：" + exception.Message; return false; }
    }

    private static bool TryOpenState(RecentCanvasState state, string path, out CanvasOpenOutcome outcome,
        Func<string, bool>? assetAccessible)
    {
        var migrated = state.FormatVersion > CanvasFormat.Current
            ? new CanvasMigrationResult(state, MigrationReport.Unsupported(state.FormatVersion))
            : CanvasMigration.Migrate(state);
        var validation = CanvasIdentityValidator.Validate(migrated.State.Canvas ?? new WorkflowCanvasState(), assetAccessible);
        outcome = new CanvasOpenOutcome(migrated.State, migrated.Report, validation, path);
        return true;
    }

    /// <summary>只读取画布文件，不迁移、不校验。</summary>
    public static bool TryRead(string path, out RecentCanvasState state, out string error)
    {
        state = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) { error = "画布路径为空。"; return false; }

        try
        {
            if (!File.Exists(path)) { error = "画布文件不存在。"; return false; }
            var parsed = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllText(path));
            if (parsed is null) { error = "画布文件内容为空。"; return false; }
            state = parsed;
            return true;
        }
        catch (JsonException exception) { error = "画布文件不是有效的 JSON：" + exception.Message; return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { error = exception.Message; return false; }
    }
}

/// <summary>保存画布的结果：写入了哪个文件、备份在哪、迁移与校验报告。</summary>
public sealed record CanvasSaveOutcome(
    string Path,
    string? BackupPath,
    MigrationReport Migration,
    CanvasIdentityReport Validation)
{
    public byte[] WrittenBytes { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// 保存画布的真实入口（目标 1.3，返工 R7、R8、R11）：
/// 1. 先深拷贝：迁移、校验、写入都只作用于副本，失败时调用方对象图完全不变；
/// 2. 格式版本高于当前支持时拒绝覆盖保存；
/// 3. 目标已存在时先备份，备份失败直接中止并抛出 <see cref="CanvasSaveAbortedException"/>，不覆盖源文件；
/// 4. 写入采用临时文件 + 替换，失败清理临时文件并保留原文件。
/// </summary>
public static class CanvasSaveService
{
    public static CanvasSaveOutcome Save(RecentCanvasState state, string path, byte[]? expectedBytes = null, Func<ProjectEntityFile?>? validateUnderLease = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Desktop and Web share a file-system lease. Expected bytes are checked under that
        // lease, before backup or replacement, not merely in the HTTP adapter.
        var fullPath = Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var lease = new FileStream(fullPath + ".web.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (expectedBytes is not null && (!File.Exists(path) ||
            !expectedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(path))))
            throw new CanvasSaveAbortedException("画布已被其他进程修改，拒绝覆盖保存。请重新加载。");

        var authority = validateUnderLease?.Invoke();
        // R8：隔离副本，任何后续失败都不会改动调用方的对象。
        var isolated = CanvasCloner.Clone(state);
        EnsureWritable(isolated);

        // 目标 6 / G6-1：项目级资源在画布文件里只是快照，写盘前从项目库刷新，
        // 保证「改一次库 → 所有画布看到同一份内容」，同时旧版程序仍能读到完整快照。
        if (isolated.Canvas is not null)
        {
            if (validateUnderLease is not null)
                ProjectEntityScope.RefreshSnapshots(isolated.Canvas, authority ?? new ProjectEntityFile());
            else
                ProjectEntityScope.RefreshSnapshots(isolated.Canvas);
        }

        var migrated = CanvasMigration.Migrate(isolated);
        var validation = CanvasIdentityValidator.Validate(migrated.State.Canvas ?? new WorkflowCanvasState(),
            validateUnderLease is null ? AssetStore.Exists : null);
        if (validateUnderLease is not null && validation.HasErrors)
            throw new CanvasSaveAbortedException("项目库快照刷新后画布引用校验失败，已中止保存。");
        var (backup, writtenBytes) = WriteWithBackupBytes(path, migrated.State, prune: validateUnderLease is null);
        return new CanvasSaveOutcome(path, backup, migrated.Report, validation) { WrittenBytes = writtenBytes };
    }

    /// <summary>更高的格式版本不允许被当前版本覆盖保存（返工 R11）。</summary>
    public static void EnsureWritable(RecentCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FormatVersion <= CanvasFormat.Current) return;
        throw new CanvasSaveAbortedException(
            $"该画布的格式版本是 {state.FormatVersion}，高于当前支持的 {CanvasFormat.Current}；已拒绝覆盖保存，以免丢失未知数据。"
            + "可以用只读方式查看，或用更高版本的程序打开。");
    }

    /// <summary>
    /// 「先备份再替换」的统一实现：目标不存在就直接写入；存在但备份失败则中止。
    /// </summary>
    public static string? WriteWithBackup(string path, RecentCanvasState state) => WriteWithBackupBytes(path, state).Backup;

    private static (string? Backup, byte[] Bytes) WriteWithBackupBytes(string path, RecentCanvasState state, bool prune = true)
    {
        string? backup = null;
        if (File.Exists(path))
        {
            backup = CanvasBackup.TryBackup(path, out var error);
            if (backup is null)
                throw new CanvasSaveAbortedException($"目标文件已存在，但备份失败，已中止保存以免覆盖原文件：{error}");
        }

        var bytes = CanvasFileWriter.Write(path, state);
        // Pruning is maintenance, not part of the commit. A failure must not turn a saved
        // canvas into an apparent failed write.
        try
        {
            if (prune && string.Equals(AppPaths.CurrentProject.RootPath,
                Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path))!), StringComparison.OrdinalIgnoreCase))
                CanvasBackup.Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return (backup, bytes);
    }
}
