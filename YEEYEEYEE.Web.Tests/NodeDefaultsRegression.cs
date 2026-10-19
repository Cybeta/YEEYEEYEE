using System.Text.Json;
using Microsoft.AspNetCore.Http;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web;

/// <summary>
/// Web 新建节点的默认资产需求回归。
///
/// 网页端没有 Desktop 的「确认这一镜的资产需求」对话框；新建分镜若保持 Unknown，
/// 每次提交生成都会被 GenerationAudit 拦下。这里钉住两点：
/// · 新建分镜默认是「明确无角色 / 场景 / 道具」，并且能通过图片提交审计；
/// · 在网页端挂上一条引用后，对应类别自动变成 Required，避免「无资产却有引用」的自相矛盾。
/// </summary>
internal static class NodeDefaultsRegression
{
    public static void Run()
    {
        static void Assert(bool value, string label) { if (!value) throw new Exception("Node defaults: " + label); }
        static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;

        var root = Path.Combine(Path.GetTempPath(), "dream-node-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = ProjectContext.Create(root, "node-defaults");
            AppPaths.UseProject(project);
            var canvasPath = Path.Combine(project.RootPath, "canvases", "画面.json");
            var entitiesPath = Path.Combine(project.RootPath, "project", "entities.json");
            var canvas = new WorkflowCanvasState();
            var character = new WorkflowEntity { Kind = EntityKind.Character, Name = "角色" };
            var variant = character.CreateVariant("默认");
            canvas.Entities.Add(character);
            void Save() => File.WriteAllBytes(canvasPath, JsonSerializer.SerializeToUtf8Bytes(
                new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            Save();

            var store = new ProjectCanvasSceneStore(canvasPath, entitiesPath);
            var revision = store.CurrentRevision() ?? 0;
            Assert(Status(store.CreateNode(revision, NodeCategory.Storyboard, "新分镜", null)) == 200, "create storyboard node");

            var createdState = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!;
            var created = createdState.Canvas.Nodes.Single(node => node.Title == "新分镜");
            Assert(created.AssetRequirements.Character == AssetNeed.None
                && created.AssetRequirements.Scene == AssetNeed.None
                && created.AssetRequirements.Prop == AssetNeed.None, "new Web storyboard defaults to explicit none");
            var audit = GenerationAudit.CheckImageSubmission(createdState.Canvas, created, _ => null,
                Array.Empty<string>(), 4);
            Assert(audit.Count == 0, "explicit none must pass image submission audit: " + string.Join("；", audit));

            // 挂一条角色引用：对应需求必须自动变成 Required，否则会撞上「明确无角色却有引用」。
            revision = store.CurrentRevision() ?? 0;
            Assert(Status(store.AddReference(revision, created.Id, character.Id, variant.Id, null)) == 200, "add character reference");
            var referenced = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!
                .Canvas.Nodes.Single(node => node.Id == created.Id);
            Assert(referenced.AssetRequirements.Character == AssetNeed.Required, "adding a reference marks that kind required");
            Assert(referenced.AssetRequirements.Scene == AssetNeed.None
                && referenced.AssetRequirements.Prop == AssetNeed.None, "other kinds stay none");

            // 非分镜节点不受图片提交审计约束，保持 Unknown 也不会被误判成「明确无」。
            revision = store.CurrentRevision() ?? 0;
            Assert(Status(store.CreateNode(revision, NodeCategory.General, "通用节点", null)) == 200, "create general node");
            var general = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(canvasPath))!
                .Canvas.Nodes.Single(node => node.Title == "通用节点");
            Assert(general.AssetRequirements.Character == AssetNeed.Unknown, "non-storyboard keeps unknown requirements");

            Console.WriteLine("Node defaults regression passed: Web-created storyboard defaults to explicit none and passes image audit; "
                + "adding a reference promotes that kind to required; non-storyboard stays unknown");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
