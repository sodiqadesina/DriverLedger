

namespace DriverLedger.Infrastructure.Statements.Extraction

{public interface IStatementMetadataExtractor
    {
        bool CanHandleContentType(string contentType);
        Task<StatementMetadataResult> ExtractAsync(Stream file, CancellationToken ct);
        string ModelVersion { get; }
    }
}
