using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DriverLedger.Domain.Identity
{
    public sealed class UserRole
    {
        public Guid UserId { get; set; }
        public Guid RoleId { get; set; }
    }
}
