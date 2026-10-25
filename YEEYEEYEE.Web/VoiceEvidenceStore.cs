using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>
/// 声音证据的落盘：挨着画布放一份 <c>&lt;画布&gt;.voice.json</c>。
///
/// 为什么不塞进任务库：任务会被清理、会被重试，而「这一镜的声音已被验证过」属于创作产物的一部分，
/// 要跟画布一起备份、一起移交。也不塞进画布 JSON：那是节点正文的权威文件，
/// 证据变更不该触发整张画布的修订冲突。
/// </summary>
internal sealed class VoiceEvidenceStore
{
    public sealed record Entry(Guid NodeId, string Speaker, VoiceAudioSource Source, string VoiceKey,
        bool TranscriptMatches, double SpeakerSimilarity, string Note, DateTimeOffset RecordedAt);

    private sealed class FileModel
    {
        public int FormatVersion { get; set; } = 1;
        public List<Entry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathFor(string canvasPath) => canvasPath + ".voice.json";

    public static List<Entry> Load(string canvasPath)
    {
        var path = PathFor(canvasPath);
        if (!File.Exists(path)) return new List<Entry>();
        try { return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Json)?.Entries ?? new List<Entry>(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { throw new InvalidDataException("声音证据文件无法读取：" + error.Message); }
    }

    /// <summary>同一节点同一说话人只保留最新一条：重测之后旧的判定不该继续影响成片闸门。</summary>
    public static List<Entry> Replace(string canvasPath, Guid nodeId, IEnumerable<Entry> entries)
    {
        var all = Load(canvasPath);
        all.RemoveAll(entry => entry.NodeId == nodeId);
        all.AddRange(entries);
        var path = PathFor(canvasPath);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel { Entries = all }, Json));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return all.Where(entry => entry.NodeId == nodeId).ToList();
    }
}
