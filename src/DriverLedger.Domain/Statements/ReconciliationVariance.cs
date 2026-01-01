

namespace DriverLedger.Domain.Statements
{
    public sealed class ReconciliationVariance : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid ReconciliationRunId { get; set; }

        public ReconciliationRun ReconciliationRun { get; set; } = default!;

        public required string MetricKey { get; set; }
        public decimal MonthlyTotal { get; set; }
        public decimal YearlyTotal { get; set; }
        public decimal VarianceAmount { get; set; }
        public string? Notes { get; set; }
    }
}
