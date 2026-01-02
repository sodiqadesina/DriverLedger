

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    public sealed record StatementLineNormalized(
    DateOnly LineDate,
    string LineType,
    string? Description,
    string? Currency,
    decimal Amount,
    decimal? TaxAmount
);
}
