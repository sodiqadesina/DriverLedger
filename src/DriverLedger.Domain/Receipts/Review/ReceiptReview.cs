
namespace DriverLedger.Domain.Receipts.Review
{
    public sealed class ReceiptReview : Entity, ITenantScoped
    {
        public required Guid TenantId { get; set; }
        public required Guid ReceiptId { get; set; }

        public required string HoldReason { get; set; } // e.g. LowConfidence, MissingFields
        public required string QuestionsJson { get; set; } = "{}"; // what user must fix
        public string? ResolutionJson { get; set; }

        public Guid? ResolvedByUserId { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }

        public Receipt? Receipt { get; set; }
    }
}
