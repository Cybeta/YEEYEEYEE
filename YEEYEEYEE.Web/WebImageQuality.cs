using System.Text.Json;
using Microsoft.Data.Sqlite;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

/// <summary>
/// Web 侧判档的规则版本。档位带、评分解析与客观坏图判据都来自 Desktop.Shared，
/// 这里只标一个版本，便于以后对比「这条结果按哪一版规则算出来的」。
/// </summary>
public static class WebImageQuality
{
    public const string RuleVersion = "desktop-quality-v1";
    public static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(120);

    public const string StatusGraded = "graded";
    public const string StatusSkipped = "skipped";
    public const string StatusFailed = "failed";

    public const string SourceModel = "model";
    public const string SourceTechnical = "technical";

    public const string ErrorModelUnavailable = "MODEL_UNAVAILABLE";
    public const string ErrorModelTimeout = "MODEL_TIMEOUT";
    public const string ErrorModelRequest = "MODEL_REQUEST_FAILED";
    public const string ErrorModelReply = "MODEL_REPLY_INVALID";
    public const string ErrorImageUnavailable = "IMAGE_UNAVAILABLE";

    /// <summary>
    /// 这一格是否已经有结论、不该再花钱。判过档的一律跳过；失败与跳过只有在用户
    /// 明确点重试（<paramref name="retry"/>）时才允许重来——重复请求因此不会重复付费。
    /// </summary>
    public static bool AlreadyHandled(IEnumerable<WebQualityRecord> records, bool retry) =>
        records.Any(record => record.OutputIndex == 0
            && (record.Status == StatusGraded
                || (!retry && (record.Status is StatusFailed or StatusSkipped))));

    /// <summary>
    /// 判档要一个能读图的模型。这里只回答「现在能不能判」，不碰密钥内容。
    /// </summary>
    public static (bool Ok, string Message) Availability(AiProviderConfig config)
    {
        if (config.UseLocalProvider || string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Model))
            return (false, "没有可用的读图模型：请在 Web 设置中选择一个模型，并勾选「支持图片输入」。");
        if (!config.SupportsImageInput)
            return (false, "当前模型没有开启「支持图片输入」，不能判档；请换一个能读图的模型，或这次跳过判档。");
        if (config.ApiKeyUnreadable)
            return (false, "当前模型密钥无法解密，暂时不能判档；请管理员重新填写密钥，或这次跳过判档。");
        return (true, string.Empty);
    }
}

/// <summary>
/// 一格待判的图：任务与产物下标是**身份**，分数回来时按这个身份落位，
/// 不按前端数组顺序或模型回复顺序猜。
/// </summary>
public sealed record WebQualityCandidate(
    Guid JobId,
    int OutputIndex,
    string Path,
    int ExpectedShortSide,
    string Prompt,
    string Negative,
    string NodeContext = "");

/// <summary>
/// 一条与「任务 + 产物下标」绑定的判档结果。
///
/// 状态只有三种，且互相区分：<c>graded</c>（含本地坏图判出的白档）、
/// <c>skipped</c>（用户明确跳过）、<c>failed</c>（模型不可用 / 超时 / 返回不可用）。
/// **没有记录**是第四种，表示没判过——界面不能把它画成白档。
/// </summary>
public sealed class WebQualityRecord
{
    public Guid JobId { get; init; }
    public int OutputIndex { get; init; }
    public string Status { get; init; } = WebImageQuality.StatusFailed;
    public string? Source { get; init; }
    public string? Tier { get; init; }
    public int? Score { get; init; }
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyList<string> NegativeHits { get; init; } = Array.Empty<string>();
    public string RuleVersion { get; init; } = WebImageQuality.RuleVersion;
    public string? ErrorCode { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 判档的执行层：先本地筛客观坏图，再按同一组提示词把剩下的图交给模型。
///
/// 与 Desktop 的 <see cref="ImageQualityRunner"/> 同一套顺序与规则，区别只在输入形态：
/// Web 没有 NodeImageBatch，一格是「任务 + 产物下标」。规则本身（白档、分数带、裂纹、
/// 解析容错）全部复用共享实现，不在这里另写一份。
/// </summary>
public static class WebQualityRunner
{
    public static async Task<IReadOnlyList<WebQualityRecord>> RunAsync(
        IReadOnlyList<WebQualityCandidate> candidates,
        Func<string, int, ImageFacts> probe,
        Func<IAiImageJsonCompleter?> completerFactory,
        string modelLabel,
        string modelUnavailableReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(completerFactory);

        var records = new Dictionary<(Guid JobId, int OutputIndex), WebQualityRecord>();
        var judgeable = new List<WebQualityCandidate>();

        foreach (var candidate in candidates)
        {
            var facts = probe(candidate.Path, candidate.ExpectedShortSide);
            if (TechnicalScreening.Screen(facts) is { } broken)
                records[(candidate.JobId, candidate.OutputIndex)] = Graded(candidate, broken, WebImageQuality.SourceTechnical, null);
            else
                judgeable.Add(candidate);
        }

        if (judgeable.Count > 0)
        {
            var completer = completerFactory();
            if (completer is null)
            {
                foreach (var candidate in judgeable)
                    records[(candidate.JobId, candidate.OutputIndex)] = Failed(candidate, WebImageQuality.ErrorModelUnavailable,
                        modelUnavailableReason, modelLabel);
            }
            else
            {
                // 同一段提示词与负面词的图合成一组：模型要说「这一批 N 张」，
                // 组内按发送顺序编号，回来时再按这个编号映射回任务身份。
                foreach (var group in judgeable.GroupBy(item => new { item.Prompt, item.Negative, item.NodeContext }))
                {
                    var loaded = new List<(WebQualityCandidate Candidate, string Image)>();
                    foreach (var candidate in group)
                    {
                        var attachment = AgentAttachmentLoader.Load(candidate.Path);
                        if (attachment.Attachment is null)
                        {
                            records[(candidate.JobId, candidate.OutputIndex)] = Failed(candidate, WebImageQuality.ErrorImageUnavailable,
                                "这张图读不出来，没能送去判档", modelLabel);
                            continue;
                        }
                        loaded.Add((candidate, attachment.Attachment.Payload));
                    }
                    if (loaded.Count == 0) continue;

                    string reply;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(WebImageQuality.ModelTimeout);
                    try
                    {
                        reply = await completer.CompleteJsonWithImagesAsync(
                            ImageQualityJudge.SystemPrompt,
                            ImageQualityJudge.BuildUserPrompt(group.Key.NodeContext, group.Key.Prompt, group.Key.Negative, loaded.Count),
                            loaded.Select(item => item.Image).ToList(),
                            timeout.Token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        MarkGroupFailed(loaded, WebImageQuality.ErrorModelTimeout,
                            "判档超时（模型没有在限定时间内返回），图不受影响");
                        continue;
                    }
                    catch (Exception)
                    {
                        MarkGroupFailed(loaded, WebImageQuality.ErrorModelRequest,
                            "判档请求失败（模型没有返回可用结果），图不受影响");
                        continue;
                    }

                    var parsed = QualityJudgement.ParseScores(reply, loaded.Count);
                    if (parsed.Scores is null)
                    {
                        MarkGroupFailed(loaded, WebImageQuality.ErrorModelReply,
                            "模型返回的评分不符合约定（需要每张 0–10 的整数分），这次不落档位");
                        continue;
                    }

                    var grades = QualityJudgement.ToGrades(Enumerable.Range(0, loaded.Count).ToList(), parsed.Scores);
                    foreach (var (index, quality) in grades)
                    {
                        if (index < 0 || index >= loaded.Count) continue;
                        var candidate = loaded[index].Candidate;
                        records[(candidate.JobId, candidate.OutputIndex)] = Graded(candidate, quality, WebImageQuality.SourceModel, modelLabel);
                    }
                    for (var index = 0; index < loaded.Count; index++)
                    {
                        var candidate = loaded[index].Candidate;
                        if (records.ContainsKey((candidate.JobId, candidate.OutputIndex))) continue;
                        records[(candidate.JobId, candidate.OutputIndex)] = Failed(candidate, WebImageQuality.ErrorModelReply,
                            "模型没有给出这一张的分数，这次不落档位", modelLabel);
                    }
                }
            }
        }

        return candidates
            .Select(candidate => records.TryGetValue((candidate.JobId, candidate.OutputIndex), out var record)
                ? record
                : Failed(candidate, WebImageQuality.ErrorModelReply, "没有拿到这一张的判档结果", modelLabel))
            .ToList();

        void MarkGroupFailed(IEnumerable<(WebQualityCandidate Candidate, string Image)> items, string code, string reason)
        {
            foreach (var (candidate, _) in items)
                records[(candidate.JobId, candidate.OutputIndex)] = Failed(candidate, code, reason, modelLabel);
        }
    }

    private static WebQualityRecord Graded(WebQualityCandidate candidate, SlotQuality quality, string source, string? model) => new()
    {
        JobId = candidate.JobId,
        OutputIndex = candidate.OutputIndex,
        Status = WebImageQuality.StatusGraded,
        Source = source,
        Tier = quality.Tier.ToString().ToLowerInvariant(),
        Score = quality.Score == SlotQuality.NoScore ? null : quality.Score,
        Reason = quality.Reason,
        NegativeHits = quality.NegativeHits,
        Model = model
    };

    private static WebQualityRecord Failed(WebQualityCandidate candidate, string code, string reason, string? model) => new()
    {
        JobId = candidate.JobId,
        OutputIndex = candidate.OutputIndex,
        Status = WebImageQuality.StatusFailed,
        Reason = reason,
        ErrorCode = code,
        Model = model
    };
}

/// <summary>
/// 判档结果落盘。与任务库分开一个文件，按「任务 + 产物下标」主键存；
/// 产物本身没有判档结果时就是没有记录，不拿默认档补上。
/// </summary>
internal sealed class WebJobQualityStore
{
    private readonly string connectionString;
    private readonly object gate = new();

    public WebJobQualityStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS job_quality(
                job_id TEXT NOT NULL,
                output_index INTEGER NOT NULL,
                payload TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(job_id, output_index)
            );
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<WebQualityRecord> Get(Guid jobId)
    {
        lock (gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT payload FROM job_quality WHERE job_id=$job ORDER BY output_index";
            command.Parameters.AddWithValue("$job", jobId.ToString("D"));
            using var reader = command.ExecuteReader();
            var records = new List<WebQualityRecord>();
            while (reader.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize<WebQualityRecord>(reader.GetString(0)) is { } record)
                        records.Add(record);
                }
                catch (JsonException) { }
            }
            return records;
        }
    }

    public void Save(IEnumerable<WebQualityRecord> records)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            foreach (var record in records)
            {
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO job_quality(job_id,output_index,payload,updated_at)
                    VALUES($job,$index,$payload,$updated)
                    ON CONFLICT(job_id,output_index) DO UPDATE SET
                        payload=excluded.payload,
                        updated_at=excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$job", record.JobId.ToString("D"));
                command.Parameters.AddWithValue("$index", record.OutputIndex);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record));
                command.Parameters.AddWithValue("$updated", record.UpdatedAt.ToString("O"));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }
}

/// <summary>
/// 判档的 Web 服务层：把当前用户那批成功候选图交给 <see cref="WebQualityRunner"/>，
/// 结果按「任务 + 产物下标」保存。模型调用全部在这里（服务端），前端只传任务 ID。
/// </summary>
internal sealed class WebImageQualityService(
    WebJobQualityStore store,
    IHttpClientFactory httpFactory,
    string assetDirectory)
{
    /// <summary>
    /// 判档前的任务资格校验。**归属与当前画布先于一切**：不满足时按「不存在」回答，
    /// 不泄露别人的任务是否存在；其余拒绝各自带一个可读的业务码。
    /// </summary>
    public static (int Status, string Code, string Message)? ValidateJob(
        Job? job, Guid userId, bool inCurrentCanvas, bool candidate)
    {
        if (job is null || job.UserId != userId || !inCurrentCanvas)
            return (404, "JOB_NOT_FOUND", "任务不存在");
        if (!candidate) return (409, "NOT_CANDIDATE_JOB", "这条任务不是候选批次");
        if (job.State != JobState.Succeeded) return (409, "JOB_NOT_READY", "任务还没有成功结果");
        if (job.Capability != Capability.TextToImage) return (409, "NOT_IMAGE_JOB", "这条任务不是出图任务");
        if (job.Outputs.Count == 0) return (409, "JOB_NOT_READY", "任务没有可判的图片产物");
        return null;
    }
    /// <summary>
    /// 一次只跑一批判档。同一批被重复请求时，后来者等前一次完成后再查一次结果，
    /// 已经判过的图不会再花第二次模型调用。
    /// </summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<(IReadOnlyList<WebQualityRecord> Results, string ModelMessage)> GradeAsync(
        IReadOnlyList<Job> jobs, bool retry, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var candidates = new List<WebQualityCandidate>();
            var failures = new List<WebQualityRecord>();

            foreach (var job in jobs)
            {
                var existing = store.Get(job.JobId);
                // 已经判过的不再花钱；失败/跳过也只有用户明确重试时才再判。
                if (WebImageQuality.AlreadyHandled(existing, retry)) continue;

                if (job.Outputs.Count == 0 ||
                    !job.Outputs[0].Role.Equals("image", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(Failed(job, 0, WebImageQuality.ErrorImageUnavailable, "这个任务没有可判的图片产物"));
                    continue;
                }
                var path = WebSkillJobApi.ResolveAsset(job.Outputs[0].Ref, assetDirectory);
                if (path is null)
                {
                    failures.Add(Failed(job, 0, WebImageQuality.ErrorImageUnavailable, "任务产物文件不可读取"));
                    continue;
                }
                candidates.Add(new WebQualityCandidate(
                    job.JobId,
                    0,
                    path,
                    ExpectedShortSide(job),
                    InputText(job, "prompt"),
                    InputText(job, "negativePrompt")));
            }

            var config = WebSettingsApi.LoadModels();
            var availability = WebImageQuality.Availability(config);
            var modelLabel = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Model : config.DisplayName;
            IAiImageJsonCompleter? Completer()
            {
                if (!availability.Ok) return null;
                return new OpenAiCompatibleProvider(config, httpFactory.CreateClient("chat"));
            }

            IReadOnlyList<WebQualityRecord> produced = candidates.Count == 0
                ? Array.Empty<WebQualityRecord>()
                : await WebQualityRunner.RunAsync(candidates, WebImageProbe.Measure, Completer,
                    modelLabel, availability.Message, cancellationToken);

            if (failures.Count > 0 || produced.Count > 0)
                store.Save(failures.Concat(produced));

            var results = new List<WebQualityRecord>();
            foreach (var job in jobs) results.AddRange(store.Get(job.JobId));
            return (results, availability.Ok ? string.Empty : availability.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 用户明确跳过判档：只把**尚未判过**的那几格记成 skipped。
    /// 已经有真实档位的（包括本地坏图判出的白档）保持原样，不被跳过覆盖。
    /// </summary>
    public IReadOnlyList<WebQualityRecord> Skip(IReadOnlyList<Job> jobs)
    {
        var skipped = new List<WebQualityRecord>();
        foreach (var job in jobs)
        {
            var existing = store.Get(job.JobId);
            if (existing.Any(record => record.OutputIndex == 0 && record.Status == WebImageQuality.StatusGraded)) continue;
            if (existing.Any(record => record.OutputIndex == 0 && record.Status == WebImageQuality.StatusSkipped)) continue;
            skipped.Add(new WebQualityRecord
            {
                JobId = job.JobId,
                OutputIndex = 0,
                Status = WebImageQuality.StatusSkipped,
                Reason = "你选择了跳过判档"
            });
        }
        if (skipped.Count > 0) store.Save(skipped);

        var results = new List<WebQualityRecord>();
        foreach (var job in jobs) results.AddRange(store.Get(job.JobId));
        return results;
    }

    private static int ExpectedShortSide(Job job)
    {
        var width = InputInt(job, "width");
        var height = InputInt(job, "height");
        return width > 0 && height > 0 ? Math.Min(width, height) : 0;
    }

    private static string InputText(Job job, string name) =>
        job.Inputs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int InputInt(Job job, string name) =>
        job.Inputs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static WebQualityRecord Failed(Job job, int index, string code, string reason) => new()
    {
        JobId = job.JobId,
        OutputIndex = index,
        Status = WebImageQuality.StatusFailed,
        ErrorCode = code,
        Reason = reason
    };
}
