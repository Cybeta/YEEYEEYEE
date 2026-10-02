using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using YEEYEEYEE.Web.Auth;

namespace YEEYEEYEE.Web;

/// <summary>
/// 编辑租约的存放处。**锁文件放在画布旁边**（`&lt;画布&gt;.edits.json`），由服务端进程读写，
/// 两端都通过 API 申请与查询。
///
/// 为什么是文件而不是纯内存：
/// · 服务重启后锁还在（TTL 内），不会因为一次滚动重启就把所有人放进来。
/// · 出问题时可以直接看文件，不用挂调试器。
///
/// 为什么仲裁权在服务端而不是直接用文件锁：桌面与网页不在同一台机器上时，
/// 本地文件锁管不到对方；这个文件只是**记录**，仲裁由这里的读写逻辑做。
///
/// 已知限制（本轮范围外）：桌面端还没接入，所以它现在不遵守这里的锁；
/// 画布改名会丢锁（键是画布路径）；用户登出不会立刻释放，最多留两分钟等 TTL。
/// </summary>
internal sealed class EditLeaseStore(string canvasPath, TimeSpan lifetime)
{
    /// <summary>默认两分钟。太短会让长任务频繁丢锁，太长会让崩溃的人把节点占太久。</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private readonly string path = canvasPath + ".edits.json";
    private readonly object gate = new();

    /// <summary>
    /// 这份文件是**给人看的**（排查「谁锁着这张画布」时直接打开就行），所以中文不转义成 \uXXXX。
    /// 用 <c>Create(UnicodeRanges.All)</c> 而不是 UnsafeRelaxedJsonEscaping：前者仍会转义 HTML 敏感字符
    /// ——与仓库里给人和设置看的其他 JSON 一致（见 Skills / SiteCatalog / AiProviderSettings）。
    /// </summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public TimeSpan Lifetime => lifetime;

    private static string NameOf(WebUser user) =>
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;

    /// <summary>
    /// 读锁文件。**损坏时按「没有锁」处理**：宁可短暂放两个人进来，也不要让整张画布永远无法编辑。
    /// 这份文件坏了是自愈的——下一次保存会把它覆盖成合法内容。
    /// </summary>
    private List<EditLease> Load()
    {
        if (!File.Exists(path)) return new List<EditLease>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<EditLease>>(File.ReadAllText(path), Options);
            return parsed is null
                ? new List<EditLease>()
                : parsed.Where(lease => lease is not null && lease.LeaseId != Guid.Empty && EditScope.IsValid(lease.Scope)).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new List<EditLease>();
        }
    }

    /// <summary>原子替换写入。返回是否成功——写不进去时调用方必须如实报错，不能假装拿到了锁。</summary>
    private bool Save(List<EditLease> leases)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(leases, Options), Encoding.UTF8);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>清掉过期锁，返回是否有改动。每次读写都先做这件事，所以不需要后台清理线程。</summary>
    private static bool Purge(List<EditLease> leases, DateTimeOffset now) =>
        leases.RemoveAll(lease => lease.ExpiresAt <= now) > 0;

    public IReadOnlyList<EditLease> List()
    {
        lock (gate)
        {
            var leases = Load();
            var now = DateTimeOffset.UtcNow;
            // 顺带回收过期锁；这里写盘失败不算错——查询本身仍然可以如实回答。
            if (Purge(leases, now)) Save(leases);
            return leases.OrderBy(lease => lease.AcquiredAt).ThenBy(lease => lease.LeaseId).ToList();
        }
    }

    /// <summary>
    /// 申请。同一目标重复申请是**幂等**的（刷新过期时间即可）——连点两下、网络重试都不该把自己挡住。
    /// <paramref name="client"/> 为 null 表示调用方没声明来源端：新建时按网页端算，刷新时**保留原值**
    /// （否则一次没带 client 的心跳就会把「某某（桌面端）」悄悄改成网页端）。
    /// </summary>
    public EditLeaseResult Acquire(string scope, Guid? targetId, WebUser user, string? client, bool force)
    {
        lock (gate)
        {
            var leases = Load();
            var now = DateTimeOffset.UtcNow;
            Purge(leases, now);

            var existing = leases.FindIndex(lease => lease.LeaseId != Guid.Empty &&
                lease.UserId == user.Id && lease.Scope == scope && lease.TargetId == targetId);
            if (existing >= 0)
            {
                var refreshed = leases[existing] with
                {
                    ExpiresAt = now + lifetime,
                    Client = client ?? leases[existing].Client,
                    DisplayName = NameOf(user)
                };
                leases[existing] = refreshed;
                return Save(leases)
                    ? EditLeaseResult.Ok(refreshed)
                    : EditLeaseResult.Fail(EditLeaseStatus.StorageFailed, "锁记录写不进去（磁盘或权限问题）");
            }

            var resolvedClient = client ?? EditClient.Web;

            // 别人持有的冲突锁才算冲突；自己持有的不算——申请整棵树时会把自己的节点锁吸收掉。
            var conflicts = leases.Where(lease => lease.UserId != user.Id && Conflicts(lease, scope, targetId)).ToList();
            if (conflicts.Count > 0 && !force)
            {
                var holder = conflicts.OrderBy(lease => lease.AcquiredAt).First();
                return EditLeaseResult.Fail(EditLeaseStatus.Conflict,
                    $"{EditLeaseResult.Describe(holder)}正在编辑，请等他保存或让管理员接管", holder);
            }

            if (conflicts.Count > 0)
            {
                var victims = conflicts.ToList();
                leases.RemoveAll(lease => victims.Contains(lease));
                // 申请整棵树时，自己原有的节点锁是它的子集，一并收掉，免得留下永远没人续期的僵尸锁。
                if (scope == EditScope.Tree) leases.RemoveAll(lease => lease.UserId == user.Id);
                var taken = new EditLease(Guid.NewGuid(), scope, targetId, user.Id, NameOf(user), resolvedClient, now, now + lifetime);
                leases.Add(taken);
                return Save(leases)
                    ? EditLeaseResult.Ok(taken, victims)
                    : EditLeaseResult.Fail(EditLeaseStatus.StorageFailed, "锁记录写不进去（磁盘或权限问题）");
            }

            if (scope == EditScope.Tree) leases.RemoveAll(lease => lease.UserId == user.Id);
            var lease = new EditLease(Guid.NewGuid(), scope, targetId, user.Id, NameOf(user), resolvedClient, now, now + lifetime);
            leases.Add(lease);
            return Save(leases)
                ? EditLeaseResult.Ok(lease)
                : EditLeaseResult.Fail(EditLeaseStatus.StorageFailed, "锁记录写不进去（磁盘或权限问题）");
        }
    }

    /// <summary>范围冲突规则：树锁挡一切；申请树锁时被任何锁挡；否则只有同一目标才挡。</summary>
    private static bool Conflicts(EditLease existing, string scope, Guid? targetId) =>
        existing.Scope == EditScope.Tree
        || scope == EditScope.Tree
        || existing.TargetId == targetId;

    /// <summary>心跳续期。找不到 = 已被释放或过期，调用方应当停下编辑重新申请，而不是硬写。</summary>
    public EditLeaseResult Renew(Guid leaseId, WebUser user)
    {
        lock (gate)
        {
            var leases = Load();
            var now = DateTimeOffset.UtcNow;
            var index = leases.FindIndex(lease => lease.LeaseId == leaseId);
            if (index < 0)
            {
                Purge(leases, now);
                Save(leases);
                return EditLeaseResult.Fail(EditLeaseStatus.NotFound, "锁不存在，可能已被管理员接管或已释放");
            }
            if (leases[index].ExpiresAt <= now)
            {
                var gone = leases[index];
                leases.RemoveAt(index);
                Save(leases);
                return EditLeaseResult.Fail(EditLeaseStatus.Expired, "锁已过期（可能中间断过心跳），请重新申请", gone);
            }
            if (leases[index].UserId != user.Id)
                return EditLeaseResult.Fail(EditLeaseStatus.NotHolder, "这个锁不是你持有的", leases[index]);

            var renewed = leases[index] with { ExpiresAt = now + lifetime, DisplayName = NameOf(user) };
            leases[index] = renewed;
            return Save(leases)
                ? EditLeaseResult.Ok(renewed)
                : EditLeaseResult.Fail(EditLeaseStatus.StorageFailed, "锁记录写不进去（磁盘或权限问题）");
        }
    }

    /// <summary>释放。<paramref name="force"/> 由**接口层**确认调用者是管理员之后才传 true。</summary>
    public EditLeaseResult Release(Guid leaseId, WebUser? user, bool force)
    {
        lock (gate)
        {
            var leases = Load();
            var now = DateTimeOffset.UtcNow;
            Purge(leases, now);
            var lease = leases.FirstOrDefault(item => item.LeaseId == leaseId);
            if (lease is null)
            {
                Save(leases);
                return EditLeaseResult.Fail(EditLeaseStatus.NotFound, "锁不存在或已过期");
            }
            if (!force && (user is null || lease.UserId != user.Id))
                return EditLeaseResult.Fail(EditLeaseStatus.NotHolder, "这个锁不是你持有的", lease);

            leases.Remove(lease);
            return Save(leases)
                ? EditLeaseResult.Ok(lease)
                : EditLeaseResult.Fail(EditLeaseStatus.StorageFailed, "锁记录写不进去（磁盘或权限问题）");
        }
    }
}
