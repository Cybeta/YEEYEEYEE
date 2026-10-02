using Microsoft.Extensions.Configuration;

namespace YEEYEEYEE.Web;

/// <summary>
/// 这个服务实例当前服务**哪一张画布**。
///
/// 抽出来是因为有两个调用方：场景接口（读写画布）与编辑锁接口（锁文件挨着画布放）。
/// 两边必须解出同一个答案——否则锁会记到另一份文件上，表现成「锁了却没人看见」。
/// 解析规则与失败口径原样保留（连错误码都没改）。
/// </summary>
internal sealed record WebCanvasMode(string? CanvasPath, string? EntitiesPath, bool IsProject, IResult? Error)
{
    private static IResult Fail(int status, string code, string message) =>
        Results.Json(new { code, message }, statusCode: status);

    public static WebCanvasMode Resolve(IConfiguration configuration)
    {
        var projectCanvasPath = LegacyConfig.Text(configuration, "ProjectCanvasPath");
        if (!string.IsNullOrWhiteSpace(projectCanvasPath))
        {
            try
            {
                var (canvas, entities) = ProjectCanvasSceneStore.ResolvePaths(
                    projectCanvasPath, LegacyConfig.Text(configuration, "ProjectEntitiesPath"));
                return new WebCanvasMode(canvas, entities, true, null);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 项目画布不可信时**不回退**到独立场景：那会让「编辑的是哪个项目」变成一个静默的猜测。
                return new WebCanvasMode(null, null, false,
                    Fail(503, "PROJECT_CANVAS_UNAVAILABLE", "项目上下文不可信，拒绝回退到独立 Web 场景：" + ex.Message));
            }
        }

        if (LegacyConfig.Flag(configuration, "AllowStandaloneWebScene") != true)
            return new WebCanvasMode(null, null, false,
                Fail(503, "SCENE_MODE_NOT_CONFIGURED", "独立 Web 场景需显式启用；项目画布编辑当前不可用"));

        var standalonePath = LegacyConfig.Text(configuration, "WebScenePath");
        if (string.IsNullOrWhiteSpace(standalonePath) || !Path.IsPathFullyQualified(standalonePath))
            return new WebCanvasMode(null, null, false,
                Fail(503, "SCENE_PATH_NOT_CONFIGURED", "独立 Web 场景需配置绝对路径"));

        return new WebCanvasMode(Path.GetFullPath(standalonePath), null, false, null);
    }
}
