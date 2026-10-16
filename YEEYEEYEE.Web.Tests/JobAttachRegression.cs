using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;

/// <summary>
/// 生成产物回挂的并发回归：多个任务从**同一版画布**提交，产物要能依次安全追加。
///
/// 为什么值得单独钉住：任务提交时记下的是当时那一版 revision，而同一批里第一个任务回挂成功
/// 就会把它改掉——按「修订必须相等」写，第二个任务必然 SCENE_REVISION_CONFLICT（哪怕它属于
/// 另一个节点）。这里把「同一节点两个任务」「不同节点两个任务」「同一任务重复回挂」
/// 「目标删除/锁定/产物不可读」四类都跑一遍。
/// </summary>
internal static class JobAttachRegression
{
    public static void Run()
    {
        static void Assert(bool value, string label) { if (!value) throw new Exception("Job attach: " + label); }
        static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        // Error(...) 回的是匿名对象 { code, message }：只取 code 用，不依赖具体形状。
        static string Code(IResult result) => (result as IValueHttpResult)?.Value?.GetType()
            .GetProperty("code")?.GetValue((result as IValueHttpResult)!.Value)?.ToString() ?? string.Empty;

        var root = Path.Combine(Path.GetTempPath(), "dream-job-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousAssets = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
        try
        {
            var project = ProjectContext.Create(root, "attach");
            AppPaths.UseProject(project);
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(assets);
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", assets);
            File.WriteAllBytes(Path.Combine(assets, "out-a.png"), new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            File.WriteAllBytes(Path.Combine(assets, "out-b.png"), new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 });
            File.WriteAllBytes(Path.Combine(assets, "out-c.mp4"), new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70 });
            // 支持的媒体只有图片与视频：音频这一类产物必须被挡在门外（不是「挂上去但打不开」）。
            File.WriteAllBytes(Path.Combine(assets, "out-d.wav"), new byte[] { 0x52, 0x49, 0x46, 0x46 });

            var nodeA = new WorkflowNode { Category = NodeCategory.Storyboard, Title = "镜头一" };
            var nodeB = new WorkflowNode { Category = NodeCategory.Storyboard, Title = "镜头二" };
            var canvas = new WorkflowCanvasState();
            canvas.Nodes.Add(nodeA);
            canvas.Nodes.Add(nodeB);
            var canvasPath = Path.Combine(project.RootPath, "canvases", "attach.json");
            void SaveCanvas() => File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(
                new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            SaveCanvas();

            var assembly = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly;
            var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
            var store = Activator.CreateInstance(storeType, canvasPath, Path.Combine(project.RootPath, "project", "entities.json"))!;
            var attachMethod = storeType.GetMethod("AttachJobOutputs")!;
            var revisionMethod = storeType.GetMethod("CurrentRevision")!;
            long Revision() => (long)revisionMethod.Invoke(store, null)!;
            IResult Attach(ExecutionResult job, Guid nodeId) => (IResult)attachMethod.Invoke(store, new object[] { job, nodeId })!;
            static ExecutionResult Job(Guid jobId, long baseline, Guid nodeId, string reference, string role = "image", bool withRevision = true)
            {
                var inputs = new Dictionary<string, JsonElement>
                {
                    ["targetNodeId"] = JsonSerializer.SerializeToElement(nodeId),
                    ["webCanvasPath"] = JsonSerializer.SerializeToElement("ignored-by-this-fixture"),
                    ["prompt"] = JsonSerializer.SerializeToElement("offline")
                };
                if (withRevision) inputs["targetBaseRevision"] = JsonSerializer.SerializeToElement(baseline);
                return new ExecutionResult
                {
                    JobId = jobId, State = JobState.Succeeded, Inputs = inputs,
                    Outputs = new[] { new AssetRef { Ref = "asset://" + reference, Role = role } }
                };
            }
            int AttachmentsOf(Guid nodeId) => JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!
                .Canvas.Nodes.Single(node => node.Id == nodeId).Attachments.Count;

            // 两个任务从**同一版**提交：第一个回挂成功会改掉 revision，第二个必须照样追加。
            var baseline = Revision();
            var jobA1 = Guid.NewGuid();
            var jobB1 = Guid.NewGuid();
            var jobA2 = Guid.NewGuid();
            var firstA = Attach(Job(jobA1, baseline, nodeA.Id, "out-a.png"), nodeA.Id);
            Assert(Status(firstA) == 200, "first job attaches from the shared baseline");
            Assert(Revision() != baseline, "first attach moves the canvas revision");
            var secondB = Attach(Job(jobB1, baseline, nodeB.Id, "out-c.mp4", "video"), nodeB.Id);
            Assert(Status(secondB) == 200 && Code(secondB) == string.Empty, "second job from the same stale revision still attaches (other node)");
            Assert(AttachmentsOf(nodeA.Id) == 1 && AttachmentsOf(nodeB.Id) == 1, "each node keeps exactly its own output");
            var thirdA = Attach(Job(jobA2, baseline, nodeA.Id, "out-b.png"), nodeA.Id);
            Assert(Status(thirdA) == 200, "second job on the same node appends instead of overwriting");
            var persisted = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
            var nodeAAttachments = persisted.Canvas.Nodes.Single(node => node.Id == nodeA.Id).Attachments;
            Assert(nodeAAttachments.Count == 2 && nodeAAttachments[0].Id != nodeAAttachments[1].Id
                && nodeAAttachments[0].Reference == "asset://out-a.png" && nodeAAttachments[1].Reference == "asset://out-b.png",
                "both outputs of the same node survive in order");
            Assert(persisted.Canvas.Nodes.Single(node => node.Id == nodeA.Id).ExecutionStatus == NodeExecutionStatus.Completed,
                "attach marks the target node completed");

            // 同一个任务重复处理：幂等，不产生第二份附件（两个产物也一样）。
            var replay = Attach(Job(jobA1, baseline, nodeA.Id, "out-a.png"), nodeA.Id);
            Assert(Status(replay) == 200 && AttachmentsOf(nodeA.Id) == 2, "re-attaching the same job adds nothing");
            var jobB2 = Guid.NewGuid();
            var twoOutputs = new ExecutionResult
            {
                JobId = jobB2, State = JobState.Succeeded,
                Inputs = new Dictionary<string, JsonElement>
                {
                    ["targetNodeId"] = JsonSerializer.SerializeToElement(nodeB.Id),
                    ["targetBaseRevision"] = JsonSerializer.SerializeToElement(baseline),
                    ["prompt"] = JsonSerializer.SerializeToElement("offline")
                },
                Outputs = new[] { new AssetRef { Ref = "asset://out-a.png", Role = "image" }, new AssetRef { Ref = "asset://out-b.png", Role = "image" } }
            };
            Assert(Status(Attach(twoOutputs, nodeB.Id)) == 200 && AttachmentsOf(nodeB.Id) == 3, "multi-output job appends every output once");
            Assert(Status(Attach(twoOutputs, nodeB.Id)) == 200 && AttachmentsOf(nodeB.Id) == 3, "per-output attachment ids stay idempotent");

            // 目标与产物校验一条都不能少。
            var mismatch = Attach(Job(Guid.NewGuid(), baseline, nodeB.Id, "out-a.png"), nodeA.Id);
            Assert(Status(mismatch) == 409 && Code(mismatch) == "TARGET_MISMATCH", "target mismatch rejected");
            var missingRevision = Job(Guid.NewGuid(), baseline, nodeA.Id, "out-a.png", withRevision: false);
            Assert(Code(Attach(missingRevision, nodeA.Id)) == "TARGET_REVISION_REQUIRED", "missing submission revision rejected");
            var missingFile = Attach(Job(Guid.NewGuid(), baseline, nodeA.Id, "absent.png"), nodeA.Id);
            Assert(Status(missingFile) == 409 && Code(missingFile) == "OUTPUT_UNAVAILABLE", "unreadable output rejected");
            // 产物存在、也读得到，但不是图片/视频（音频）：同样不能挂上去。
            var unsupported = Attach(Job(Guid.NewGuid(), baseline, nodeA.Id, "out-d.wav", "audio"), nodeA.Id);
            Assert(Status(unsupported) == 409 && Code(unsupported) == "OUTPUT_UNSUPPORTED", "non-image/video output rejected");
            var deletedNodeId = Guid.NewGuid();
            var missingNode = Attach(Job(Guid.NewGuid(), baseline, deletedNodeId, "out-a.png"), deletedNodeId);
            Assert(Status(missingNode) == 404 && Code(missingNode) == "TARGET_NOT_FOUND", "deleted target rejected");

            // 被拒时写进 attachmentState 的那句话：必须是「CODE：说明」这样一句人话。
            // 序列化整个错误对象会把一段 JSON 直接摆到界面上（字段名与花括号都在）。
            var describe = assembly.GetType("YEEYEEYEE.Web.WebJobAttachmentService")!
                .GetMethod("DescribeFailure", BindingFlags.NonPublic | BindingFlags.Static)!;
            string Describe(IResult result) => (string)describe.Invoke(null, new object[] { result })!;
            Assert(Describe(unsupported) == "OUTPUT_UNSUPPORTED：产物不是支持的图片或视频",
                "rejected attach reports a readable reason");
            Assert(!Describe(missingNode).Contains('{') && !Describe(missingNode).Contains("message"),
                "rejected attach reason is not a serialized error object");

            // 目标锁定：产物保留在任务里，画布一个字节不改；解锁后再试才写进去。
            // 锁写在**文件里**（回挂是重新读文件再写），所以这里改盘上的那一份，而不是手上那份旧快照。
            void SetLocked(Guid nodeId, bool locked)
            {
                var state = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
                state.Canvas.Nodes.Single(node => node.Id == nodeId).IsLocked = locked;
                File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(state));
            }
            var attachmentsBeforeLock = AttachmentsOf(nodeB.Id);
            SetLocked(nodeB.Id, true);
            var beforeLocked = File.ReadAllBytes(canvasPath);
            var jobB3 = Guid.NewGuid();
            var locked = Attach(Job(jobB3, baseline, nodeB.Id, "out-b.png"), nodeB.Id);
            Assert(Status(locked) == 409 && Code(locked) == "TARGET_LOCKED", "locked target rejected");
            Assert(beforeLocked.SequenceEqual(File.ReadAllBytes(canvasPath)), "rejected attach leaves the canvas untouched");
            SetLocked(nodeB.Id, false);
            Assert(Status(Attach(Job(jobB3, baseline, nodeB.Id, "out-b.png"), nodeB.Id)) == 200
                && AttachmentsOf(nodeB.Id) == attachmentsBeforeLock + 1, "unlocked target accepts the same job afterwards");

            // 读取与写入之间画布被别人占住（这里用独占锁文件把那一瞬固定下来）：
            // 整笔作废、一个字节不写，调用方下一轮重新读、重新追加。
            var jobA3 = Guid.NewGuid();
            using (new FileStream(canvasPath + ".web.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var contended = Attach(Job(jobA3, baseline, nodeA.Id, "out-b.png"), nodeA.Id);
                Assert(Status(contended) >= 400 && AttachmentsOf(nodeA.Id) == 2, "contended write fails without touching the canvas");
            }
            Assert(Status(Attach(Job(jobA3, baseline, nodeA.Id, "out-b.png"), nodeA.Id)) == 200
                && AttachmentsOf(nodeA.Id) == 3, "retry after contention appends exactly once");

            Console.WriteLine("Job attach regression passed: shared-baseline appends (same/other node), multi-output idempotent replay, "
                + "target/revision/file/deleted/locked/unsupported-output rejection with preserved outputs, readable rejection reason, "
                + "contended write abort and retry");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previousAssets);
            Directory.Delete(root, recursive: true);
        }
    }
}
