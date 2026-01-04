namespace DriverLedger.Domain.Statements
{
    public sealed class Statement : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid FileObjectId { get; set; }

        public required string Provider { get; set; }
        public required string PeriodType { get; set; } // Monthly, Yearly
        public required string PeriodKey { get; set; }  // YYYY-MM or YYYY

        public DateOnly PeriodStart { get; set; }
        public DateOnly PeriodEnd { get; set; }

        public string? VendorName { get; set; }

        // ===== Statement-level currency normalization =====
        public string? CurrencyCode { get; set; } // ISO: CAD, USD
        public string CurrencyEvidence { get; set; } = "Inferred"; // Extracted | Inferred

        public decimal? StatementTotalAmount { get; set; }
        public decimal? TaxAmount { get; set; }

        public string Status { get; set; } = "Draft";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public List<StatementLine> Lines { get; set; } = new();

        // Monetary total only (metrics excluded)
        public decimal TotalAmount => Lines.Sum(l => l.MoneyAmount ?? 0m);
    }
}
