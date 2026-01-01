

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

        public string Status { get; set; } = "Draft";

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public List<StatementLine> Lines { get; set; } = new();
        public decimal TotalAmount => Lines.Sum(line => line.Amount);
    }
}
