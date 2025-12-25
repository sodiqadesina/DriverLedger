

namespace DriverLedger.Domain.Receipts
{
    public sealed class Receipt : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid FileObjectId { get; set; }

        public string Status { get; set; } = "Draft";
        // Draft → Submitted → Processing → Hold/Posted/Failed

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
