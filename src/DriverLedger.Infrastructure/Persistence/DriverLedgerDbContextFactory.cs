using DriverLedger.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DriverLedger.Infrastructure.Persistence;

public sealed class DriverLedgerDbContextFactory : IDesignTimeDbContextFactory<DriverLedgerDbContext>
{
    public DriverLedgerDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("DRIVERLEDGER_SQL")
                   ?? throw new InvalidOperationException("Set DRIVERLEDGER_SQL env var for migrations.");

        var options = new DbContextOptionsBuilder<DriverLedgerDbContext>()
            .UseSqlServer(conn)
            .Options;

        return new DriverLedgerDbContext(options, new TenantProvider());
    }
}
