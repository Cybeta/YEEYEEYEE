using YEEYEEYEE.Desktop;

foreach (var existing in new[] { false, true })
foreach (var fault in new[] { "none", "restore-write", "restore-delete", "restore-canvas" })
    Run(existing, fault);
Console.WriteLine("G6-V2: 8/8 scenarios passed");

static void Run(bool existing, string fault)
{
    var root = Path.Combine(Path.GetTempPath(), "df-g6v2-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var priorCanvas = Environment.GetEnvironmentVariable("YEEYEEYEE_CANVAS_DIR");
    var priorAsset = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
    var priorConfig = Environment.GetEnvironmentVariable("YEEYEEYEE_CONFIG");
    FileStream? lockStream = null;
    try
    {
        Environment.SetEnvironmentVariable("YEEYEEYEE_CANVAS_DIR", Path.Combine(root, "canvases"));
        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", Path.Combine(root, "assets"));
        Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG", Path.Combine(root, "config.json"));
        Directory.CreateDirectory(CanvasLibrary.Directory);
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        AppPaths.UseProject(ProjectContext.Create(root, "isolated"));
        byte[]? historicalLibrary = null;
        if (existing)
        {
            var old = Entity("old");
            Check(ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { old }, out _, out _), out var error), error);
            historicalLibrary = File.ReadAllBytes(ProjectLibrary.FilePath);
            old.Core = "newer library content";
            Check(ProjectLibrary.TrySave(ProjectLibrary.Upsert(new[] { old }, out _, out _), out error), error);
        }
        var beforeLibrary = existing ? File.ReadAllBytes(ProjectLibrary.FilePath) : null;
        Check(!existing || !historicalLibrary!.SequenceEqual(beforeLibrary!), "history A and migration baseline B must differ");
        var paths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var entity = Entity("local-" + i);
            var canvas = new WorkflowCanvasState();
            canvas.Entities.Add(ProjectEntityScope.CloneEntity(entity));
            paths.Add(CanvasLibrary.Save(new RecentCanvasState("g6-" + i, 1, "", "", 512, 512, 20, 7, "", canvas)
                { FormatVersion = CanvasFormat.Current }, null));
        }
        var beforeCanvas = paths.ToDictionary(path => path, File.ReadAllBytes);
        Check(CanvasLibrary.TryLoad(paths[0], out var loaded) && loaded?.Canvas != null, "load current");
        var current = loaded!.Canvas!;
        // 在第一份画布写完后锁定恢复目标，第二份写完后注入异常；不依赖画布枚举的具体顺序。
        var writes = 0;
        string? changedPath = null;
        var writtenPaths = new List<string>();
        // 在本轮备份创建后预置同秒不同字节的旧备份，排序确定性领先本轮备份。
        string? decoy = null;
        string? activeBackup = null;
        var backupsBeforeApply = ProjectLibrary.Backups().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outcome = ProjectEntityMigration.Apply(current, paths[0], (path, number) =>
        {
            writes = number;
            writtenPaths.Add(path);
            if (number == 2) throw new IOException("injected after partial canvas write");
            if (number != 1) return;
            changedPath = path;
            Check(!File.ReadAllBytes(path).SequenceEqual(beforeCanvas[path]), "first write must reach disk");
            Check(ProjectLibrary.Load().Entities.Count == (existing ? 4 : 3), "library must be written");
            if (existing)
            {
                // TrySave 本身也会备份，故本轮新增的 B 备份可能不止一份。
                activeBackup = ProjectLibrary.Backups().Where(p => !backupsBeforeApply.Contains(p) && File.ReadAllBytes(p).SequenceEqual(beforeLibrary!)).First();
                var second = Path.GetFileName(activeBackup).Substring("entities-".Length, "yyyyMMdd-HHmmss".Length);
                decoy = Path.Combine(ProjectLibrary.BackupDirectory, $"entities-{second}-zzzzzzzz.json");
                File.WriteAllBytes(decoy, historicalLibrary!);
                Check(string.CompareOrdinal(decoy, activeBackup) > 0, "old history must sort ahead of active migration backup");
            }
            if (fault == "restore-write")
            {
                // Windows 上禁止共享删除/写入，恢复复制将失败；锁释放后可以重试。
                lockStream = new FileStream(ProjectLibrary.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else if (fault == "restore-delete" && !existing)
            {
                lockStream = new FileStream(ProjectLibrary.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else if (fault == "restore-canvas")
            {
                lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            // 第二次真实 CanvasSaveService 写入之后抛 IOException，覆盖已写两份再整批回滚。
        });
        Check(writes == 2 && changedPath != null, $"must fail after two written canvases: writes={writes}; {outcome.Message}; {string.Join(" | ", outcome.Errors)}");
        Check(!outcome.Succeeded && outcome.Migrated == 0, "failure report");
        if (existing)
        {
            Check(outcome.LibraryBackup != decoy && !backupsBeforeApply.Contains(outcome.LibraryBackup!) &&
                ProjectLibrary.Backups().Contains(outcome.LibraryBackup!), "report must identify a backup created by this Apply, not older history");
            Check(File.ReadAllBytes(outcome.LibraryBackup!).SequenceEqual(beforeLibrary!), "active backup must retain pre-migration bytes");
            Check(File.ReadAllBytes(decoy!).SequenceEqual(historicalLibrary!), "unrelated historical backup must not change");
        }
        Check(outcome.Errors.Any(e => e.Contains("injected after partial canvas write")), "original failure must be reported");
        var rollbackShouldFail = fault is "restore-write" or "restore-canvas" || (fault == "restore-delete" && !existing);
        Check(outcome.RollbackComplete != rollbackShouldFail, "rollback status");
        Check(outcome.WrittenCanvases.Count == (rollbackShouldFail ? 2 : 0), "incomplete rollback must retain written paths");
        Check(outcome.Message.Contains(rollbackShouldFail ? "未完全成功" : "已回滚"), "rollback text");
        Check(writtenPaths.All(p => File.ReadAllBytes(p).SequenceEqual(beforeCanvas[p]) == (fault != "restore-canvas" || p != changedPath)),
            "canvas restoration must reflect fault");
        Check(current.Entities.All(e => !e.ManagedByProject), "memory restored");
        Check(CanvasBackup.ListFor(changedPath!).Any(p => File.ReadAllBytes(p).SequenceEqual(beforeCanvas[changedPath!])), "original canvas backup retained");
        if (rollbackShouldFail)
        {
            Check(outcome.Errors.Any(e => e.Contains(fault == "restore-canvas" ? "恢复失败" : existing ? "恢复项目库失败" : "删除本次新建的项目库失败")), "rollback error surfaced");
            if (fault != "restore-canvas") Check(File.Exists(ProjectLibrary.FilePath), "failed rollback keeps library");
        }
        else Check(existing ? File.ReadAllBytes(ProjectLibrary.FilePath).SequenceEqual(beforeLibrary!) : !File.Exists(ProjectLibrary.FilePath), "library restored");
        Check(existing ? ProjectLibrary.Backups().Any(p => File.ReadAllBytes(p).SequenceEqual(beforeLibrary!)) : outcome.LibraryBackup == null,
            "library backup state");
        lockStream?.Dispose(); lockStream = null;
        // 将注入导致未恢复的文件人工恢复，并解除锁；然后从同一 Apply 入口重试。
        foreach (var path in paths) File.WriteAllBytes(path, beforeCanvas[path]);
        if (rollbackShouldFail && fault != "restore-canvas")
        {
            if (existing) Check(ProjectLibrary.RestoreFromBackup(outcome.LibraryBackup!) == null, "manual library recovery");
            else Check(ProjectLibrary.TryDeleteFile(out var error), error);
        }
        var retry = ProjectEntityMigration.Apply(current, paths[0]);
        Check(retry.Succeeded && retry.Migrated == 3 && ProjectLibrary.Load().Entities.Count == (existing ? 4 : 3), "retry converges");
        Check(current.Entities.Single().ManagedByProject, "retry memory managed");
        Check(paths.All(p => CanvasLibrary.TryLoad(p, out var s) && s!.Canvas!.Entities.Single().ManagedByProject), "retry canvas managed");
        Check(ProjectEntityMigration.Preview(current, paths[0]).Migrated == 0, "idempotent preview");
        Console.WriteLine($"existing={existing}, fault={fault}: written={writes}, rollbackComplete={outcome.RollbackComplete}, retry={retry.Migrated}, backups={ProjectLibrary.Backups().Count}/{CanvasBackup.List().Count}");
    }
    finally
    {
        lockStream?.Dispose();
        Environment.SetEnvironmentVariable("YEEYEEYEE_CANVAS_DIR", priorCanvas);
        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", priorAsset);
        Environment.SetEnvironmentVariable("YEEYEEYEE_CONFIG", priorConfig);
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

static WorkflowEntity Entity(string name)
{
    var entity = new WorkflowEntity { Kind = EntityKind.Character, Name = name, Core = name };
    entity.CreateVariant("default");
    return entity;
}
static void Check(bool value, string reason)
{
    if (!value) throw new Exception(reason);
}
