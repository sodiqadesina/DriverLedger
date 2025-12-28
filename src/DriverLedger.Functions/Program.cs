using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DriverLedger.Application.Common;
using DriverLedger.Application.Receipts;
using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Infrastructure.Common;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Receipts;
using DriverLedger.Infrastructure.Receipts.Extraction;
using DriverLedger.Infrastructure.Statements.Snapshots;
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

        // Azure clients
        services.AddSingleton(_ => new ServiceBusClient(ctx.Configuration["Azure:ServiceBusConnectionString"]));
        services.AddSingleton(_ => new BlobServiceClient(ctx.Configuration["Azure:BlobConnectionString"]));

        // Infra services
        services.AddSingleton<IBlobStorage, BlobStorage>();
        services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();
        services.AddSingleton<ServiceBusPublisher>(); // your handler uses concrete type
        services.AddScoped<SnapshotCalculator>();

        // Receipt extractor: start with Fake
        // Swap to AzureDocumentIntelligenceReceiptExtractor later
        services.AddSingleton<IReceiptExtractor, FakeReceiptExtractor>();

        // Handlers
        services.AddScoped<IReceiptReceivedHandler, ReceiptReceivedHandler>();
        services.AddScoped<ReceiptExtractionHandler>();
    })
    .Build();

host.Run();
