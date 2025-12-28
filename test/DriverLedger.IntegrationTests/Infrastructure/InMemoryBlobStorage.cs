using DriverLedger.Infrastructure.Files;



namespace DriverLedger.IntegrationTests.Infrastructure
{
    public sealed class InMemoryBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _store = new();

        public Task UploadAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _store[blobPath] = (ms.ToArray(), contentType);
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct)
        {
            if (!_store.TryGetValue(blobPath, out var item))
                throw new FileNotFoundException($"Blob not found in InMemoryBlobStorage: {blobPath}");

            return Task.FromResult<Stream>(new MemoryStream(item.Bytes, writable: false));
        }
    }
}
