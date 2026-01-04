

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    public sealed record StatementMetadataResult
    {
        public string? Provider { get; init; }
        public string? PeriodType { get; init; }
        public string? PeriodKey { get; init; }
        public DateOnly? PeriodStart { get; init; }
        public DateOnly? PeriodEnd { get; init; }

        public string? VendorName { get; init; }
        public decimal? StatementTotalAmount { get; init; }
        public decimal? TaxAmount { get; init; }
        public string? Currency { get; init; }
    }
}
