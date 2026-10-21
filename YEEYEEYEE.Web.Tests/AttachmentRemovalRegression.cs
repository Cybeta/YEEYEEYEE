using System.Text.Json;
using Microsoft.AspNetCore.Http;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web;

internal static class AttachmentRemovalRegression
{
    public static void Run()
    {
        static void Assert(bool ok, string label) { if (!ok) throw new Exception("Attachment removal: " + label); }
        static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var root = Path.Combine(Path.GetTempPath(), "yeeeyee-removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
        var outside = Path.Combine(Path.GetTempPath(), "yeeeyee-outside-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var project = ProjectContext.Create(root, "test");
            AppPaths.UseProject(project);
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(assets);
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", assets);
            var png = new byte[24];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            "IHDR"u8.CopyTo(png.AsSpan(12));
            File.WriteAllBytes(Path.Combine(assets, "image.png"), png);
            File.WriteAllBytes(Path.Combine(assets, "shared.png"), png);
            File.WriteAllBytes(Path.Combine(assets, "locked.png"), png);
            File.WriteAllBytes(outside, png);

            var node = new WorkflowNode { Title = "镜头一", Category = NodeCategory.Storyboard };
            var image = new WorkflowAttachment { Reference = "asset://image.png" };
            var sharedMine = new WorkflowAttachment { Reference = "asset://shared.png" };
            node.Attachments.Add(image);
            node.Attachments.Add(sharedMine);
            var other = new WorkflowNode { Title = "镜头二", Category = NodeCategory.Storyboard };
            other.Attachments.Add(new WorkflowAttachment { Reference = "asset://shared.png" });
            var locked = new WorkflowNode { Title = "锁定镜头", Category = NodeCategory.Storyboard, IsLocked = true };
            var lockedImage = new WorkflowAttachment { Reference = "asset://locked.png" };
            locked.Attachments.Add(lockedImage);
            var outsideNode = new WorkflowNode { Title = "越界引用", Category = NodeCategory.Storyboard };
            var outsideAttachment = new WorkflowAttachment { Reference = outside };
            outsideNode.Attachments.Add(outsideAttachment);

            var canvas = new WorkflowCanvasState();
            canvas.Nodes.Add(node); canvas.Nodes.Add(other); canvas.Nodes.Add(locked); canvas.Nodes.Add(outsideNode);
            var path = Path.Combine(project.RootPath, "canvases", "test.json");
            var entitiesPath = Path.Combine(project.RootPath, "project", "entities.json");
            var storeType = typeof(WebChatMessage).Assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
            var store = Activator.CreateInstance(storeType, path, entitiesPath)!;
            long? Revision() => (long?)storeType.GetMethod("CurrentRevision")!.Invoke(store, null);
            IResult Remove(long revision, Guid nodeId, Guid attachmentId) =>
                (IResult)storeType.GetMethod("RemoveAttachment")!.Invoke(store, new object[] { revision, nodeId, attachmentId })!;
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));

            var revision = Revision()!.Value;
            Assert(Status(Remove(revision + 1, node.Id, image.Id)) == 409, "revision CAS");
            Assert(Status(Remove(revision, locked.Id, lockedImage.Id)) == 409, "locked node rejected");
            Assert(File.Exists(Path.Combine(assets, "locked.png")), "locked node file untouched");

            Assert(Status(Remove(revision, node.Id, image.Id)) == 200, "remove unique attachment");
            Assert(!File.Exists(Path.Combine(assets, "image.png")), "unique file moved out");
            Assert(Directory.Exists(Path.Combine(assets, "_recycle")) &&
                Directory.EnumerateFiles(Path.Combine(assets, "_recycle"), "*image.png").Any(), "unique file recycled");

            revision = Revision()!.Value;
            Assert(Status(Remove(revision, node.Id, sharedMine.Id)) == 200, "remove shared reference");
            Assert(File.Exists(Path.Combine(assets, "shared.png")), "shared file retained");

            revision = Revision()!.Value;
            Assert(Status(Remove(revision, outsideNode.Id, outsideAttachment.Id)) == 200, "outside path reference removed");
            Assert(File.Exists(outside), "outside path file untouched");

            var saved = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(path))!;
            Assert(saved.Canvas.Nodes.Single(n => n.Id == node.Id).Attachments.Count == 0, "node attachments removed");
            Assert(saved.Canvas.Nodes.Single(n => n.Id == other.Id).Attachments.Count == 1, "other node keeps shared attachment");
            revision = Revision()!.Value;
            Assert(Status(Remove(revision, node.Id, sharedMine.Id)) == 404, "missing attachment rejected");
            Console.WriteLine("Attachment removal passed: CAS, lock, unique recycle, shared retention.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previous);
            try { File.Delete(outside); } catch (IOException) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
