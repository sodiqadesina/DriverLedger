using DriverLedger.Domain.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DriverLedger.Domain.Identity
{
    public sealed class Role : Entity
    {
        public string Name { get; private set; } = default!;
        private Role() { }
        public Role(string name) => Name = name;
    }
}
