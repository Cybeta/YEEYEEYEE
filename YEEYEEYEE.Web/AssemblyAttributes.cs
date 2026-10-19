using System.Runtime.CompilerServices;

// 判档的资格校验、结果存储与运行器都在 Web 项目内部：
// 测试项目直接调这些规则，不再靠反射拼参数，避免「测的是另一份形状」。
[assembly: InternalsVisibleTo("YEEYEEYEE.Web.Tests")]
