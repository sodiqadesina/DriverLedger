using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
