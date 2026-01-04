

namespace DriverLedger.Infrastructure.Options
{
    public sealed class DocumentIntelligenceOptions
    {
        public string Endpoint { get; init; } = "";
        public string ApiKey { get; init; } = "";

        public string ReceiptsModelId { get; init; } = "prebuilt-receipt";
        public string StatementsModelId { get; init; } = "prebuilt-invoice";
        public string StatementsMetadataModelId { get; init; } = "prebuilt-layout";
    }
}

