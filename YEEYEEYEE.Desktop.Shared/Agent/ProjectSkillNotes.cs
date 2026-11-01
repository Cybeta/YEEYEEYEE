namespace YEEYEEYEE.Desktop;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

/// <summary>
/// 某个内置技能在**这个项目里**的补充要求。
///
/// 为什么不直接改内置技能：那 9 条技能是全局的，改一条会让所有项目都跟着变；
/// 而「这个项目要冷调、那个项目台词更口语」本来就该跟着项目走。
/// </summary>
public sealed class ProjectSkillNote
{
    public string SkillId { get; set; } = string.Empty;

    /// <summary>补充要求正文（追加在技能说明之后，不替换原文）。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>每批准一次 +1。文件里出现过的条目至少是 1。</summary>
    public int Version { get; set; }

    /// <summary>最近一次批准的时间（ISO 8601，本地时区）。</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>旧版本，最新的在前。批准新版本时把上一版推进来，措辞不会被覆盖掉。</summary>
    public List<ProjectSkillNoteRevision> History { get; set; } = new();
}

/// <summary>一条被替换下来的旧版本。</summary>
public sealed class ProjectSkillNoteRevision
{
    public int Version { get; set; }
    public string Note { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>
/// 项目级技能补充的读写。一个项目一个 <c>skill-notes.json</c>，放在项目根目录。
///
/// 谁都不能直接写它：Agent 只能提建议，用户在对话里的卡片上点「应用」才落盘——
/// 技能说明会决定花多少钱、出什么图，它不是模型能单方面改的东西。
/// </summary>
public static class ProjectSkillNotes
{
    public const string FileName = "skill-notes.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 中文不该被转义成 \uXXXX：这个文件是给人看、给人 diff 的。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static string PathFor(string projectRoot) => Path.Combine(projectRoot, FileName);

    /// <summary>读一个项目的补充清单；文件不存在返回空清单（不是错误）。</summary>
    public static (List<ProjectSkillNote> Notes, List<string> Errors) Load(string? projectRoot)
    {
        var notes = new List<ProjectSkillNote>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(projectRoot)) return (notes, errors);

        var path = PathFor(projectRoot);
        if (!File.Exists(path)) return (notes, errors);

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            var array = root switch
            {
                JsonArray items => items,
                JsonObject obj when obj["notes"] is JsonArray items => items,
                _ => null
            };
            if (array is null)
            {
                errors.Add($"{FileName}：顶层不是 notes 数组");
                return (notes, errors);
            }
            foreach (var item in array)
            {
                if (item is not JsonObject entry) continue;
                var skillId = (string?)entry["skillId"] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(skillId)) { errors.Add($"{FileName}：有一条缺少 skillId"); continue; }
                notes.Add(new ProjectSkillNote
                {
                    SkillId = skillId.Trim(),
                    Note = (string?)entry["note"] ?? string.Empty,
                    Version = (int?)entry["version"] ?? 0,
                    UpdatedAt = (string?)entry["updatedAt"] ?? string.Empty,
                    History = ReadHistory(entry["history"])
                });
            }
        }
        catch (JsonException error) { errors.Add($"{FileName}：{error.Message}"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add($"{FileName}：{error.Message}"); }
        return (notes, errors);
    }

    private static List<ProjectSkillNoteRevision> ReadHistory(JsonNode? node)
    {
        var history = new List<ProjectSkillNoteRevision>();
        if (node is not JsonArray items) return history;
        foreach (var item in items)
        {
            if (item is not JsonObject entry) continue;
            history.Add(new ProjectSkillNoteRevision
            {
                Version = (int?)entry["version"] ?? 0,
                Note = (string?)entry["note"] ?? string.Empty,
                UpdatedAt = (string?)entry["updatedAt"] ?? string.Empty
            });
        }
        return history;
    }

    /// <summary>
    /// 写入一条补充（用户批准后才调用）。新版本号 = 旧版本 + 1，旧的正文进 <see cref="ProjectSkillNote.History"/>。
    /// </summary>
    public static bool TryApply(string? projectRoot, string skillId, string note, out int version, out string error)
    {
        version = 0;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(projectRoot)) { error = "还没有打开项目，无法保存技能补充。"; return false; }
        if (string.IsNullOrWhiteSpace(skillId)) { error = "缺少技能 id。"; return false; }
        var text = (note ?? string.Empty).Trim();
        if (text.Length == 0) { error = "补充内容是空的，没有保存。"; return false; }

        try
        {
            Directory.CreateDirectory(projectRoot);
            var (notes, _) = Load(projectRoot);
            var existing = notes.FirstOrDefault(item => string.Equals(item.SkillId, skillId, StringComparison.OrdinalIgnoreCase));
            var stamp = DateTimeOffset.Now.ToString("o");
            if (existing is null)
            {
                existing = new ProjectSkillNote { SkillId = skillId.Trim(), Version = 1, UpdatedAt = stamp, Note = text };
                notes.Add(existing);
            }
            else
            {
                if (existing.Note.Length > 0)
                    existing.History.Insert(0, new ProjectSkillNoteRevision
                    {
                        Version = existing.Version,
                        Note = existing.Note,
                        UpdatedAt = existing.UpdatedAt
                    });
                existing.Note = text;
                existing.Version += 1;
                existing.UpdatedAt = stamp;
            }

            var array = new JsonArray();
            foreach (var item in notes.OrderBy(entry => entry.SkillId, StringComparer.Ordinal))
            {
                var entry = new JsonObject
                {
                    ["skillId"] = item.SkillId,
                    ["note"] = item.Note,
                    ["version"] = item.Version,
                    ["updatedAt"] = item.UpdatedAt
                };
                if (item.History.Count > 0)
                {
                    var history = new JsonArray();
                    foreach (var revision in item.History)
                        history.Add(new JsonObject
                        {
                            ["version"] = revision.Version,
                            ["note"] = revision.Note,
                            ["updatedAt"] = revision.UpdatedAt
                        });
                    entry["history"] = history;
                }
                array.Add(entry);
            }

            var document = new JsonObject { ["notes"] = array };
            File.WriteAllText(PathFor(projectRoot), document.ToJsonString(Options));
            version = existing.Version;
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            error = failure.Message;
            return false;
        }
    }
}
