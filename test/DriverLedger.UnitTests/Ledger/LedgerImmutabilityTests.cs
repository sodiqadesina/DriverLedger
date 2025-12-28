

using DriverLedger.Application.Common;
using DriverLedger.Domain.Ledger;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace DriverLedger.UnitTests.Ledger
{
    public sealed class LedgerImmutabilityTests
    {
        [Fact]
        public void Updating_ledger_entry_throws()
        {
            var tenantProvider = new TestTenantProvider();
            var tenantId = Guid.NewGuid();
            tenantProvider.SetTenant(tenantId);

            var options = new DbContextOptionsBuilder<DriverLedgerDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString("N"))
                // IMPORTANT: Interceptor must be attached here for this unit tests
                .AddInterceptors(new LedgerImmutabilityInterceptor())
                .Options;

            using var db = new DriverLedgerDbContext(options, tenantProvider);

            var entry = new LedgerEntry
            {
                TenantId = tenantId,
                EntryDate = DateOnly.FromDateTime(DateTime.UtcNow),
                SourceType = "Receipt",
                SourceId = Guid.NewGuid().ToString("D"),
                PostedByType = "System",
                CorrelationId = Guid.NewGuid().ToString("N")
            };

            db.LedgerEntries.Add(entry);
            db.SaveChanges();

            // attempt mutation (should be blocked)
            entry.SourceType = "Manual";

            Action act = () => db.SaveChanges();

            act.Should().Throw<InvalidOperationException>();
        }

        private sealed class TestTenantProvider : ITenantProvider
        {
            private Guid? _tenantId;
            public Guid? TenantId => _tenantId;
            public void SetTenant(Guid tenantId) => _tenantId = tenantId;
        }
    }
}
