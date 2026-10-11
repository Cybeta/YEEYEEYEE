using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

internal static class WebWorkflowCatalog
{
    internal sealed record Entry(string Id, SiteProfile Site, SiteWorkflow Workflow, string Payload, ComfyUiWorkflowSlots Slots)
    {
        public Capability Capability => Workflow.Kind == "video" ? Capability.TextToVideo : Capability.TextToImage;
    }

    internal static bool IsWorkflow(string id) => id.StartsWith("comfyui.workflow.", StringComparison.Ordinal);
    internal static IEnumerable<Entry> Load(string baseUrl)
    {
        foreach (var site in SiteCatalog.Load().Sites.Where(s => s.IsComfyUi &&
                     string.Equals(s.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
        foreach (var workflow in site.Workflows.Where(w => w.Converted && w.Enabled && w.Kind is "image" or "video"))
        {
            var payload = SiteCatalog.LoadPayload(site.Id, workflow.PayloadFile);
            if (payload is null) continue;
            ComfyUiWorkflowSlots slots;
            try { slots = ComfyUiWorkflowBinder.Detect(payload, site.OptionValues, site.FileSlots); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; }
            if (!slots.CanTextToImage || slots.CanTakeImage || slots.CanTakeVideo || slots.CanTakeAudio || slots.ControlSlots.Count > 0 ||
                workflow.DroppedInputs + workflow.BrokenInputs + workflow.UncertainInputs + workflow.MissingFiles > 0) continue;
            using var graph = JsonDocument.Parse(payload);
            var outputTypes = graph.RootElement.EnumerateObject().Select(n => n.Value.GetProperty("class_type").GetString()).ToArray();
            if (workflow.Kind == "image" && !outputTypes.Any(t => t is "SaveImage" or "PreviewImage")) continue;
            if (workflow.Kind == "video" && !outputTypes.Any(t => t is "VHS_VideoCombine" or "SaveVideo" or "SaveWEBM")) continue;
            var id = "comfyui.workflow." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(site.Id + "\n" + workflow.Key))).ToLowerInvariant();
            yield return new Entry(id, site, workflow, payload, slots);
        }
    }

    internal static JsonElement Bind(Entry entry, Invocation invocation)
    {
        var graph = ComfyUiWorkflowBinder.Bind(entry.Payload, entry.Slots, new ComfyUiBindValues
        {
            Prompt = invocation.Inputs["prompt"].GetString()!
        });
        return JsonSerializer.SerializeToElement(graph);
    }
}
