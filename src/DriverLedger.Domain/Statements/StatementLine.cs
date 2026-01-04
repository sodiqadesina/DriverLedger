namespace DriverLedger.Domain.Statements
{
    public sealed class StatementLine : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid StatementId { get; set; }

        public Statement Statement { get; set; } = default!;

        public DateOnly LineDate { get; set; }

        // Monetary classifications: Income, Fee, Expense, TaxCollected, Itc, Other, Metric
        public required string LineType { get; set; }

        public string? Description { get; set; }

        // ===== Currency normalization =====
        public string? CurrencyCode { get; set; } // ISO: CAD, USD, ...
        public string CurrencyEvidence { get; set; } = "Inferred"; // Extracted | Inferred

        // ===== Classification evidence =====
        public string ClassificationEvidence { get; set; } = "Inferred"; // Extracted | Inferred

        // ===== Metric support =====
        public bool IsMetric { get; set; }
        public string? MetricKey { get; set; }   // RideKilometers, Trips, RideMiles, ...
        public decimal? MetricValue { get; set; }
        public string? Unit { get; set; }        // km, mi, trips, etc.

        // ===== Monetary support =====
        // Only set for Income/Fee/Expense. For Metric lines keep null.
        public decimal? MoneyAmount { get; set; }

        // Tax fields (kept separate so Income/Fee can have tax evidence too)
        public decimal? TaxAmount { get; set; }
    }
}
