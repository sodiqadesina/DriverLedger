using DriverLedger.Application.Common;



namespace DriverLedger.Infrastructure.Common
{
    public sealed class TenantProvider : ITenantProvider
    {
        private static readonly AsyncLocal<Guid?> _current = new();

        public Guid? TenantId => _current.Value;

        public void SetTenant(Guid tenantId) => _current.Value = tenantId;
    }
}
