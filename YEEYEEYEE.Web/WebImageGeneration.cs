using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

internal static class WebImageGeneration
{
    internal const string SkillId = "web.text-to-image";
    internal static bool IsConfigured(AiProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.ImageEndpoint) && !string.IsNullOrWhiteSpace(config.ImageModel);

    internal static int ReferenceCapacity(AiProviderConfig config) =>
        config.ImageModel.Contains("grok-image-2.0(池4)", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(0, config.ImageMaxReferenceImages);

    internal static (int Width, int Height)? Size(string? ratio) => ratio switch
    {
        "1:1" => (1024, 1024),
        "16:9" => (1536, 864),
        "9:16" => (864, 1536),
        "4:3" => (1024, 768),
        "3:4" => (768, 1024),
        _ => null
    };
}
