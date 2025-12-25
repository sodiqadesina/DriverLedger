using DriverLedger.Application.Common;
using DriverLedger.Domain.Files;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriverLedger.IntegrationTests;

public sealed class TenantIsolationTests : IClassFixture<SqlConnectionFixture>
{
    private readonly SqlConnectionFixture _sql;
    public TenantIsolationTests(SqlConnectionFixture sql) => _sql = sql;

    [Fact]
    public async Task Tenant_scoped_queries_do_not_leak_across_tenants()
    {
        await using var factory = new ApiFactory(_sql.ConnectionString);
        using var scope = factory.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
        var tenantProvider = scope.ServiceProvider.GetRequiredService<ITenantProvider>();

        await db.Database.MigrateAsync();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Insert A
        tenantProvider.SetTenant(tenantA);
        db.FileObjects.Add(new FileObject
        {
            TenantId = tenantA,
            BlobPath = "a/path",
            Sha256 = new string('a', 64),
            Size = 10,
            ContentType = "application/pdf",
            OriginalName = "a.pdf",
            Source = "Test"
        });
        await db.SaveChangesAsync();

        // Insert B
        tenantProvider.SetTenant(tenantB);
        db.FileObjects.Add(new FileObject
        {
            TenantId = tenantB,
            BlobPath = "b/path",
            Sha256 = new string('b', 64),
            Size = 10,
            ContentType = "application/pdf",
            OriginalName = "b.pdf",
            Source = "Test"
        });
        await db.SaveChangesAsync();

        // Read as A
        tenantProvider.SetTenant(tenantA);
        (await db.FileObjects.CountAsync()).Should().Be(1);

        // Read as B
        tenantProvider.SetTenant(tenantB);
        (await db.FileObjects.CountAsync()).Should().Be(1);
    }
}
