using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DriverLedger.IntegrationTests.Fixtures;

public sealed class BlobContainerStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            var cfg = app.ApplicationServices.GetRequiredService<IConfiguration>();
            var logger = app.ApplicationServices.GetRequiredService<ILogger<BlobContainerStartupFilter>>();

            var conn = cfg["Azure:BlobConnectionString"];
            if (string.IsNullOrWhiteSpace(conn))
            {
                logger.LogWarning("BlobConnectionString missing. Skipping blob container init.");
                next(app);
                return;
            }

            try
            {
                var service = new BlobServiceClient(conn);
                var container = service.GetBlobContainerClient("receipts");
                container.CreateIfNotExists();
                logger.LogInformation("Test blob container ensured.");
            }
            catch (Exception ex)
            {
                //  THIS IS THE KEY PART
                logger.LogWarning(ex, "Blob storage not available. Skipping container creation.");
            }

            next(app);
        };
    }
}
