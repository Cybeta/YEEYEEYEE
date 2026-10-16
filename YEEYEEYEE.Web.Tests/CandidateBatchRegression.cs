using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;

/// <summary>
/// 节点出图的**候选批次**回归（离线）：采用一件产物的语义、幂等与全部拒绝路径，
/// 以及「候选任务不走后台自动回挂」这条判据。
///
/// 为什么单独钉：候选这条路与普通回挂共用同一条写入通道，但**入口不同**——
/// 后台不能替候选任务抢先挂上去（那就没有「采用」这回事了），而采用又必须只加**那一件**。
/// 这两件事都只有在服务端这一侧才成立，所以在这里按真实 store 跑一遍。
/// </summary>
internal static class CandidateBatchRegression
{
    public static void Run()
    {
        static void Assert(bool value, string label) { if (!value) throw new Exception("Candidate batch: " + label); }
        // 服务端那个常量是 internal（测试项目只反射得到），这里按字面量写一份，并在下面用反射对账。
        const string CandidateInput = "nodeImageCandidate";
        static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        static string Code(IResult result) => (result as IValueHttpResult)?.Value?.GetType()
            .GetProperty("code")?.GetValue((result as IValueHttpResult)!.Value)?.ToString() ?? string.Empty;

        var root = Path.Combine(Path.GetTempPath(), "dream-candidate-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousAssets = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
        try
        {
            var project = ProjectContext.Create(root, "candidates");
            AppPaths.UseProject(project);
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(assets);
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", assets);
            File.WriteAllBytes(Path.Combine(assets, "shot-a.png"), new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            File.WriteAllBytes(Path.Combine(assets, "shot-b.png"), new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 });
            File.WriteAllBytes(Path.Combine(assets, "shot-c.mp4"), new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70 });

            var node = new WorkflowNode { Category = NodeCategory.Storyboard, Title = "镜头一" };
            var other = new WorkflowNode { Category = NodeCategory.Storyboard, Title = "另一个镜头" };
            var canvas = new WorkflowCanvasState();
            canvas.Nodes.Add(node);
            canvas.Nodes.Add(other);
            var canvasPath = Path.Combine(project.RootPath, "canvases", "candidates.json");
            void SaveCanvas() => File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(
                new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            SaveCanvas();

            var assembly = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly;
            var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
            var store = Activator.CreateInstance(storeType, canvasPath, Path.Combine(project.RootPath, "project", "entities.json"))!;
            var adoptMethod = storeType.GetMethod("AdoptJobOutput")!;
            var attachMethod = storeType.GetMethod("AttachJobOutputs")!;
            IResult Adopt(ExecutionResult job, Guid nodeId, int index) => (IResult)adoptMethod.Invoke(store, new object[] { job, nodeId, index })!;

            static ExecutionResult CandidateJob(Guid jobId, Guid nodeId, params string[] references)
            {
                var inputs = new Dictionary<string, JsonElement>
                {
                    ["targetNodeId"] = JsonSerializer.SerializeToElement(nodeId),
                    // 真实提交路径一定会写下这一条（提交时修订），回挂那条路要用它。
                    ["targetBaseRevision"] = JsonSerializer.SerializeToElement(1L),
                    ["prompt"] = JsonSerializer.SerializeToElement("离线候选"),
                    // 候选标记：后台自动回挂的判据就看它（见下面的 AttachApplies 断言）。
                    [CandidateInput] = JsonSerializer.SerializeToElement(true)
                };
                return new ExecutionResult
                {
                    JobId = jobId, UserId = Guid.NewGuid(), InvocationId = Guid.NewGuid(), IdempotencyKey = "candidate", State = JobState.Succeeded,
                    Tool = "comfyui.text-to-image", Inputs = inputs,
                    Outputs = references.Select(reference => new AssetRef { Ref = "asset://" + reference, Role = "image" }).ToArray()
                };
            }
            static Guid AttachmentId(Guid jobId, int index) =>
                new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"web-job:{jobId}:{index}"))[..16]);

            WorkflowNode PersistedNode(Guid nodeId) => JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!
                .Canvas.Nodes.Single(item => item.Id == nodeId);

            // 1. 采用选中的那一张：只加这一件，节点进入 Completed。
            var jobA = Guid.NewGuid();
            var adopted = Adopt(CandidateJob(jobA, node.Id, "shot-a.png", "shot-b.png"), node.Id, 1);
            Assert(Status(adopted) == 200, "adopt succeeds");
            var attachedNode = PersistedNode(node.Id);
            Assert(attachedNode.Attachments.Count == 1, "adopt adds exactly the picked output");
            Assert(attachedNode.Attachments[0].Id == AttachmentId(jobA, 1), "adopted attachment keeps the original output index");
            Assert(attachedNode.Attachments[0].Reference == "asset://shot-b.png", "adopted attachment keeps the task's own reference");
            Assert(attachedNode.ExecutionStatus == NodeExecutionStatus.Completed, "adopt marks the target node completed");

            // 2. 重复采用：不重复添加（同一件按确定性 ID 认出来），也不重写画布。
            var before = File.ReadAllBytes(canvasPath);
            var replay = Adopt(CandidateJob(jobA, node.Id, "shot-a.png", "shot-b.png"), node.Id, 1);
            Assert(Status(replay) == 200 && PersistedNode(node.Id).Attachments.Count == 1, "re-adopting the same output adds nothing");
            Assert(before.SequenceEqual(File.ReadAllBytes(canvasPath)), "re-adopting does not rewrite the canvas");

            // 3. 同一任务、另一件产物：各认各的（下标不同就是不同的产物）。
            var jobD = Guid.NewGuid();
            Assert(Status(Adopt(CandidateJob(jobD, node.Id, "shot-a.png"), node.Id, 0)) == 200
                && PersistedNode(node.Id).Attachments.Count == 2, "another candidate appends beside the first");
            // 普通回挂（同一件产物、同一个确定性 ID）也不会再加一份。
            var reattach = (IResult)attachMethod.Invoke(store, new object[] { CandidateJob(jobD, node.Id, "shot-a.png"), node.Id })!;
            Assert(Status(reattach) == 200 && PersistedNode(node.Id).Attachments.Count == 2,
                "background attach of an adopted output stays idempotent");

            // 4. 别的画布改动（改另一个节点的标题）之后仍可安全采用，已有附件一件不动、顺序不变。
            var changed = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
            changed.Canvas.Nodes.Single(item => item.Id == other.Id).Title = "别人刚改过";
            File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(changed));
            var jobC = Guid.NewGuid();
            Assert(Status(Adopt(CandidateJob(jobC, node.Id, "shot-b.png"), node.Id, 0)) == 200, "adopt survives unrelated canvas changes");
            var afterUnrelated = PersistedNode(node.Id);
            Assert(afterUnrelated.Attachments.Count == 3
                && afterUnrelated.Attachments[0].Id == AttachmentId(jobA, 1)
                && afterUnrelated.Attachments[1].Id == AttachmentId(jobD, 0)
                && afterUnrelated.Attachments[2].Id == AttachmentId(jobC, 0), "existing attachments are preserved in order");

            // 5. 拒绝路径：目标不一致、下标越界、产物读不到、不是图片、节点锁定、节点已删除。
            Assert(Code(Adopt(CandidateJob(Guid.NewGuid(), other.Id, "shot-a.png"), node.Id, 0)) == "TARGET_MISMATCH",
                "adopting onto another node is rejected");
            Assert(Code(Adopt(CandidateJob(jobA, node.Id, "shot-a.png", "shot-b.png"), node.Id, 7)) == "OUTPUT_NOT_FOUND",
                "out-of-range output index is rejected");
            Assert(Code(Adopt(CandidateJob(Guid.NewGuid(), node.Id, "absent.png"), node.Id, 0)) == "OUTPUT_UNAVAILABLE",
                "unreadable output is rejected");
            Assert(Code(Adopt(CandidateJob(Guid.NewGuid(), node.Id, "shot-c.mp4"), node.Id, 0)) == "OUTPUT_UNSUPPORTED",
                "non-image output is rejected");
            var deleted = Guid.NewGuid();
            var deletedResult = Adopt(CandidateJob(Guid.NewGuid(), deleted, "shot-a.png"), deleted, 0);
            Assert(Status(deletedResult) == 404 && Code(deletedResult) == "TARGET_NOT_FOUND", "deleted target is rejected");

            var lockedSnapshot = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
            lockedSnapshot.Canvas.Nodes.Single(item => item.Id == node.Id).IsLocked = true;
            File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(lockedSnapshot));
            var beforeLocked = File.ReadAllBytes(canvasPath);
            var locked = Adopt(CandidateJob(Guid.NewGuid(), node.Id, "shot-a.png"), node.Id, 0);
            Assert(Status(locked) == 409 && Code(locked) == "TARGET_LOCKED", "locked target is rejected");
            Assert(beforeLocked.SequenceEqual(File.ReadAllBytes(canvasPath)), "rejected adopt leaves the canvas untouched");

            // 6. 候选任务不走后台自动回挂这条路（判据就是 inputs 里那个标记）。
            var serviceType = assembly.GetType("YEEYEEYEE.Web.WebJobAttachmentService")!;
            var candidateField = serviceType.GetField("CandidateInput", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
            Assert((string)candidateField.GetRawConstantValue()! == CandidateInput, "candidate marker name matches the server constant");
            var applies = serviceType.GetMethod("AttachApplies", BindingFlags.NonPublic | BindingFlags.Static)!;
            bool Applies(ExecutionResult result) => (bool)applies.Invoke(null, new object[] { Job.Restore(result) })!;
            Assert(!Applies(CandidateJob(Guid.NewGuid(), node.Id, "shot-a.png")), "candidate jobs are never auto-attached");
            Assert(Applies(new ExecutionResult { JobId = Guid.NewGuid(), UserId = Guid.NewGuid(), InvocationId = Guid.NewGuid(), IdempotencyKey = "plain",
                State = JobState.Succeeded, Inputs = new Dictionary<string, JsonElement>
                {
                    ["targetNodeId"] = JsonSerializer.SerializeToElement(node.Id),
                    ["targetBaseRevision"] = JsonSerializer.SerializeToElement(1L)
                } }), "ordinary targeted jobs still auto-attach");
            Assert(!Applies(new ExecutionResult { JobId = Guid.NewGuid(), UserId = Guid.NewGuid(), InvocationId = Guid.NewGuid(), IdempotencyKey = "chat",
                State = JobState.Succeeded, Inputs = new Dictionary<string, JsonElement>
                {
                    ["targetNodeId"] = JsonSerializer.SerializeToElement(node.Id),
                    ["webChatGeneration"] = JsonSerializer.SerializeToElement(true)
                } }), "chat generations still stay out of background attach");
            Assert(!Applies(new ExecutionResult { JobId = Guid.NewGuid(), UserId = Guid.NewGuid(), InvocationId = Guid.NewGuid(), IdempotencyKey = "loose",
                State = JobState.Succeeded, Inputs = new Dictionary<string, JsonElement>() }), "jobs without a target still stay out");

            Console.WriteLine("Candidate batch regression passed: single-output adopt, idempotent replay, per-output identity, "
                + "protected append after unrelated changes, target/index/file/type/locked/deleted rejection, "
                + "and candidate jobs excluded from background attach");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previousAssets);
            Directory.Delete(root, recursive: true);
        }
    }
}
