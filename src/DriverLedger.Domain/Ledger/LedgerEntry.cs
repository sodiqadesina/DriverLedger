

namespace DriverLedger.Domain.Ledger
{
    public sealed class LedgerEntry : Entity, ITenantScoped
    {
        public required Guid TenantId { get; set; }
        public required DateOnly EntryDate { get; set; }

        public required string SourceType { get; set; } // Receipt, Manual, Adjustment
        public required string SourceId { get; set; }   // receiptId etc.

        public Guid? PostedByUserId { get; set; }
        public required string PostedByType { get; set; } // Driver, Admin, System

        public required string CorrelationId { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public List<LedgerLine> Lines { get; set; } = new();
    }

}
