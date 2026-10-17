using System.Runtime.CompilerServices;

// 封面提取与本地播放的边界验收（临时样本、路径校验、令牌会话与释放）需要在离线回归里跑，
// 所以把这一端的 internal 开放给 Agent 测试工程——被测的是产品代码本身，不是另写一套。
[assembly: InternalsVisibleTo("YEEYEEYEE.Agent.Tests")]
