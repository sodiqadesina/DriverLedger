namespace DriverLedger.Infrastructure.Statements.Extraction
{
    public interface IStatementExtractor
    {
        bool CanHandleContentType(string contentType);

        /// <summary>
        /// Extract normalized monetary + metric lines.
        /// Metric lines MUST be returned as IsMetric=true and should not have MoneyAmount.
        /// </summary>
        Task<IReadOnlyList<StatementLineNormalized>> ExtractAsync(Stream file, CancellationToken ct);

        string ModelVersion { get; }
    }
}
