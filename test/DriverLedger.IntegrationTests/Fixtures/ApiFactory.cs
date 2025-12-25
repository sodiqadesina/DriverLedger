using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DriverLedger.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public ApiFactory(string connectionString)
        => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            // Deterministic configuration for integration tests
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sql"] = _connectionString,

                // JWT settings (must match JwtBearer validation)
                ["Auth:JwtKey"] = "dev_test_super_secret_key_32chars_minimum!!",
                ["Auth:JwtIssuer"] = "driverledger-test",
                ["Auth:JwtAudience"] = "driverledger-test",

                // Azure config (present but never actually used)
                ["Azure:BlobConnectionString"] = "UseDevelopmentStorage=true",
                ["Azure:ServiceBusConnectionString"] =
                    "Endpoint=sb://test/;SharedAccessKeyName=test;SharedAccessKey=test"
            };

            cfg.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            // -------------------------------
            // DATABASE (real SQL for tests)
            // -------------------------------
            services.RemoveAll(typeof(DbContextOptions<DriverLedgerDbContext>));

            services.AddDbContext<DriverLedgerDbContext>(opt =>
                opt.UseSqlServer(_connectionString));

            // -------------------------------
            // MESSAGING (NO-OP for tests)
            // -------------------------------
            // Prevent Azure Service Bus calls during integration tests
            services.RemoveAll<IMessagePublisher>();
            services.AddScoped<IMessagePublisher, NoOpMessagePublisher>();
        });
    }
}
