using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using DriverLedger.Application.Common;
using DriverLedger.Application.Receipts;
using DriverLedger.Application.Receipts.Extraction;
using DriverLedger.Infrastructure.Common;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Ledger;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Options;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Receipts;
using DriverLedger.Infrastructure.Receipts.Extraction;
using DriverLedger.Infrastructure.Statements.Extraction;
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

        // Azure clients (with checks)
        services.AddSingleton(_ =>
        {
            var cs = ctx.Configuration["Azure:ServiceBusConnectionString"];
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("Missing configuration: Azure:ServiceBusConnectionString");
            return new ServiceBusClient(cs);
        });

        services.AddSingleton(_ =>
        {
            var cs = ctx.Configuration["Azure:BlobConnectionString"];
            if (string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("Missing configuration: Azure:BlobConnectionString");
            return new BlobServiceClient(cs);
        });

        // Infra services
        services.AddSingleton<IBlobStorage, BlobStorage>();

        // single instance for both concrete + interface
        services.AddSingleton<ServiceBusPublisher>();
        services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<ServiceBusPublisher>());

        services.AddScoped<SnapshotCalculator>();

        // Receipt extractor: REAL (options + scoped)
        services.Configure<DocumentIntelligenceOptions>(
            ctx.Configuration.GetSection("Azure:DocumentIntelligence"));

        services.AddScoped<IReceiptExtractor, AzureDocumentIntelligenceReceiptExtractor>();
        services.AddScoped<IStatementExtractor, AzureDocumentIntelligenceStatementExtractor>();
        services.AddScoped<IStatementExtractor, CsvStatementExtractor>();

        // Handlers
        services.AddScoped<IReceiptReceivedHandler, ReceiptReceivedHandler>();
        services.AddScoped<ReceiptExtractionHandler>();
        services.AddScoped<StatementExtractionHandler>();
        services.AddScoped<ReceiptToLedgerPostingHandler>();
        services.AddScoped<StatementToLedgerPostingHandler>();

    })
    .Build();

host.Run();
