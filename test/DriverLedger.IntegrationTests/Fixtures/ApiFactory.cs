using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Receipts.Extraction;
using DriverLedger.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;


namespace DriverLedger.IntegrationTests
{
    public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly string _connectionString;

        public ApiFactory(string connectionString) => _connectionString = connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Sql"] = _connectionString,

                    ["Auth:JwtKey"] = "dev_test_super_secret_key_32chars_minimum!!",
                    ["Auth:JwtIssuer"] = "driverledger-test",
                    ["Auth:JwtAudience"] = "driverledger-test",

                    ["Azure:BlobConnectionString"] = "UseDevelopmentStorage=true",
                    ["Azure:ServiceBusConnectionString"] =
                        "Endpoint=sb://test/;SharedAccessKeyName=test;SharedAccessKey=test"
                });
            });

            builder.ConfigureServices(services =>
            {
                // DB
                services.RemoveAll(typeof(DbContextOptions<DriverLedgerDbContext>));
                services.AddDbContext<DriverLedgerDbContext>(opt =>
                    opt.UseSqlServer(_connectionString));

                // Blob storage (test-friendly)
                services.RemoveAll<IBlobStorage>();
                services.AddSingleton<IBlobStorage, InMemoryBlobStorage>();

                // Messaging (capture)
                services.RemoveAll<IMessagePublisher>();
                services.AddSingleton<InMemoryMessagePublisher>();
                services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<InMemoryMessagePublisher>());

                // Receipt extraction (fake)
                services.RemoveAll<IReceiptExtractor>();
                services.AddSingleton<IReceiptExtractor, FakeReceiptExtractor>();
            });
        }

        // xUnit calls this before running tests that use this fixture
        public async Task InitializeAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
            await db.Database.MigrateAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                using var scope = Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
                await db.Database.EnsureDeletedAsync();
            }
            catch
            {
                // ignore cleanup failures
            }

            await base.DisposeAsync();
        }

        Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    }

}

