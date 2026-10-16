using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;
using YEEYEEYEE.Web;

internal static class VideoReferenceRegression
{
    public static async Task Run()
    {
        static void Assert(bool value, string label) { if (!value) throw new Exception("Video reference: " + label); }
        var root = Path.Combine(Path.GetTempPath(), "dream-video-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousAssets = Environment.GetEnvironmentVariable("YEEYEEYEE_ASSET_DIR");
        var assembly = typeof(WebChatMessage).Assembly;
        var settings = assembly.GetType("YEEYEEYEE.Web.WebSettingsApi")!;
        var configPath = settings.GetProperty("ServerConfigPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousConfig = configPath.GetValue(null);
        try
        {
            configPath.SetValue(null, Path.Combine(root, "unused-config.json"));
            var project = ProjectContext.Create(root, "video");
            AppPaths.UseProject(project);
            var assets = Path.Combine(root, "assets"); Directory.CreateDirectory(assets);
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", assets);
            var frame = Path.Combine(assets, "frame.png");
            File.WriteAllBytes(frame, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var parsed = AgentActionParser.Parse("```json\n{\"actions\":[{\"kind\":\"invoke_skill\",\"target\":\"web.text-to-video\",\"content\":\"move\",\"aspectRatio\":\"16:9\",\"seconds\":15,\"width\":1280,\"height\":720}]}\n```").Actions.Single();
            Assert(parsed.UnsupportedSkillFields.Count == 0 && parsed.Seconds == 15 && parsed.Width == 1280 && parsed.Height == 720, "video parameters survive action parsing");
            var invalid = AgentActionParser.Parse("```json\n{\"actions\":[{\"kind\":\"invoke_skill\",\"target\":\"web.text-to-video\",\"seconds\":\"15\"}]}\n```").Actions.Single();
            Assert(invalid.UnsupportedSkillFields.Contains("seconds"), "invalid duration is rejected");
            var node = new WorkflowNode { Category = NodeCategory.Storyboard, AssetRequirements = new ShotAssetRequirements { Character = AssetNeed.None, Scene = AssetNeed.None, Prop = AssetNeed.None } };
            node.Attachments.Add(new WorkflowAttachment { Reference = "asset://frame.png" });
            var canvas = new WorkflowCanvasState(); canvas.Nodes.Add(node);
            var path = Path.Combine(project.RootPath, "canvases", "video.json");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            var storeType = assembly.GetType("YEEYEEYEE.Web.ProjectCanvasSceneStore")!;
            var store = Activator.CreateInstance(storeType, path, Path.Combine(project.RootPath, "project", "entities.json"))!;
            var references = assembly.GetType("YEEYEEYEE.Web.WebGenerationReferences")!;
            var resolve = references.GetMethod("ResolveVideo", BindingFlags.NonPublic | BindingFlags.Static)!;
            var paths = (string[])resolve.Invoke(null, new object[] { store, node.Id, Array.Empty<string>() })!;
            Assert(paths.SequenceEqual(new[] { frame }), "local asset reference resolves to adopted frame");
            try { resolve.Invoke(null, new object[] { store, node.Id, new[] { frame, frame } }); throw new Exception("multiple references accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
            var provider = new RecordingProvider();
            var executorType = assembly.GetType("YEEYEEYEE.Web.WebInvocationExecutor")!;
            using var http = new HttpClient();
            var executor = (IInvocationExecutor)Activator.CreateInstance(executorType, new object?[] { null, provider, null, false, http, new ConfigurationBuilder().Build() })!;
            var invocation = new Invocation { Tool = "web.text-to-video", Inputs = new Dictionary<string, JsonElement>
            {
                ["prompt"] = JsonSerializer.SerializeToElement("move"), ["seconds"] = JsonSerializer.SerializeToElement(15),
                ["width"] = JsonSerializer.SerializeToElement(1280), ["height"] = JsonSerializer.SerializeToElement(720),
                ["referenceImages"] = JsonSerializer.SerializeToElement(paths)
            } };
            var record = references.GetMethod("RecordEvidence", BindingFlags.NonPublic | BindingFlags.Static)!;
            record.Invoke(null, new object?[] { invocation.Inputs, paths, null });
            try { await executor.ExecuteAsync(new SessionContext(), invocation, new Job(Guid.NewGuid(), Guid.NewGuid(), "offline"), CancellationToken.None); }
            catch (InvalidOperationException error) when (error.Message == "offline verification") { }
            Assert(provider.Request is { Seconds: 15, Width: 1280, Height: 720 } request && request.ReferenceImages.SequenceEqual(new[] { frame }), "executor forwards duration, size and local frame without HTTP");
            var entity = new WorkflowEntity { Name = "reference", Kind = EntityKind.Character, ManagedByProject = false };
            var variant = new WorkflowEntityVariant();
            variant.Attachments.Add(new WorkflowAttachment { Reference = "asset://frame.png" });
            entity.Variants.Add(variant); canvas.Entities.Add(entity);
            node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
            node.AssetRequirements!.Character = AssetNeed.Required;
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            var target = storeType.GetMethod("GenerationTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(store, new object[] { node.Id })!;
            var imagePaths = (string[])references.GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { target, 1, Array.Empty<string>() })!;
            Assert(imagePaths.SequenceEqual(provider.Request!.ReferenceImages), "necessary image reference equals actual transmitted reference");
            record.Invoke(null, new object?[] { invocation.Inputs, imagePaths, target });
            var imageExecutor = new RecordingImageExecutor();
            var imageInvocation = new Invocation { Tool = "comfyui.text-to-image", Inputs = invocation.Inputs };
            var imageForwarder = (IInvocationExecutor)Activator.CreateInstance(executorType, new object?[] { imageExecutor, provider, null, false, http, new ConfigurationBuilder().Build() })!;
            await imageForwarder.ExecuteAsync(new SessionContext(), imageInvocation, new Job(Guid.NewGuid(), Guid.NewGuid(), "offline"), CancellationToken.None);
            Assert(imageExecutor.Paths.SequenceEqual(imagePaths), "image executor receives necessary reference paths");
            var inputs = (Dictionary<string, JsonElement>)invocation.Inputs;
            inputs["targetNodeId"] = JsonSerializer.SerializeToElement(node.Id);
            inputs["webCanvasPath"] = JsonSerializer.SerializeToElement(path);
            var projectApi = assembly.GetType("YEEYEEYEE.Web.WebProjectApi")!;
            var modeType = assembly.GetType("YEEYEEYEE.Web.WebCanvasMode")!;
            var mode = Activator.CreateInstance(modeType, path, Path.Combine(project.RootPath, "project", "entities.json"), true, null)!;
            var registered = projectApi.GetField("Canvases", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            registered.GetType().GetProperty("Item")!.SetValue(registered, mode, new object[] { path });
            try { await executor.ExecuteAsync(new SessionContext(), invocation, new Job(Guid.NewGuid(), Guid.NewGuid(), "offline"), CancellationToken.None); }
            catch (InvalidOperationException error) when (error.Message == "offline verification") { }
            var baselineCalls = provider.Calls;
            variant.Description = "changed after submission";
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            try { await executor.ExecuteAsync(new SessionContext(), invocation, new Job(Guid.NewGuid(), Guid.NewGuid(), "offline"), CancellationToken.None); throw new Exception("changed reference accepted"); }
            catch (InvalidDataException) { }
            Assert(provider.Calls == baselineCalls, "changed reference snapshot rejected before provider");
            variant.Description = "";
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new RecentCanvasState { Canvas = canvas, FormatVersion = CanvasFormat.Current }));
            var job = new ExecutionResult { JobId = Guid.NewGuid(), State = JobState.Succeeded, Inputs = inputs,
                Outputs = new[] { new AssetRef { Ref = "asset://frame.png", Role = "image" } } };
            var action = new AgentAction { Kind = "attach_asset", Target = node.Id.ToString(), Source = job.JobId.ToString(), Content = "asset://frame.png" };
            var previewMethod = storeType.GetMethod("PreviewActions")!;
            var preview = ((long Revision, byte[] Before, byte[] After, string?[] Hints, string[] Errors, object Diff))previewMethod.Invoke(store,
                new object[] { new[] { action }, (Func<AgentAction, ExecutionResult>)(_ => job) })!;
            Assert(preview.Errors.Length == 0, "trusted output previews successfully");
            var after = JsonSerializer.Deserialize<RecentCanvasState>(preview.After)!;
            var attached = after.Canvas.Nodes.Single().Attachments.Last();
            Assert(after.Canvas.Nodes.Single().Attachments.First().Id == node.Attachments.First().Id && attached.Id != node.Attachments.First().Id, "preview keeps original attachment first and appends trusted output last");
            Assert(attached.SourceFingerprints.OrderBy(p => p.Key).SequenceEqual(ReferenceStaleness.Snapshot(canvas, node).OrderBy(p => p.Key)), "attachment copies original submission snapshot");
            Assert(GenerationAudit.CheckVideoAssets(after.Canvas, after.Canvas.Nodes.Single(), a => a.Reference == "asset://frame.png" ? frame : null).CanSubmit(), "recorded required reference passes unchanged video audit");
            inputs["targetBaseRevision"] = JsonSerializer.SerializeToElement((long)storeType.GetMethod("CurrentRevision")!.Invoke(store, null)!);
            var attach = (Microsoft.AspNetCore.Http.IResult)storeType.GetMethod("AttachJobOutputs")!.Invoke(store, new object[] { job, node.Id })!;
            Assert((attach as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode is null or 200, "nonchat output attaches");
            var persisted = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(path))!;
            Assert(persisted.Canvas.Nodes.Single().Attachments.First().Id == node.Attachments.First().Id && persisted.Canvas.Nodes.Single().Attachments.Last().Id != node.Attachments.First().Id, "nonchat keeps original attachment first and appends trusted output last");
            Assert(persisted.Canvas.Nodes.Single().Attachments.Last().SourceFingerprints.Count == attached.SourceFingerprints.Count, "nonchat output preserves submission evidence");
            Assert(GenerationAudit.CheckVideoAssets(persisted.Canvas, persisted.Canvas.Nodes.Single(), a => a.Reference == "asset://frame.png" ? frame : null).CanSubmit(), "nonchat required reference passes unchanged video audit");
            inputs.Remove("sourceFingerprints");
            job = new ExecutionResult { JobId = Guid.NewGuid(), State = JobState.Succeeded, Inputs = inputs, Outputs = job.Outputs };
            inputs["targetBaseRevision"] = JsonSerializer.SerializeToElement((long)storeType.GetMethod("CurrentRevision")!.Invoke(store, null)!);
            attach = (Microsoft.AspNetCore.Http.IResult)storeType.GetMethod("AttachJobOutputs")!.Invoke(store, new object[] { job, node.Id })!;
            Assert((attach as Microsoft.AspNetCore.Http.IStatusCodeHttpResult)?.StatusCode is null or 200, "historical nonchat output attaches");
            Assert(JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllBytes(path))!.Canvas.Nodes.Single().Attachments.Last().SourceFingerprints.Count == 0, "historical nonchat output stays without evidence");
            action.Source = job.JobId.ToString();
            preview = ((long, byte[], byte[], string?[], string[], object))previewMethod.Invoke(store,
                new object[] { new[] { action }, (Func<AgentAction, ExecutionResult>)(_ => job) })!;
            Assert(preview.Errors.Length == 0, "historical output previews successfully");
            Assert(JsonSerializer.Deserialize<RecentCanvasState>(preview.After)!.Canvas.Nodes.Single().Attachments.Last().SourceFingerprints.Count == 0, "historical output does not acquire current snapshot");
            inputs.Remove("targetNodeId");
            var calls = provider.Calls;
            File.WriteAllBytes(frame, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 });
            try { await executor.ExecuteAsync(new SessionContext(), invocation, new Job(Guid.NewGuid(), Guid.NewGuid(), "offline"), CancellationToken.None); throw new Exception("changed bytes accepted"); }
            catch (InvalidDataException) { }
            Assert(provider.Calls == calls, "changed bytes rejected before provider");
            inputs["referenceImages"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
            try { await executor.ExecuteAsync(new SessionContext(), invocation, new Job(Guid.NewGuid(), Guid.NewGuid(), "offline"), CancellationToken.None); throw new Exception("missing coverage accepted"); }
            catch (InvalidDataException) { }
            Assert(provider.Calls == calls, "missing request coverage rejected before provider");
            Console.WriteLine("Video reference passed: offline forwarding, original snapshot attachment, historical empty evidence, changed bytes and request coverage rejection.");
        }
        finally
        {
            configPath.SetValue(null, previousConfig);
            Environment.SetEnvironmentVariable("YEEYEEYEE_ASSET_DIR", previousAssets);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingImageExecutor : IInvocationExecutor
    {
        public string[] Paths { get; private set; } = [];
        public Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
        {
            Paths = invocation.Inputs.TryGetValue("referenceImages", out var images) ? images.Deserialize<string[]>() ?? [] : [];
            return Task.FromResult(new ExecutionOutput());
        }
    }

    private sealed class RecordingProvider : IVideoProvider
    {
        public VideoGenerationRequest? Request { get; private set; }
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public string Name => "offline";
        public ReferenceCapacity ReferenceCapacity => new(1, "offline");
        public IReadOnlySet<GenerationCapability> Capabilities => new HashSet<GenerationCapability> { GenerationCapability.TextToVideo, GenerationCapability.ImageToVideo };
        public Task<VideoGenerationResult> GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            return Task.FromResult(new VideoGenerationResult { Status = VideoGenerationStatus.Failed, Error = "offline verification" });
        }
    }
}
