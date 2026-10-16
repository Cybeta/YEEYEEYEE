using System.Text.Json;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Web;

internal static class UsageRegression
{
    public static void Run()
    {
        static void Assert(bool value, string label) { if (!value) throw new Exception("Usage regression: " + label); }
        var message = new WebChatMessage { Role = "assistant", Model = "test", ContextWindow = 10000, Usage = new AiUsage(2000, 100, 500, 1500) };
        message.ObserveOutput(1.5, thinking: true);
        message.ObserveOutput(2.5, thinking: true);
        Assert(message.FirstTokenSeconds == 1.5 && message.DecodeSeconds is null, "thinking starts first token but does not end decode");
        message.ObserveOutput(3.5, thinking: false);
        message.ObserveOutput(5.5, thinking: false);
        message.ObserveOutput(9.5, thinking: true);
        Assert(message.FirstTokenSeconds == 1.5 && message.DecodeSeconds == 4, "decode ends at last body delta, including initial thinking");
        var report = message.UsageReport!;
        var desktopReport = AiUsageReport.From(message.Usage, 10000, 4, 1.5);
        Assert(report.Chips.SequenceEqual(desktopReport.Chips) && report.Details.SequenceEqual(desktopReport.Details), "shared desktop report");
        Assert(report.Chips.Any(c => c.Contains("25") && c.Contains("缓存")), "cache denominator is billed input");
        Assert(report.Chips.Contains("25 tok/s") && report.Chips.Any(c => c.Contains("上下文")), "decode rate and declared context");
        var json = JsonSerializer.SerializeToElement(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(json.GetProperty("usageReport").GetProperty("chips").GetArrayLength() == 5 && json.GetProperty("contextWindow").GetInt32() == 10000, "web protocol includes report and raw timing/context");
        var missing = new WebChatMessage { Role = "assistant" };
        Assert(!missing.UsageReport!.HasAnything && missing.UsageReport.Details.Any(d => d.Contains("不是 0")), "missing usage is not zero");
        missing.ObserveOutput(1.2, thinking: true);
        Assert(missing.UsageReport!.Chips.Count == 1 && !missing.UsageReport.Chips.Any(c => c.Contains("↑")), "first token without usage does not fabricate tokens");
        var noMetadata = new WebChatMessage { Role = "assistant", Usage = new AiUsage(1200, 80, 0, 0) };
        Assert(noMetadata.UsageReport!.Chips.Count == 1, "unknown cache/context/clock are not fabricated");
        var root = Path.Combine(Path.GetTempPath(), "yeeeyee-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "chat.db");
            var store = new WebChatStore(path);
            message.State = "streaming";
            var session = new WebChatSession { Messages = new() { message, missing, noMetadata } };
            store.Save("owner", session);
            var recovered = new WebChatStore(path).Get("owner", session.Id)!;
            var restored = recovered.Messages[0];
            Assert(restored.State == "interrupted" && restored.ContextWindow == 10000 && restored.DecodeSeconds == 4 && restored.FirstTokenSeconds == 1.5, "crash recovery preserves request statistics");
            Assert(restored.Usage == message.Usage && restored.UsageReport!.Chips.SequenceEqual(report.Chips), "report survives SQLite/JSON restoration");
            Assert(recovered.Messages[1].Usage is null && recovered.Messages[2].DecodeSeconds is null, "missing metadata survives restoration");
            var legacy = JsonSerializer.Deserialize<WebChatMessage>("{\"Role\":\"assistant\",\"Usage\":{\"PromptTokens\":1200,\"CompletionTokens\":80,\"CacheHitTokens\":0,\"CacheMissTokens\":0}}")!;
            Assert(legacy.UsageReport!.Chips.Count == 1 && legacy.ContextWindow == 0 && legacy.FirstTokenSeconds is null, "old sessions retain tokens without inventing metadata");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Usage regression passed: Desktop report, thinking/body timing, cache denominator, missing data, web JSON, SQLite/restart recovery and legacy sessions");
    }
}
