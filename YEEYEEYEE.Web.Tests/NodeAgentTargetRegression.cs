using System.Reflection;
using System.Text.Json;
using YEEYEEYEE.Desktop;

/// <summary>
/// 节点 Agent 的目标身份（Web 层）：模型上下文里必须**始终**带着目标节点的稳定 id，
/// 且同名节点、章节筛选与清单截断都不能让动作改到别的节点。
///
/// 为什么这层要单独钉住：目标节点的标题只是标签——同名节点存在时它不足以定位；
/// 而 Web 的画布清单还会被章节筛选与 100 条上限截断，目标可能压根不在清单里。
/// 这里的断言全部是确定性的（不依赖真实模型输出）：直接看构造出来的上下文文本与预检结果。
/// </summary>
internal static class NodeAgentTargetRegression
{
    private sealed record PreviewResult(byte[] After, string[] Errors);

    public static void Run()
    {
        static void Assert(bool value, string label) { if (!value) throw new Exception("Node agent target: " + label); }
        var root = Path.Combine(Path.GetTempPath(), "dream-node-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousAssets = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
        try
        {
            var project = ProjectContext.Create(root, "target");
            AppPaths.UseProject(project);
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(assets);
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", assets);

            var chapterX = Guid.NewGuid();
            var chapterY = Guid.NewGuid();
            var target = new WorkflowNode { Category = NodeCategory.Storyboard, Title = "同名分镜", Content = "目标节点的正文标记 TARGET-BODY", WorkTreeItemId = chapterX, Chapter = "第一章" };
            // 诱饵与目标标题相同，但**不绑同一个工作树条目**：绑在一起时共享正文是产品既有行为（统一工作树），
            // 那会把「同名节点被误改」的判定搅浑。这里要单独钉的是「按 id 只改一个」。
            var decoy = new WorkflowNode { Category = NodeCategory.Storyboard, Title = "同名分镜", Content = "另一个同名节点的正文", Chapter = "第一章" };
            var other = new WorkflowNode { Category = NodeCategory.Storyboard, Title = "别的节点", Content = "无关节点", WorkTreeItemId = chapterY, Chapter = "第二章" };
            var canvas = new WorkflowCanvasState();
            canvas.WorkTree.Add(new WorkTreeItem { Id = chapterX, Name = "第一章", Kind = WorkTreeKind.Chapter, Order = 10 });
            canvas.WorkTree.Add(new WorkTreeItem { Id = chapterY, Name = "第二章", Kind = WorkTreeKind.Chapter, Order = 20 });
            canvas.Nodes.Add(decoy);
            canvas.Nodes.Add(other);
            // 塞满 100 条以上，再把目标节点放在最后：它会落在画布清单的截断之外。
            for (var index = 0; index < 120; index++)
                canvas.Nodes.Add(new WorkflowNode { Category = NodeCategory.General, Title = $"填充 {index}", Content = "填充" });
            canvas.Nodes.Add(target);
            var canvasPath = Path.Combine(project.RootPath, "canvases", "target.json");
            File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));

            var assembly = typeof(YEEYEEYEE.Web.WebChatMessage).Assembly;
            var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
            var store = Activator.CreateInstance(storeType, canvasPath, Path.Combine(project.RootPath, "project", "entities.json"))!;
            var snapshotMethod = storeType.GetMethod("AgentContextSnapshot")!;
            var previewMethod = storeType.GetMethod("PreviewActions")!;
            var targetFullId = target.Id.ToString("N");
            var targetShortId = targetFullId[..8];

            string Context(string? chapterId, string? nodeId)
            {
                var value = snapshotMethod.Invoke(store, new object?[] { chapterId, nodeId, false })!;
                // 返回的是 ValueTuple：运行期只有 Item1/Item2，名字是编译期的。
                return (string)value.GetType().GetField("Item2")!.GetValue(value)!;
            }
            PreviewResult Preview(AgentAction action)
            {
                var value = previewMethod.Invoke(store, new object?[] { new[] { action }, null })!;
                var type = value.GetType();
                return new PreviewResult((byte[])type.GetField("Item3")!.GetValue(value)!, (string[])type.GetField("Item5")!.GetValue(value)!);
            }
            WorkflowCanvasState AfterCanvas(PreviewResult preview) => JsonSerializer.Deserialize<RecentCanvasState>(preview.After)!.Canvas;
            var bytesBefore = File.ReadAllBytes(canvasPath);

            // ① 目标身份：id（全 + 短）、类别、章节、标题、正文一个都不少，且在截断之外也在清单里。
            var context = Context(null, target.Id.ToString());
            Assert(context.Contains("本次请求的目标节点"), "context states the request target");
            Assert(context.Contains(targetFullId) && context.Contains($"短 {targetShortId}"), "target identity carries the full and short id");
            Assert(context.Contains("分镜") && context.Contains(chapterX.ToString("N")) && context.Contains("第一章"), "target identity carries category and chapter");
            Assert(context.Contains("标题「同名分镜」"), "target identity distinguishes id from title");
            Assert(context.Contains("目标节点的正文标记 TARGET-BODY"), "target body is present in full");
            Assert(canvas.Nodes.Count > 100 && context.Contains($"{target.Id} |"), "target row survives the 100-node truncation");
            Assert(context.Split("同名分镜").Length - 1 >= 2, "both same-named nodes stay listed (ambiguity visible)");

            // ② 章节筛选把目标排除在外时，身份、正文与清单行仍然给全。
            var filtered = Context(chapterY.ToString(), target.Id.ToString());
            Assert(filtered.Contains(targetFullId) && filtered.Contains(targetShortId) && filtered.Contains("目标节点的正文标记 TARGET-BODY"),
                "target context survives a chapter filter that excludes it");
            Assert(filtered.Contains($"{target.Id} |"), "target row is pinned into the filtered list");

            // ③ 同名节点：动作按 id 只改指定那一个；只给标题时不猜（宁可报错）。
            var byId = Preview(new AgentAction { Kind = "update_node", Target = targetFullId, Content = "按 id 改的内容" });
            Assert(byId.Errors.Length == 0, "id-targeted update previews cleanly");
            var afterById = AfterCanvas(byId).Nodes;
            var targetAfter = afterById.Single(node => node.Id == target.Id).Content;
            var decoyAfter = afterById.Single(node => node.Id == decoy.Id).Content;
            Assert(targetAfter == "按 id 改的内容" && decoyAfter == "另一个同名节点的正文",
                $"update touches only the id-targeted node (target='{targetAfter}' decoy='{decoyAfter}')");
            var byShortId = Preview(new AgentAction { Kind = "update_node", Target = targetShortId, Content = "按短 id 改的内容" });
            Assert(byShortId.Errors.Length == 0
                && AfterCanvas(byShortId).Nodes.Single(node => node.Id == decoy.Id).Content != "按短 id 改的内容", "8-char short id also resolves to the same node");
            var byTitle = Preview(new AgentAction { Kind = "update_node", Target = "同名分镜", Content = "按标题改" });
            Assert(byTitle.Errors.Any(error => error.Contains("找不到目标节点")), "ambiguous title is refused instead of guessing");
            Assert(AfterCanvas(byTitle).Nodes.All(node => node.Content != "按标题改"), "ambiguous title changes nothing");

            // ④ 目标失效（提交前被删掉）：明确报错，不退回到同名节点。
            var missing = Preview(new AgentAction { Kind = "update_node", Target = Guid.NewGuid().ToString("N"), Content = "改一个不存在的节点" });
            Assert(missing.Errors.Any(error => error.Contains("找不到目标节点")), "missing target is rejected explicitly");
            var deleted = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
            deleted.Canvas.Nodes.RemoveAll(node => node.Id == target.Id);
            File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(deleted));
            var afterDelete = Preview(new AgentAction { Kind = "update_node", Target = targetFullId, Content = "目标已被删除" });
            Assert(afterDelete.Errors.Any(error => error.Contains("找不到目标节点"))
                && AfterCanvas(afterDelete).Nodes.Single(node => node.Id == decoy.Id).Content == "另一个同名节点的正文",
                "deleted target is refused and the same-named node is untouched");
            File.WriteAllBytes(canvasPath, bytesBefore);

            // ⑤ 跨节点与新建节点不被这次修复挡住。
            var cross = Preview(new AgentAction { Kind = "update_node", Target = decoy.Id.ToString("N"), Content = "改另一个节点是允许的" });
            Assert(cross.Errors.Length == 0 && AfterCanvas(cross).Nodes.Single(node => node.Id == decoy.Id).Content == "改另一个节点是允许的",
                "cross-node action stays allowed");
            var created = Preview(new AgentAction { Kind = "create_node", Title = "新建节点", Content = "正文" });
            Assert(created.Errors.Length == 0 && AfterCanvas(created).Nodes.Count == canvas.Nodes.Count + 1, "create_node stays allowed");
            var edge = Preview(new AgentAction { Kind = "create_edge", Source = targetFullId, Target = decoy.Id.ToString("N") });
            Assert(edge.Errors.Length == 0, "edge between two existing nodes stays allowed");

            Assert(bytesBefore.SequenceEqual(File.ReadAllBytes(canvasPath)), "previews never write the canvas");
            Console.WriteLine("Node agent target regression passed: target identity (id/category/chapter/title/body) always present, "
                + "same-title nodes resolved by id only, missing/deleted target rejected, cross-node and create actions unaffected");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previousAssets);
            Directory.Delete(root, recursive: true);
        }
    }
}
