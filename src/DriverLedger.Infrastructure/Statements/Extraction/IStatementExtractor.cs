

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    public interface IStatementExtractor
    {
        bool CanHandleContentType(string contentType);
        Task<IReadOnlyList<StatementLineNormalized>> ExtractAsync(Stream file, CancellationToken ct);
        string ModelVersion { get; }
    }
}
