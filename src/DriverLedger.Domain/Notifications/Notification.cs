

namespace DriverLedger.Domain.Notifications
{
    public sealed class Notification : Entity, ITenantScoped
    {
        public required Guid TenantId { get; set; }

        public required string Type { get; set; } // ReceiptHold, ReceiptPosted, SnapshotUpdated
        public required string Severity { get; set; } // Info, Warning, Error
        public required string Title { get; set; }
        public required string Body { get; set; }

        public required string DataJson { get; set; } = "{}";

        public required string Status { get; set; } = "New"; // New/Read
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ReadAt { get; set; }
    }
}
