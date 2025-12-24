using DriverLedger.Domain.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DriverLedger.Domain.Drivers
{
    public sealed class DriverProfile : Entity, ITenantScoped
    {
        // TenantId = UserId (Driver)
        public Guid TenantId { get; set; }

        public string Province { get; set; } = "ON";
        public bool HstRegistered { get; set; }
        public decimal DefaultBusinessUsePct { get; set; } = 0.90m;
        public string PolicyJson { get; set; } = "{}";
    }
}
