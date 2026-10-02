using System.Runtime.CompilerServices;

// 更新链路里有两处 internal 需要测试够得着：
// · UpdateInstaller.SwapScript —— 那段脚本**必须只含 ASCII**（PowerShell 5 会把无 BOM 的 UTF-8 当 ANSI 读，
//   中文会变乱码甚至语法错），这是本轮已经踩过两次的坑，得有一条测试把它钉住；
// · UpdateInstaller 的标记读写要能在隔离的配置目录里跑一遍。
[assembly: InternalsVisibleTo("YEEYEEYEE.Agent.Tests")]
