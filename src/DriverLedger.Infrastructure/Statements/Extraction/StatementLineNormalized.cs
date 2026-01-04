namespace DriverLedger.Infrastructure.Statements.Extraction
{
    /// <summary>
    /// Normalized line output from statement extractors.
    /// Supports both monetary and non-posting metric lines (e.g., RideKilometers).
    /// </summary>
    public sealed record StatementLineNormalized(
          DateOnly LineDate,
        string LineType,
        string? Description,

        // currency normalization
        string? CurrencyCode,
        string CurrencyEvidence,

        // classification evidence
        string ClassificationEvidence,

        // metric support
        bool IsMetric,
        string? MetricKey,
        decimal? MetricValue,
        string? Unit,

        // monetary support
        decimal? MoneyAmount,
        decimal? TaxAmount
    );
}
