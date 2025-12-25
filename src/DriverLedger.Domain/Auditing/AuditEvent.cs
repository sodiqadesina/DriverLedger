

namespace DriverLedger.Domain.Auditing
{

    public sealed class AuditEvent : ITenantScoped
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }

        public string ActorUserId { get; set; } = default!;   // "system" for functions
        public string Action { get; set; } = default!;        // e.g. "file.uploaded"
        public string EntityType { get; set; } = default!;    // e.g. "FileObject"
        public string EntityId { get; set; } = default!;      // Guid.ToString("D")
        public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

        public string CorrelationId { get; set; } = default!;
        public string? MetadataJson { get; set; }             // extra info payload
    }

}

