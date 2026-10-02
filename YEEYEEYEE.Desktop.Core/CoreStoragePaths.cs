namespace YEEYEEYEE.Desktop;

/// <summary>
/// Core 自己需要的项目内路径（草稿画布）。
///
/// 第 127 轮之前这里叫 <c>StorageMaintenance</c>——而共享层里**另有一个**同名类装着完整的存储维护功能
/// （统计占用、列出资产引用、清理无引用图片），那个才是测试与界面在用的。同名两个类在同一个命名空间里
/// 是「一份实现两处声明」的老毛病，所以这里按职责改名：Core 只保留路径入口，
/// 存储维护整体归 <c>YEEYEEYEE.Desktop.Shared</c> 的同名类（见那边的说明）。
/// </summary>
public static class CoreStoragePaths
{
    /// <summary>未保存到画布库的草稿画布（项目根文件夹下的 last-canvas.json）。</summary>
    public static string DraftCanvasPath => AppPaths.Combine("last-canvas.json");
}
