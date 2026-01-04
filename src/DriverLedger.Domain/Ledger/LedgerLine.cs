

namespace DriverLedger.Domain.Ledger
{
    public sealed class LedgerLine : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public required Guid LedgerEntryId { get; set; }
        public LedgerEntry? Entry { get; set; }

        public required Guid CategoryId { get; set; } // we will keep as Guid for now (seed later)
        public required decimal Amount { get; set; }  // positive expense amount (CAD)
        public required decimal GstHst { get; set; }  // ITC portion (CAD)
        public required decimal DeductiblePct { get; set; } // 0..1
        public string? Memo { get; set; }
        public string? AccountCode { get; set; }

        /// <summary>
        /// Ledger meaning:
        /// - "Income" => revenue
        /// - "Fee" => expense
        /// - "TaxCollected" => output tax line (Amount should be 0; GstHst holds tax)
        /// - "Itc" => input tax line (Amount should be 0; GstHst holds tax)
        /// - "Other" => ignored or treated as expense depending on snapshot rules
        /// </summary>
        public string LineType { get; init; } = "Fee";

        public List<LedgerSourceLink> SourceLinks { get; set; } = new();
    }
}
