

namespace DriverLedger.Domain.Files
{
    public sealed class FileObject : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public string BlobPath { get; set; } = default!;
        public string Sha256 { get; set; } = default!;
        public long Size { get; set; }
        public string ContentType { get; set; } = default!;
        public string OriginalName { get; set; } = default!;
        public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
        public string Source { get; set; } = "UserUpload";
    }
}
