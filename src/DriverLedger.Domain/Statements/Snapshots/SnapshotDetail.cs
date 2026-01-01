

namespace DriverLedger.Domain.Statements.Snapshots
{
    public sealed class SnapshotDetail : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public required Guid SnapshotId { get; set; }
        public LedgerSnapshot? Snapshot { get; set; }

        public required string MetricKey { get; set; } // ExpensesTotal, ItcTotal, NetTax
        public required decimal Value { get; set; }

        public required decimal EvidencePct { get; set; }
        public required decimal EstimatedPct { get; set; }
       
    }
}
