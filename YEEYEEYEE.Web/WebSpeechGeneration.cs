using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>
/// 配音技能：走 OpenAI 兼容的 <c>POST {endpoint}/audio/speech</c>。
///
/// 端点与密钥复用媒体配置里的图片那一套（同一个 AnyAIAPI 站点），
/// 音色与模型由**角色的声音档案**决定，不在请求体里随便传——
/// 这正是「跨镜声音一致」能被检查的前提。
/// </summary>
internal static class WebSpeechGeneration
{
    public const string SkillId = "web.text-to-speech";

    public sealed record Result(bool Ok, string? FilePath, string? Error);

    public static bool IsConfigured(AiProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.ImageEndpoint) && !string.IsNullOrWhiteSpace(config.ImageApiKey);

    public static async Task<Result> GenerateAsync(
        AiProviderConfig config, string text, VoiceProfile voice, string outputPath,
        HttpClient http, CancellationToken cancellationToken)
    {
        var endpoint = config.ImageEndpoint.TrimEnd('/') + "/audio/speech";
        var body = JsonSerializer.Serialize(new
        {
            model = voice.Model,
            voice = voice.VoiceId,
            input = text,
            speed = voice.Speed <= 0 ? 1 : voice.Speed,
            response_format = "mp3"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ImageApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (detail.Length > 300) detail = detail[..300];
            return new Result(false, null, $"语音接口返回 {(int)response.StatusCode}：{detail.Trim()}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(outputPath);
        await stream.CopyToAsync(file, cancellationToken);
        return new Result(true, outputPath, null);
    }
}
