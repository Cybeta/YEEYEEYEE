using System.Text.Json;
using DreamForge.Core;
using Microsoft.Data.Sqlite;

namespace DreamForge.Host;

public interface IJobStore : IDisposable
{
    void Initialize();
    void Save(ExecutionResult result);
    IReadOnlyList<ExecutionResult> LoadAll();

    /// <summary>清空全部任务记录，用于本地维护与空间清理。</summary>
    void Clear();
}

public sealed class SqliteJobStore : IJobStore
{
    private readonly string connectionString;
    private readonly object gate = new();
    private bool disposed;

    public SqliteJobStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("SQLite 数据库路径不能为空", nameof(databasePath));
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = false }.ToString();
    }

    public void Initialize()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS jobs (
                    job_id TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    invocation_id TEXT NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    progress_percent INTEGER NOT NULL,
                    error_code TEXT NULL,
                    error_message TEXT NULL,
                    external_task_id TEXT NULL,
                    outputs_json TEXT NOT NULL,
                    inputs_json TEXT NOT NULL DEFAULT '{}',
                    tool TEXT NOT NULL DEFAULT '',
                    capability INTEGER NOT NULL DEFAULT 0,
                    channel TEXT NOT NULL DEFAULT '',
                    attempt INTEGER NOT NULL DEFAULT 1,
                    retry_of_job_id TEXT NULL,
                    root_job_id TEXT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(user_id, idempotency_key)
                );
                CREATE INDEX IF NOT EXISTS ix_jobs_user_updated ON jobs(user_id, updated_at DESC);
                """;
            command.ExecuteNonQuery();
            AddColumnIfMissing(connection, "external_task_id", "TEXT NULL");
            AddColumnIfMissing(connection, "inputs_json", "TEXT NOT NULL DEFAULT '{}'");
            AddColumnIfMissing(connection, "tool", "TEXT NOT NULL DEFAULT ''");
            AddColumnIfMissing(connection, "capability", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "channel", "TEXT NOT NULL DEFAULT ''");
            AddColumnIfMissing(connection, "attempt", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "retry_of_job_id", "TEXT NULL");
            AddColumnIfMissing(connection, "root_job_id", "TEXT NULL");
        }
    }

    public void Save(ExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.JobId == Guid.Empty || result.UserId == Guid.Empty) throw new ArgumentException("Job 快照标识不能为空", nameof(result));
        lock (gate)
        {
            ThrowIfDisposed();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO jobs(job_id,user_id,invocation_id,idempotency_key,state,progress_percent,error_code,error_message,external_task_id,outputs_json,inputs_json,tool,capability,channel,attempt,retry_of_job_id,root_job_id,updated_at)
                VALUES($jobId,$userId,$invocationId,$key,$state,$progress,$errorCode,$errorMessage,$externalTaskId,$outputs,$inputs,$tool,$capability,$channel,$attempt,$retryOf,$rootJobId,$updatedAt)
                ON CONFLICT(job_id) DO UPDATE SET
                    state=excluded.state,
                    progress_percent=excluded.progress_percent,
                    error_code=excluded.error_code,
                    error_message=excluded.error_message,
                    external_task_id=excluded.external_task_id,
                    outputs_json=excluded.outputs_json,
                    inputs_json=excluded.inputs_json,
                    tool=excluded.tool,
                    capability=excluded.capability,
                    channel=excluded.channel,
                    attempt=excluded.attempt,
                    retry_of_job_id=excluded.retry_of_job_id,
                    root_job_id=excluded.root_job_id,
                    updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$jobId", result.JobId.ToString("D"));
            command.Parameters.AddWithValue("$userId", result.UserId.ToString("D"));
            command.Parameters.AddWithValue("$invocationId", result.InvocationId.ToString("D"));
            command.Parameters.AddWithValue("$key", result.IdempotencyKey);
            command.Parameters.AddWithValue("$state", (int)result.State);
            command.Parameters.AddWithValue("$progress", result.ProgressPercent);
            command.Parameters.AddWithValue("$errorCode", (object?)result.ErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$errorMessage", (object?)result.ErrorMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$externalTaskId", (object?)result.ExternalTaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("$outputs", JsonSerializer.Serialize(result.Outputs));
            command.Parameters.AddWithValue("$inputs", JsonSerializer.Serialize(result.Inputs));
            command.Parameters.AddWithValue("$tool", result.Tool);
            command.Parameters.AddWithValue("$capability", (int)result.Capability);
            command.Parameters.AddWithValue("$channel", result.Channel);
            command.Parameters.AddWithValue("$attempt", result.Attempt < 1 ? 1 : result.Attempt);
            command.Parameters.AddWithValue("$retryOf", (object?)result.RetryOfJobId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$rootJobId", result.RootJobId == Guid.Empty ? result.JobId.ToString("D") : result.RootJobId.ToString("D"));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ExecutionResult> LoadAll()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT job_id,user_id,invocation_id,idempotency_key,state,progress_percent,error_code,error_message,external_task_id,outputs_json,inputs_json,tool,capability,channel,attempt,retry_of_job_id,root_job_id FROM jobs ORDER BY updated_at ASC";
            using var reader = command.ExecuteReader();
            var results = new List<ExecutionResult>();
            while (reader.Read())
            {
                var outputs = JsonSerializer.Deserialize<List<AssetRef>>(reader.GetString(9)) ?? [];
                var inputs = reader.IsDBNull(10)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(10)) ?? new Dictionary<string, JsonElement>();
                var jobId = Guid.Parse(reader.GetString(0));
                results.Add(new ExecutionResult
                {
                    JobId = jobId, UserId = Guid.Parse(reader.GetString(1)), InvocationId = Guid.Parse(reader.GetString(2)),
                    IdempotencyKey = reader.GetString(3), State = (JobState)reader.GetInt32(4), ProgressPercent = reader.GetInt32(5),
                    ErrorCode = reader.IsDBNull(6) ? null : reader.GetString(6), ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7), ExternalTaskId = reader.IsDBNull(8) ? null : reader.GetString(8), Outputs = outputs, Inputs = inputs,
                    Tool = reader.IsDBNull(11) ? string.Empty : reader.GetString(11), Capability = (Capability)reader.GetInt32(12), Channel = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                    Attempt = reader.IsDBNull(14) ? 1 : Math.Max(1, reader.GetInt32(14)),
                    RetryOfJobId = reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
                    RootJobId = reader.IsDBNull(16) ? jobId : Guid.Parse(reader.GetString(16))
                });
            }
            return results;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM jobs;";
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
    }

    private SqliteConnection Open()
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string columnName, string definition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name = $name";
        command.Parameters.AddWithValue("$name", columnName);
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) return;
        command.Parameters.Clear();
        command.CommandText = $"ALTER TABLE jobs ADD COLUMN {columnName} {definition}";
        command.ExecuteNonQuery();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(SqliteJobStore));
    }
}
