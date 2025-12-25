

namespace DriverLedger.Application.Auditing
{
    public interface IAuditWriter
    {
        Task WriteAsync(string action, string entityType, string entityId, object? metadata = null, CancellationToken ct = default);
    }


}
