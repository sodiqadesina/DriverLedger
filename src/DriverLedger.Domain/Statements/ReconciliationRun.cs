

namespace DriverLedger.Domain.Statements

{
    public sealed class ReconciliationRun : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }

        public required string Provider { get; set; }
        public required string PeriodType { get; set; } // Yearly
        public required string PeriodKey { get; set; }  // YYYY

        public Guid? YearlyStatementId { get; set; }

        public decimal MonthlyIncomeTotal { get; set; }
        public decimal YearlyIncomeTotal { get; set; }
        public decimal VarianceAmount { get; set; }

        public string Status { get; set; } = "Pending";

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }

        public List<ReconciliationVariance> Variances { get; set; } = new();
    }
}
