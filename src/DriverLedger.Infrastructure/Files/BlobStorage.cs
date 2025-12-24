using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using DriverLedger.Application.Common;

namespace DriverLedger.Infrastructure.Files
{
    public interface IBlobStorage
    {
        Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct);
    }

    public sealed class BlobStorage : IBlobStorage
    {
        private readonly BlobContainerClient _container;

        public BlobStorage(BlobServiceClient blobServiceClient)
        {
            _container = blobServiceClient.GetBlobContainerClient("driverledger");
            _container.CreateIfNotExists();
        }

        public async Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
        {
            var blob = _container.GetBlobClient(blobPath);
            await blob.UploadAsync(content, overwrite: true, cancellationToken: ct);
            await blob.SetHttpHeadersAsync(new() { ContentType = contentType }, cancellationToken: ct);
        }
    }
}
