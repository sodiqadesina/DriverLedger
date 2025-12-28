

namespace DriverLedger.Domain.Ledger
{
    public sealed class LedgerLine : Entity
    {
        public required Guid LedgerEntryId { get; set; }
        public LedgerEntry? Entry { get; set; }

        public required Guid CategoryId { get; set; } // we will keep as Guid for now (seed later)
        public required decimal Amount { get; set; }  // positive expense amount (CAD)
        public required decimal GstHst { get; set; }  // ITC portion (CAD)
        public required decimal DeductiblePct { get; set; } // 0..1
        public string? Memo { get; set; }
        public string? AccountCode { get; set; }

        public List<LedgerSourceLink> SourceLinks { get; set; } = new();
    }
}
