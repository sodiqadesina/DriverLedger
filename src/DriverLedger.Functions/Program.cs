using Azure.Messaging.ServiceBus;
using DriverLedger.Application.Common;
using DriverLedger.Infrastructure.Common;
using DriverLedger.Infrastructure.Persistence;
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

        services.AddDbContext<DriverLedgerDbContext>(opt =>
            opt.UseSqlServer(ctx.Configuration.GetConnectionString("Sql")));

        services.AddSingleton(_ => new ServiceBusClient(ctx.Configuration["Azure:ServiceBusConnectionString"]));
    })
    .Build();

host.Run();
