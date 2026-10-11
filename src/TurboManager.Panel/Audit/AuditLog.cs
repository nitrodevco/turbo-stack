using TurboManager.Panel.Data;

namespace TurboManager.Panel.Audit;

public sealed class AuditLog(PanelDbContext db, IHttpContextAccessor http)
{
    /// <summary>Queues an audit entry; it is saved with the caller's next SaveChanges.</summary>
    public void Record(string action, PanelUser? user = null, string? target = null, string? details = null)
    {
        db.AuditLog.Add(new AuditEntry
        {
            Action = action,
            UserId = user?.Id is > 0 ? user.Id : null,
            Username = user?.Username,
            Target = target,
            Details = details,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
    }
}
