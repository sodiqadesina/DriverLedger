
namespace DriverLedger.Domain.Statements.Snapshots
{
    public sealed class LedgerSnapshot : Entity, ITenantScoped
    {
        public required Guid TenantId { get; set; }

        public required string PeriodType { get; set; } // Monthly, YTD (M1)
        public required string PeriodKey { get; set; }  // YYYY-MM or YYYY

        public required DateTimeOffset CalculatedAt { get; set; }

        public required int AuthorityScore { get; set; }  // 0..100
        public required decimal EvidencePct { get; set; } // 0..1
        public required decimal EstimatedPct { get; set; } // 0..1

        public required string TotalsJson { get; set; } = "{}";

        public List<SnapshotDetail> Details { get; set; } = new();
    }
}
