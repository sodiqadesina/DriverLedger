using DriverLedger.Domain.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DriverLedger.Domain.Identity
{
    public sealed class User : Entity
    {
        public string Email { get; private set; } = default!;
        public string PasswordHash { get; private set; } = default!;
        public string Status { get; private set; } = "Active"; // Active/Locked/Disabled
        public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

        private User() { } // EF

        public User(string email, string passwordHash)
        {
            Email = email.Trim().ToLowerInvariant();
            PasswordHash = passwordHash;
        }

        public void Lock() => Status = "Locked";
        public void Unlock() => Status = "Active";
    }
}
