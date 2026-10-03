namespace YEEYEEYEE.Core;

public static class AccessPolicy
{
    public static bool CanEditCanvas(SessionContext session) => session.ServerClaims.Contains("canvas.edit");
    public static bool CanCancelJob(SessionContext session) => session.ServerClaims.Contains("job.cancel");
    public static bool CanUndo(SessionContext session) => session.ServerClaims.Contains("canvas.undo");
    public static void Require(bool allowed, string message) { if (!allowed) throw new ProtocolViolationException("PROTOCOL_UNAUTHORIZED", message, "warning"); }
}
