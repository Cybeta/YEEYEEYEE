using System.Text.Json;

namespace DreamForge.Desktop;

/// <summary>回收站条目种类（目标 4 / 4.3、4.4）。</summary>
public enum RecycleEntryKind
{
    Entity,
    Variant
}

/// <summary>
/// 一条回收站记录：被删除的实体或变体的完整快照，附删除时间与当时引用它的来源。
/// 删除是「移入回收站」而不是抹掉内容，所以可以原样还原且 ID 不变——还原后原有引用重新生效。
/// </summary>
public sealed record RecycleEntry(
    Guid Id,
    RecycleEntryKind Kind,
    string DisplayName,
    Guid? EntityId,
    string? EntityName,
    string PayloadJson,
    DateTimeOffset DeletedAt,
    IReadOnlyList<string> ReferencedBy);

/// <summary>
/// 设定库回收站：把删除的实体/变体记到项目根目录的 recycle-bin.json，支持还原与彻底删除。
/// 只写这一个文件，不触碰画布文件，也不改写任何引用关系。
/// </summary>
public static class CanvasRecycleBin
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private const int KeepEntries = 200;

    public static string FilePath => AppPaths.Combine("recycle-bin.json");

    public static IReadOnlyList<RecycleEntry> List()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<RecycleEntry>();
            var entries = JsonSerializer.Deserialize<List<RecycleEntry>>(File.ReadAllText(FilePath)) ?? new List<RecycleEntry>();
            return entries.OrderByDescending(entry => entry.DeletedAt).ToArray();
        }
        catch (JsonException) { return Array.Empty<RecycleEntry>(); }
        catch (IOException) { return Array.Empty<RecycleEntry>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<RecycleEntry>(); }
        catch (InvalidOperationException) { return Array.Empty<RecycleEntry>(); }
    }

    public static int Count => List().Count;

    /// <summary>
    /// 把实体整体移入回收站（含它的全部变体与版本）。
    /// <b>只有快照真正写盘成功才返回 true</b>；写不进去时必须由调用方放弃删除，绝不吞掉失败。
    /// </summary>
    public static bool TryStashEntity(
        WorkflowEntity entity,
        IReadOnlyList<ReferenceHit> references,
        out RecycleEntry? entry,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return TryAppend(
            new RecycleEntry(
                Guid.NewGuid(),
                RecycleEntryKind.Entity,
                entity.Name,
                entity.Id,
                entity.Name,
                JsonSerializer.Serialize(entity, Options),
                DateTimeOffset.UtcNow,
                Describe(references)),
            out entry,
            out error);
    }

    /// <summary>
    /// 把单个变体移入回收站（快照里保留它所属实体的 ID，便于还原到原实体）。
    /// 同样只有写盘成功才返回 true。
    /// </summary>
    public static bool TryStashVariant(
        WorkflowEntity entity,
        WorkflowEntityVariant variant,
        IReadOnlyList<ReferenceHit> references,
        out RecycleEntry? entry,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(variant);
        return TryAppend(
            new RecycleEntry(
                Guid.NewGuid(),
                RecycleEntryKind.Variant,
                $"{entity.Name} · {variant.Name}",
                entity.Id,
                entity.Name,
                JsonSerializer.Serialize(variant, Options),
                DateTimeOffset.UtcNow,
                Describe(references)),
            out entry,
            out error);
    }

    /// <summary>
    /// 还原条目到给定画布：
    /// · 实体：ID 已存在时只补齐缺失的变体（不覆盖现有内容），否则整体加回；
    /// · 变体：所属实体还在就补回该变体，实体已被删除就按实体整体还原。
    /// 引用按稳定 ID 匹配，所以还原后原有引用会重新生效。
    ///
    /// 先把回收站记录落盘成功，才改动画布：写失败时画布保持原样，不产生「已还原但记录还在」的半状态。
    /// </summary>
    public static bool Restore(Guid entryId, WorkflowCanvasState canvas, out string message)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        message = string.Empty;
        var entries = List().ToList();
        var entry = entries.FirstOrDefault(item => item.Id == entryId);
        if (entry is null) { message = "回收站里找不到这条记录（可能已被彻底删除）。"; return false; }

        Action apply;
        string success;
        try
        {
            if (entry.Kind == RecycleEntryKind.Entity)
            {
                var entity = JsonSerializer.Deserialize<WorkflowEntity>(entry.PayloadJson, Options);
                if (entity is null) { message = "回收站记录内容无法解析。"; return false; }
                var existing = canvas.FindEntity(entity.Id);
                if (existing is null)
                {
                    apply = () => canvas.Entities.Add(entity);
                    success = $"已还原实体「{entity.Name}」（含 {entity.Variants.Count} 个变体），原有引用已重新生效。";
                }
                else
                {
                    var missing = entity.Variants
                        .Where(variant => existing.Variants.All(item => item.Id != variant.Id))
                        .ToList();
                    apply = () => existing.Variants.AddRange(missing);
                    success = missing.Count > 0
                        ? $"实体「{existing.Name}」已存在，补回 {missing.Count} 个缺失变体。"
                        : $"实体「{existing.Name}」已存在且没有缺失变体，无需还原。";
                }
            }
            else
            {
                var variant = JsonSerializer.Deserialize<WorkflowEntityVariant>(entry.PayloadJson, Options);
                if (variant is null) { message = "回收站记录内容无法解析。"; return false; }
                var entity = entry.EntityId is { } entityId ? canvas.FindEntity(entityId) : null;
                if (entity is null)
                {
                    message = "所属实体已被删除：请先还原该实体，再还原这个变体。";
                    return false;
                }

                if (entity.Variants.Any(item => item.Id == variant.Id))
                {
                    apply = () => { };
                    success = $"变体「{variant.Name}」已存在，无需还原。";
                }
                else
                {
                    apply = () => entity.Variants.Add(variant);
                    success = $"已还原变体「{entity.Name} · {variant.Name}」，原有引用已重新生效。";
                }
            }
        }
        catch (JsonException) { message = "回收站记录内容无法解析。"; return false; }

        if (!TryWrite(entries.Where(item => item.Id != entryId), out var error))
        {
            message = $"回收站写入失败，已取消还原（画布未改动）：{error}";
            return false;
        }

        apply();
        message = success;
        return true;
    }

    /// <summary>从回收站彻底移除一条记录（不可再还原）。写盘失败返回 false 并给出原因。</summary>
    public static bool TryPurge(Guid entryId, out string error)
    {
        error = string.Empty;
        var entries = List().ToList();
        if (entries.RemoveAll(item => item.Id == entryId) == 0)
        {
            error = "回收站里找不到这条记录。";
            return false;
        }

        return TryWrite(entries, out error);
    }

    /// <summary>清空回收站。写盘失败返回 false 并给出原因。</summary>
    public static bool TryClear(out string error)
    {
        error = string.Empty;
        if (List().Count == 0)
        {
            error = "回收站已经是空的。";
            return false;
        }

        return TryWrite(Array.Empty<RecycleEntry>(), out error);
    }

    private static bool TryAppend(RecycleEntry entry, out RecycleEntry? stored, out string error)
    {
        stored = null;
        // 新条目在最前；超出上限时丢弃最旧的记录。写盘成功才算真的进了回收站。
        if (!TryWrite(new[] { entry }.Concat(List()).Take(KeepEntries), out error)) return false;
        stored = entry;
        return true;
    }

    /// <summary>
    /// 写回收站文件；失败返回 false 并把原因带回。
    /// 绝不吞掉失败：调用方必须据此取消删除，否则会出现「快照没落盘但原数据已删」的不可恢复状态。
    /// </summary>
    private static bool TryWrite(IEnumerable<RecycleEntry> entries, out string error)
    {
        error = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory)) System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries.ToList(), Options));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static IReadOnlyList<string> Describe(IReadOnlyList<ReferenceHit> references)
    {
        if (references is null || references.Count == 0) return Array.Empty<string>();
        return references
            .Select(hit => $"{hit.SourceLabel}：{hit.NodeTitle}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
