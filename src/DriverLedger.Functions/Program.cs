using Azure.Messaging.ServiceBus;
using DriverLedger.Application.Common;
using DriverLedger.Application.Receipts;
using DriverLedger.Infrastructure.Common;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((ctx, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();

        services.AddScoped<ITenantProvider, TenantProvider>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddDbContext<DriverLedgerDbContext>(opt =>
            opt.UseSqlServer(ctx.Configuration.GetConnectionString("Sql")));

        // This is not required as we will not use ServiceBusClient manually.
        // ServiceBusTrigger uses the connection setting automatically.
        services.AddSingleton(_ => new ServiceBusClient(ctx.Configuration["Azure:ServiceBusConnectionString"]));

        services.AddScoped<IReceiptReceivedHandler, ReceiptReceivedHandler>();
    })
    .Build();

host.Run();
