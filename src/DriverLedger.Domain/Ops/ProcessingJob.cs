

namespace DriverLedger.Domain.Ops
{
    public sealed class ProcessingJob : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public string JobType { get; set; } = default!;
        public string DedupeKey { get; set; } = default!; // unique per tenant+jobtype
        public string Status { get; set; } = "Started";  // Started/Succeeded/Failed
        public int Attempts { get; set; } = 0;
        public string? LastError { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
