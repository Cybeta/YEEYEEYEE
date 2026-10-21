using System.Text.Json;
using YEEYEEYEE.Desktop;

internal static class AgentReferenceRegression
{
    public static void Run()
    {
        static void Assert(bool ok, string label) { if (!ok) throw new Exception("Agent reference: " + label); }
        var root = Path.Combine(Path.GetTempPath(), "yeeeyee-refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = ProjectContext.Create(root, "test");
            AppPaths.UseProject(project);
            Directory.CreateDirectory(Path.Combine(project.RootPath, "project"));
            var entitiesPath = Path.Combine(project.RootPath, "project", "entities.json");
            var path = Path.Combine(project.RootPath, "canvases", "test.json");

            var character = new WorkflowEntity { Kind = EntityKind.Character, Name = "CC" };
            var variant = character.CreateVariant("默认");
            var library = new ProjectEntityFile();
            library.Entities.Add(character);
            File.WriteAllText(entitiesPath, JsonSerializer.Serialize(library, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

            var shot = new WorkflowNode
            {
                Title = "S01", Category = NodeCategory.Storyboard,
                AssetRequirements = new ShotAssetRequirements { Character = AssetNeed.Required, Scene = AssetNeed.None, Prop = AssetNeed.None }
            };
            shot.References.Add(new NodeReference { EntityId = character.Id, VariantId = variant.Id });
            var canvas = new WorkflowCanvasState();
            canvas.Nodes.Add(shot);
            canvas.Entities.Add(character);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));

            var store = new YEEYEEYEE.Web.ProjectCanvasSceneStore(path, entitiesPath);
            string[] Preview(params AgentAction[] actions) => store.PreviewActions(actions, null).Errors;
            static ShotAssetRequirements Needs(AssetNeed character) => new() { Character = character, Scene = AssetNeed.None, Prop = AssetNeed.None };

            var missing = Preview(new AgentAction
            {
                Kind = "create_node", Title = "S02 缺引用", NodeCategory = "分镜",
                Content = "新分镜", AssetRequirements = Needs(AssetNeed.Required)
            });
            Assert(missing.Any(error => error.Contains("资产需求为 required")), "required without reference is rejected");

            var none = Preview(new AgentAction
            {
                Kind = "create_node", Title = "S03 无需资产", NodeCategory = "分镜",
                Content = "纯空镜", AssetRequirements = Needs(AssetNeed.None)
            });
            Assert(!none.Any(error => error.Contains("资产需求为 required")), "explicit none is not rejected");

            var existing = Preview(new AgentAction
            {
                Kind = "update_node", Target = shot.Id.ToString(), Content = "改过的镜头内容",
                AssetRequirements = Needs(AssetNeed.Required)
            });
            Assert(!existing.Any(error => error.Contains("资产需求为 required")), "existing reference satisfies the requirement");

            Console.WriteLine("Agent reference passed: required without reference rejected, none and existing references accepted.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
