

namespace DriverLedger.Domain.Statements

{
    public sealed class StatementLine : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid StatementId { get; set; }

        public Statement Statement { get; set; } = default!;

        public DateOnly LineDate { get; set; }

        public required string LineType { get; set; } // Income, Fee, Tax
        public string? Description { get; set; }
        public string? Currency { get; set; }

        public decimal Amount { get; set; }
        public decimal? TaxAmount { get; set; }
    }
}
