namespace YEEYEEYEE.Core;

public sealed class ProtocolViolationException : InvalidOperationException
{
    public string Code { get; }
    public string Severity { get; }
    public ProtocolViolationException(string code, string message, string severity = "fatal") : base(message) { Code = code; Severity = severity; }
}

public sealed class SkillReferenceException : InvalidOperationException
{
    public Guid FaultingId { get; }
    public string Code { get; }
    public SkillReferenceException(Guid faultingId, string code, string message) : base(message) { FaultingId = faultingId; Code = code; }
}

public sealed class JobStateException : InvalidOperationException
{
    public JobState From { get; }
    public JobState To { get; }
    public JobStateException(JobState from, JobState to) : base($"不允许 Job 状态从 {from} 转为 {to}") { From = from; To = to; }
}
