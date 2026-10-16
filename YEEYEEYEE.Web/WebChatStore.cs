using System.Text.Json;
using Microsoft.Data.Sqlite;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

public sealed class WebChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public List<string> AttachmentIds { get; set; } = new();
    public string? TargetNodeId { get; set; }
    public string? TargetChapterId { get; set; }
    public string? Source { get; set; }
    public string? TargetNodeTitle { get; set; }
    public string Thinking { get; set; } = "";
    public string State { get; set; } = "completed";
    public string? Error { get; set; }
    public AiUsage? Usage { get; set; }
    public int ContextWindow { get; set; }
    public double? FirstTokenSeconds { get; set; }
    public double? DecodeSeconds { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AiUsageReport? UsageReport => Role == "assistant"
        ? AiUsageReport.From(Usage ?? AiUsage.Empty, ContextWindow, DecodeSeconds, FirstTokenSeconds)
        : null;

    public void ObserveOutput(double elapsedSeconds, bool thinking)
    {
        FirstTokenSeconds ??= elapsedSeconds;
        if (!thinking && elapsedSeconds > FirstTokenSeconds.Value)
            DecodeSeconds = elapsedSeconds - FirstTokenSeconds.Value;
    }

    public string Model { get; set; } = "";
    public List<AgentAction> Actions { get; set; } = new();
    public List<AgentAction> SkillActions { get; set; } = new();
    public string SkillState { get; set; } = "none";
    public string? SkillError { get; set; }
    public string? SkillIdempotencyKey { get; set; }
    public List<Guid> SkillJobIds { get; set; } = new();
    public AgentAsk? Ask { get; set; }
    public string Mode { get; set; } = "approval";
    public long? BaseRevision { get; set; }
    public string ActionState { get; set; } = "none";
    public string[] Hints { get; set; } = Array.Empty<string>();
    public string[] ActionErrors { get; set; } = Array.Empty<string>();
    public long? CommittedRevision { get; set; }
    public int CommittedActionCount { get; set; }
    /// <summary>
    /// 最后一次**成功预检**时那一批动作的指纹（见 WebChatApi.ActionFingerprint）。
    ///
    /// 为什么要有它：同一条消息的动作会在生成任务完成之后被追加（attach_asset），
    /// 而消息 ID 与画布修订都不变；只按那两个判据，「用户批准的那一版动作」与
    /// 「此刻真正要执行的动作」可以是两批不同的东西。提交时拿它与当前待执行动作比一次，
    /// 对不上就拒绝并提示重新预检——不靠前端把按钮灰掉。
    /// **动作一有变化就必须清空它**（追加、丢弃、撤销、重新预检失败），这样旧批准立刻失效。
    /// </summary>
    public string? PreviewFingerprint { get; set; }
    public byte[]? UndoCanvas { get; set; }
    public byte[]? CommittedEntityLibrary { get; set; }
}

public sealed class WebChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<Guid> JobIds { get; set; } = new();
    public string Title { get; set; } = "新会话";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WebChatMessage> Messages { get; set; } = new();
}

// 所有查询都同时绑定 owner 与 id；会话内容不依赖前端提交的历史或身份。
public sealed class WebChatStore
{
    private readonly string connectionString;
    private readonly object gate = new();
    public WebChatStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS chat_sessions(owner TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(owner,id)); CREATE TABLE IF NOT EXISTS chat_attachments(owner TEXT NOT NULL, session TEXT NOT NULL, id TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, data BLOB NOT NULL, PRIMARY KEY(owner,session,id));";
        command.ExecuteNonQuery();
        // 上一次进程异常退出的请求只能标为中断，不能当作已完成回复恢复。
        command.CommandText = "SELECT owner,id,payload FROM chat_sessions";
        var interrupted = new List<(string Owner, WebChatSession Session)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var session = JsonSerializer.Deserialize<WebChatSession>(reader.GetString(2))!;
                if (!session.Messages.Any(m => m.State == "streaming")) continue;
                foreach (var m in session.Messages.Where(m => m.State == "streaming")) { m.State = "interrupted"; m.Error = "服务重启，请重试"; }
                interrupted.Add((reader.GetString(0), session));
            }
        foreach (var item in interrupted) Save(item.Owner, item.Session);
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public List<WebChatSession> List(string owner)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM chat_sessions WHERE owner=$owner";
            cmd.Parameters.AddWithValue("$owner", owner);
            using var reader = cmd.ExecuteReader(); var list = new List<WebChatSession>();
            while (reader.Read()) list.Add(JsonSerializer.Deserialize<WebChatSession>(reader.GetString(0))!);
            return list.OrderByDescending(s => s.UpdatedAt).ToList();
        }
    }
    public WebChatSession? Get(string owner, string id)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM chat_sessions WHERE owner=$owner AND id=$id";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<WebChatSession>(payload) : null;
        }
    }
    public void Save(string owner, WebChatSession session)
    {
        lock (gate)
        {
            session.UpdatedAt = DateTimeOffset.UtcNow;
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO chat_sessions(owner,id,payload) VALUES($owner,$id,$payload) ON CONFLICT(owner,id) DO UPDATE SET payload=$payload";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$id", session.Id);
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session)); cmd.ExecuteNonQuery();
        }
    }
    public sealed record Attachment(string Id, string Name, string Type, byte[] Data);
    public Attachment? GetAttachment(string owner, string session, string id)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT name,type,data FROM chat_attachments WHERE owner=$owner AND session=$session AND id=$id";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$session", session); cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new Attachment(id, reader.GetString(0), reader.GetString(1), (byte[])reader[2]) : null;
        }
    }
    public Attachment SaveAttachment(string owner, string session, string name, string type, byte[] data)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chat_attachments WHERE owner=$owner AND session=$session";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$session", session);
            if (Convert.ToInt32(cmd.ExecuteScalar()) >= 32) throw new InvalidOperationException("每个会话最多32个附件");
            var id = Guid.NewGuid().ToString("N");
            cmd.CommandText = "INSERT INTO chat_attachments(owner,session,id,name,type,data) VALUES($owner,$session,$id,$name,$type,$data)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$type", type); cmd.Parameters.AddWithValue("$data", data);
            cmd.ExecuteNonQuery(); return new Attachment(id, name, type, data);
        }
    }
    public bool Delete(string owner, string id)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            using var transaction = db.BeginTransaction();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM chat_sessions WHERE owner=$owner AND id=$id";
            cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$id", id);
            var deleted = cmd.ExecuteNonQuery() > 0;
            cmd.CommandText = "DELETE FROM chat_attachments WHERE owner=$owner AND session=$id";
            cmd.ExecuteNonQuery();
            transaction.Commit();
            return deleted;
        }
    }
}
