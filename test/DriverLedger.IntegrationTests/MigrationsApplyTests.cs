using DriverLedger.Infrastructure.Persistence;
using DriverLedger.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriverLedger.IntegrationTests;

public sealed class MigrationsApplyTests : IClassFixture<SqlConnectionFixture>
{
    private readonly SqlConnectionFixture _sql;

    public MigrationsApplyTests(SqlConnectionFixture sql) => _sql = sql;

    [Fact]
    public async Task Database_migrations_apply_successfully()
    {
        await using var factory = new ApiFactory(_sql.ConnectionString);
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
        await db.Database.MigrateAsync();

        // Confirm EF migrations history table exists
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name='__EFMigrationsHistory'";

        var result = await cmd.ExecuteScalarAsync();
        var count = result is not null ? Convert.ToInt32(result) : 0;

        count.Should().Be(1);
    }
}
