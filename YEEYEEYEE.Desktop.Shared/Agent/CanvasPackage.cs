using System.Text.Json;

namespace YEEYEEYEE.Desktop;

// 这里原先还有一份 `RecentCanvasState`（位置记录）。它与 YEEYEEYEE.Desktop.Core/CanvasLibrary.cs 里
// 那份同名同字段——两份同名类型并存时，本编译单元里声明的那份会**盖住** Core 的那份，
// 于是 CanvasMigration / CanvasLibrary 拿到的参数类型对不上（编译期就能看出「无法从 A 转换为 A」）。
// 第 127 轮删掉了这里的副本：序列化结构只有一份，在 Core 里。
// 语义没有变化：两边字段名一致，`FormatVersion` 的缺省也是 `CanvasFormat.Legacy`（0）。

/// <summary>资产包导入结果：迁移后的画布状态、资产复制统计与迁移报告。</summary>
public sealed record CanvasPackageImportResult(
    RecentCanvasState State,
    int Imported,
    int Missing,
    MigrationReport Migration);

/// <summary>
/// 画布资产包：把画布 JSON 与它引用的图片资产一起导出到目录，便于拷贝到另一台机器后导入。
/// 画布中保存的是 asset:// 引用，因此包内 assets 目录可以直接被目标机器复用。
/// </summary>
public static class CanvasPackage
{
    public const string CanvasFileName = "canvas.json";
    public const string AssetsFolderName = "assets";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static (int Copied, int Missing) Export(string targetDirectory, RecentCanvasState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(targetDirectory);
        var assetsDirectory = Path.Combine(targetDirectory, AssetsFolderName);
        var copied = 0;
        var missing = 0;

        // 与导入对称：先在副本上迁移再收集，旧字段引用的资产同样会被打包，
        // 包内画布也直接是当前格式；调用方对象不被改动（返工 R9 的同一类问题）。
        var migrated = CanvasMigration.Migrate(CanvasCloner.Clone(state));
        var canvas = migrated.State.Canvas ?? new WorkflowCanvasState();

        foreach (var reference in CollectReferences(canvas))
        {
            var source = AssetStore.Resolve(reference);
            if (source is null) { missing++; continue; }
            var destination = Path.Combine(assetsDirectory, Path.GetFileName(source));
            if (!File.Exists(destination))
            {
                Directory.CreateDirectory(assetsDirectory);
                File.Copy(source, destination, overwrite: false);
            }
            copied++;
        }

        // 包内 canvas.json 走原子写入：导出到已存在的包目录时不会留下半截文件。
        CanvasFileWriter.Write(Path.Combine(targetDirectory, CanvasFileName), migrated.State);
        return (copied, missing);
    }

    /// <summary>读取资产包，并把包内资产补入本机资产目录。</summary>
    public static (RecentCanvasState State, int Imported, int Missing) Import(string canvasFilePath)
    {
        var result = ImportDetailed(canvasFilePath);
        return (result.State, result.Imported, result.Missing);
    }

    /// <summary>
    /// 读取资产包并补入本机资产目录（返工 R9）：先在包内副本上完成迁移，
    /// 再收集资产引用，这样旧字段（如 <c>LegacyAssetPaths</c>）里的资产同样会被复制，缺失计数也真实。
    /// 迁移只作用于包内容的内存副本，不改动包文件本身。
    /// </summary>
    public static CanvasPackageImportResult ImportDetailed(string canvasFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasFilePath);
        var state = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllText(canvasFilePath))
            ?? throw new InvalidDataException("画布文件内容为空或格式无效。");

        // 先迁移再收集：旧格式包里的资产引用此时已经变成附件，能被完整收集。
        var migrated = CanvasMigration.Migrate(state);
        var canvas = migrated.State.Canvas ?? new WorkflowCanvasState();

        var packageAssets = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(canvasFilePath)) ?? string.Empty, AssetsFolderName);
        var localDirectory = AssetStore.EnsureDirectory();
        var imported = 0;
        var missing = 0;

        foreach (var reference in CollectReferences(canvas))
        {
            if (AssetStore.Resolve(reference) is not null) continue;
            var fileName = Path.GetFileName(reference.Replace("asset://", string.Empty, StringComparison.Ordinal));
            var source = Path.Combine(packageAssets, fileName);
            if (!File.Exists(source)) { missing++; continue; }
            var destination = Path.Combine(localDirectory, fileName);
            if (!File.Exists(destination)) File.Copy(source, destination, overwrite: false);
            imported++;
        }

        return new CanvasPackageImportResult(migrated.State, imported, missing, migrated.Report);
    }

    /// <summary>收集画布引用的全部资产引用（节点附件、生成历史与设定库各版本参考图）。</summary>
    public static IEnumerable<string> CollectReferences(WorkflowCanvasState canvas) =>
        canvas.Nodes
            .SelectMany(node => node.Attachments.Select(attachment => attachment.Reference)
                .Concat(node.GenerationHistory.Select(record => record.Output)))
            .Concat(EntityAssets.AllAttachments(canvas).Select(attachment => attachment.Reference))
            .Where(reference => reference.StartsWith("asset://", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
