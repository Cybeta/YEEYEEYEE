using System.Runtime.CompilerServices;

// 迁移测试要驱动「写画布中途失败」这条故障路径，而那个带回调的 Apply 是 internal
// （见 ProjectLibrary.cs 的 ProjectEntityMigration.Apply）——它不该成为公开 API，但测试必须够得着。
// 这条声明原先挂在旧 WinForms 端项目上（ProjectMigrationFaultHook.cs 里就只有这一行），
// 第 127 轮随「把共享代码收进库」一起搬到真正声明那个 internal 成员的程序集：Core。
[assembly: InternalsVisibleTo("DreamForge.Migration.Tests")]
