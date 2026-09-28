using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>最近画布与导出画布的序列化结构。</summary>
public sealed record RecentCanvasState(string Title, int Revision, string Prompt, string NegativePrompt, int Width, int Height, int Steps, double Cfg, string Seed, WorkflowCanvasState Canvas);

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

        foreach (var reference in CollectReferences(state.Canvas))
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

        File.WriteAllText(Path.Combine(targetDirectory, CanvasFileName), JsonSerializer.Serialize(state, Options));
        return (copied, missing);
    }

    /// <summary>读取资产包，并把包内资产补入本机资产目录。</summary>
    public static (RecentCanvasState State, int Imported, int Missing) Import(string canvasFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasFilePath);
        var state = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllText(canvasFilePath))
            ?? throw new InvalidDataException("画布文件内容为空或格式无效。");

        var packageAssets = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(canvasFilePath)) ?? string.Empty, AssetsFolderName);
        var localDirectory = AssetStore.EnsureDirectory();
        var imported = 0;
        var missing = 0;

        foreach (var reference in CollectReferences(state.Canvas))
        {
            if (AssetStore.Resolve(reference) is not null) continue;
            var fileName = Path.GetFileName(reference.Replace("asset://", string.Empty, StringComparison.Ordinal));
            var source = Path.Combine(packageAssets, fileName);
            if (!File.Exists(source)) { missing++; continue; }
            var destination = Path.Combine(localDirectory, fileName);
            if (!File.Exists(destination)) File.Copy(source, destination, overwrite: false);
            imported++;
        }

        return (state, imported, missing);
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
