namespace DreamForge.Desktop;

/// <summary>
/// 程序入口。
///
/// 必须写成显式的 <c>Main</c> 并带 <c>[STAThread]</c>，不能用顶层语句：
/// **顶层语句生成的 Main 不带这个标记，主线程会跑在 MTA 上**。
/// 界面本身还能起来，但凡是走 OLE 的东西都会抛 `ThreadStateException`——
/// 剪贴板（复制）、打开/保存文件对话框、拖放，全部受影响。
/// 这正是"右键复制这条报错"的根因，实测证据见 PROGRESS 第七十二轮。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        while (true)
        {
            var project = ProjectStartupForm.ShowStartup();
            if (project is null) return;
            AppPaths.UseProject(project);
            using var mainForm = new MainForm();
            Application.Run(mainForm);
            if (!mainForm.RestartForProjectSelection) return;
        }
    }
}
