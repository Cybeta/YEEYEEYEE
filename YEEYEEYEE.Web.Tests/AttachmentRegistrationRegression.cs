using System.Text.Json;
using Microsoft.AspNetCore.Http;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web;

internal static class AttachmentRegistrationRegression
{
    public static void Run()
    {
        static void Assert(bool ok, string label) { if (!ok) throw new Exception("Attachment registration: " + label); }
        static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var root = Path.Combine(Path.GetTempPath(), "yeeeyee-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
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
            File.WriteAllText(Path.Combine(assets, "fake.png"), "not an image");
            var entity = new WorkflowEntity { Name = "同名", ManagedByProject = false, Kind = EntityKind.Prop };
            var variant = new WorkflowEntityVariant();
            entity.Variants.Add(variant);
            var node = new WorkflowNode { Title = "同名", Category = NodeCategory.Prop };
            var image = new WorkflowAttachment { Reference = "asset://image.png" };
            node.Attachments.Add(image);
            var fake = new WorkflowAttachment { Reference = "asset://fake.png" };
            var traversal = new WorkflowAttachment { Reference = "asset://../image.png" };
            node.Attachments.Add(fake); node.Attachments.Add(traversal);
            var canvas = new WorkflowCanvasState();
            canvas.Entities.Add(entity); canvas.Nodes.Add(node);
            var path = Path.Combine(project.RootPath, "canvases", "test.json");
            var entitiesPath = Path.Combine(project.RootPath, "project", "entities.json");
            var storeType = typeof(WebChatMessage).Assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
            var store = Activator.CreateInstance(storeType, path, entitiesPath)!;
            long? Revision() => (long?)storeType.GetMethod("CurrentRevision")!.Invoke(store, null);
            IResult Register(long revision, Guid nodeId, Guid attachmentId, Guid entityId, Guid variantId) =>
                (IResult)storeType.GetMethod("RegisterAttachment")!.Invoke(store, new object[] { revision, nodeId, attachmentId, entityId, variantId })!;
            IResult Bind(long revision) => (IResult)storeType.GetMethod("AddReference")!.Invoke(store, new object?[] { revision, node.Id, entity.Id, variant.Id, null })!;
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            var revision = Revision()!.Value;
            Assert(Status(Register(revision, node.Id, image.Id, entity.Id, variant.Id)) == 409, "title never implies binding");
            Assert(Status(Bind(revision)) == 200, "existing reference operation binds local variant");
            revision = Revision()!.Value;
            var before = File.ReadAllBytes(path);
            Assert(Status(Register(revision + 1, node.Id, image.Id, entity.Id, variant.Id)) == 409, "revision CAS");
            Assert(Status(Register(revision, node.Id, Guid.NewGuid(), entity.Id, variant.Id)) == 404, "attachment must belong to node");
            Assert(Status(Register(revision, node.Id, image.Id, entity.Id, Guid.NewGuid())) == 404, "variant must belong to entity");
            var rejectedImage = Register(revision, node.Id, fake.Id, entity.Id, variant.Id);
            Assert(Status(rejectedImage) == 415, "false image signature: " + JsonSerializer.Serialize((rejectedImage as IValueHttpResult)?.Value));
            Assert(Status(Register(revision, node.Id, traversal.Id, entity.Id, variant.Id)) == 409, "noncanonical asset path");
            Assert(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "rejected writes preserve bytes");
            Assert(Status(Register(revision, node.Id, image.Id, entity.Id, variant.Id)) == 200, "register existing image");
            var saved = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(path))!;
            var registered = saved.Canvas.Entities.Single().Variants.Single();
            Assert(registered.CurrentVersion == 2 && registered.Versions.Count == 2, "new version preserves initial version");
            Assert(registered.Versions[0].Attachments.Count == 0 && registered.Versions[1].Attachments.Single().Id == image.Id, "historical snapshot and attachment identity");
            Assert(saved.Canvas.Nodes.Single().Attachments.Count == 3, "node attachments retained");
            before = File.ReadAllBytes(path);
            Assert(Status(Register(Revision()!.Value, node.Id, image.Id, entity.Id, variant.Id)) == 200, "duplicate succeeds without new commit");
            Assert(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "duplicate preserves revision and versions");
            Console.WriteLine("Attachment registration passed: explicit binding, CAS, signature/path rejection, snapshots, duplicate idempotency.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
