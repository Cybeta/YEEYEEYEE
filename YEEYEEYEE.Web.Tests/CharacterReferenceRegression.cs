using System.Text.Json;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web;

/// <summary>
/// 角色参考图传递与生成记录的定向回归（离线）。
///
/// 钉住三件事：引用的实体 / 变体 / **选中的那张图**原样进请求；
/// 生成记录里能确认用了哪个角色、哪张参考图、什么用途与外观描述；
/// 换角色后记录与请求都只保留新角色的素材，不残留上一角色的图。
/// </summary>
internal static class CharacterReferenceRegression
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "yeeeyee-character-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousAssetDirectory = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
        Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", root);
        try
        {
            var linTurnaround = Path.Combine(root, "lin-turnaround.png");
            var linExpression = Path.Combine(root, "lin-expression.png");
            var chenTurnaround = Path.Combine(root, "chen-turnaround.png");
            File.WriteAllBytes(linTurnaround, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(linExpression, new byte[] { 4, 5, 6 });
            File.WriteAllBytes(chenTurnaround, new byte[] { 7, 8, 9 });

            var canvas = new WorkflowCanvasState();
            var lin = new WorkflowEntity { Kind = EntityKind.Character, Name = "林夏" };
            var linVariant = lin.CreateVariant("默认", "短发、米色风衣、左眉细疤");
            var linTurnaroundAttachment = new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://lin-turnaround.png", Name = "转面图", Purpose = ReferencePurpose.Turnaround };
            var linExpressionAttachment = new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://lin-expression.png", Name = "表情表", Purpose = ReferencePurpose.Expression };
            linVariant.Attachments.Add(linTurnaroundAttachment);
            linVariant.Attachments.Add(linExpressionAttachment);

            var chen = new WorkflowEntity { Kind = EntityKind.Character, Name = "沈砚" };
            var chenVariant = chen.CreateVariant("默认", "长发、藏青外套");
            var chenTurnaroundAttachment = new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://chen-turnaround.png", Name = "转面图", Purpose = ReferencePurpose.Turnaround };
            chenVariant.Attachments.Add(chenTurnaroundAttachment);
            canvas.Entities.Add(lin);
            canvas.Entities.Add(chen);

            var node = new WorkflowNode { Title = "分镜 1", Category = NodeCategory.Storyboard };
            var reference = new NodeReference { EntityId = lin.Id, VariantId = linVariant.Id, AttachmentId = linExpressionAttachment.Id };
            node.References.Add(reference);

            var inputs = new Dictionary<string, JsonElement>();
            WebGenerationReferences.RecordEvidence(inputs, new[] { linExpression }, (canvas, node, 1));

            var images = inputs["referenceImages"].Deserialize<string[]>()!;
            Check(images.Length == 1 && images[0] == linExpression, "请求参考图必须是选中的那张表情表，实际：" + string.Join("、", images));

            var characters = inputs["characterReferences"].Deserialize<List<ReferenceSelectionRecord>>()!;
            Check(characters.Count == 1, "生成记录必须记下使用的角色数量，实际 " + characters.Count);
            var linRecord = characters.Single();
            Check(linRecord.entityId == lin.Id.ToString() && linRecord.name == "林夏", "生成记录要能确认角色身份");
            Check(linRecord.variantId == linVariant.Id.ToString(), "生成记录要能确认使用的变体");
            Check(linRecord.attachmentId == linExpressionAttachment.Id.ToString(), "生成记录要能确认具体哪张参考图");
            Check(linRecord.purpose == nameof(ReferencePurpose.Expression) && linRecord.purposeLabel == "表情表",
                "生成记录要保留参考图用途，实际：" + linRecord.purpose + "/" + linRecord.purposeLabel);
            Check(linRecord.path == linExpression, "生成记录要保留参考图实际路径");
            Check(linRecord.description.Contains("米色风衣"), "外观描述要作为辅助约束留档，实际：" + linRecord.description);

            // 换角色：只带新角色的参考图，记录里也不能残留旧角色。
            reference.EntityId = chen.Id;
            reference.VariantId = chenVariant.Id;
            reference.AttachmentId = chenTurnaroundAttachment.Id;
            inputs.Clear();
            WebGenerationReferences.RecordEvidence(inputs, new[] { chenTurnaround }, (canvas, node, 1));

            images = inputs["referenceImages"].Deserialize<string[]>()!;
            Check(images.Length == 1 && images[0] == chenTurnaround && !images.Contains(linExpression),
                "换角色后请求不能残留上一角色的参考图");
            characters = inputs["characterReferences"].Deserialize<List<ReferenceSelectionRecord>>()!;
            Check(characters.Count == 1 && characters[0].name == "沈砚" &&
                characters[0].attachmentId == chenTurnaroundAttachment.Id.ToString(),
                "换角色后生成记录只能是新角色的参考图");

            // 没有选图的老引用：记录里落到实际使用的第一张（旧行为），通用流程不受影响。
            reference.AttachmentId = null;
            inputs.Clear();
            WebGenerationReferences.RecordEvidence(inputs, new[] { chenTurnaround }, (canvas, node, 1));
            characters = inputs["characterReferences"].Deserialize<List<ReferenceSelectionRecord>>()!;
            Check(characters.Single().attachmentId == chenTurnaroundAttachment.Id.ToString(),
                "没选图时要记录实际取用的第一张参考图");

            // 有实体引用时，提示词要写明「第几张是谁」，序号必须与实际上传顺序一致：
            // 即使场景先加进引用，参考图仍按 角色 → 场景 排列。
            var scene = new WorkflowEntity { Kind = EntityKind.Scene, Name = "冷光工位" };
            var sceneVariant = scene.CreateVariant("默认", "冷白台灯、深蓝夜景");
            var scenePath = Path.Combine(root, "scene-desk.png");
            File.WriteAllBytes(scenePath, new byte[] { 9, 9, 9 });
            var sceneAttachment = new WorkflowAttachment { Kind = AttachmentKind.Image, Reference = "asset://scene-desk.png", Name = "场景参考", Purpose = ReferencePurpose.General };
            sceneVariant.Attachments.Add(sceneAttachment);
            canvas.Entities.Add(scene);

            var guidedNode = new WorkflowNode { Title = "分镜 2", Category = NodeCategory.Storyboard };
            guidedNode.References.Add(new NodeReference { EntityId = scene.Id, VariantId = sceneVariant.Id, AttachmentId = sceneAttachment.Id });
            guidedNode.References.Add(new NodeReference { EntityId = chen.Id, VariantId = chenVariant.Id, AttachmentId = chenTurnaroundAttachment.Id });

            var guided = WebGenerationReferences.WithReferenceGuide(canvas, guidedNode, "cinematic shot", 0);
            Check(guided.Contains("Reference image 1: character reference for 沈砚"), "角色必须是第一张，实际：" + guided);
            Check(guided.Contains("Reference image 2: scene reference for 冷光工位"), "场景必须是第二张，实际：" + guided);
            Check(guided.Contains("use it only for face, hair, body proportions, clothing and accessories"), "角色参考图要限定用途");
            Check(guided.Contains("never use a scene reference to redesign a character"), "要禁止把场景图当角色设定");
            Check(WebGenerationReferences.WithReferenceGuide(canvas, guidedNode, guided, 0) == guided, "已有映射时不能重复追加");

            // 没有实体引用就不能凭空写参考图说明。
            var plain = new WorkflowNode { Title = "分镜 3", Category = NodeCategory.Storyboard };
            Check(WebGenerationReferences.WithReferenceGuide(canvas, plain, "cinematic shot", 0) == "cinematic shot",
                "没有引用时提示词必须原样保留");

            Console.WriteLine("Character reference selection and generation record regression passed (offline)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previousAssetDirectory);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
