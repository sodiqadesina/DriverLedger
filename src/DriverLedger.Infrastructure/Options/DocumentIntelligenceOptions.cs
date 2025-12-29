

namespace DriverLedger.Infrastructure.Options
{
    public sealed class DocumentIntelligenceOptions
    {
        public string Endpoint { get; init; } = "";
        public string ApiKey { get; init; } = "";

        // Optional (defaults used if empty)
        public string ModelId { get; init; } = "prebuilt-receipt";
    }
}

