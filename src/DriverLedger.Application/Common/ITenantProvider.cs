

namespace DriverLedger.Application.Common
{
    public interface ITenantProvider
    {
        Guid? TenantId { get; }
        void SetTenant(Guid tenantId);
    }

    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }
}
