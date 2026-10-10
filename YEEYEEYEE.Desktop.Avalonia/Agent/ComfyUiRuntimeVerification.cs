using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YEEYEEYEE.Desktop.Avalonia;

internal static class ComfyUiRuntimeVerification
{
    public static async Task RunAsync(ComfyUiNativeWebViewService service, string url, string output)
    {
        var report = new JsonObject { ["url"] = url, ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["status"] = "running" };
        var results = new JsonArray();
        report["results"] = results;
        async Task SaveAsync() => await File.WriteAllTextAsync(output, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            await SaveAsync();
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var listing = JsonNode.Parse(await http.GetStringAsync(url.TrimEnd('/') + "/api/userdata?dir=workflows&recurse=true&full_info=true", budget.Token))!.AsArray();
            var samples = new List<(string Path, JsonObject Graph)>();
            foreach (var item in listing)
            {
                var path = item is JsonValue value ? value.GetValue<string>() : item?["path"]?.GetValue<string>();
                if (path is null || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var resource = path.StartsWith("workflows/") ? path : "workflows/" + path;
                var graph = JsonNode.Parse(await http.GetStringAsync(url.TrimEnd('/') + "/api/userdata/" + Uri.EscapeDataString(resource), budget.Token)) as JsonObject;
                if (graph?["nodes"] is not JsonArray) continue;
                samples.Add((path, graph));
                if (samples.Count == 4) break;
            }
            report["sampleCount"] = samples.Count;
            await SaveAsync();
            var next = -1;
            var workers = Enumerable.Range(0, 2).Select(async worker =>
            {
                await using var exporter = service.Factory(url, worker);
                while (true)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= samples.Count) break;
                    var sample = samples[index];
                    var timer = Stopwatch.StartNew();
                    var result = new JsonObject { ["worker"] = worker, ["path"] = sample.Path };
                    try
                    {
                        var api = await exporter.ExportAsync(sample.Graph, budget.Token);
                        result["success"] = api.Count > 0;
                        result["apiNodes"] = api.Count;
                        result["api"] = api;
                    }
                    catch (Exception ex) { result["success"] = false; result["error"] = ex.ToString(); }
                    if (exporter is ComfyUiNativeWebViewExporter native)
                        result["evidence"] = native.LastEvidence?.DeepClone();
                    result["seconds"] = timer.Elapsed.TotalSeconds;
                    results.Add(result);
                    await SaveAsync();
                }
            });
            await Task.WhenAll(workers);
            report["status"] = samples.Count > 0 && results.All(r => r?["success"]?.GetValue<bool>() == true) ? "passed" : "failed";
        }
        catch (Exception ex) { report["status"] = "failed"; report["error"] = ex.ToString(); }
        finally { report["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O"); await SaveAsync(); }
    }
}
