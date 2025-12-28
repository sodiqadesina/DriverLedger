
using DriverLedger.Application.Receipts.Extraction;

namespace DriverLedger.Infrastructure.Receipts.Extraction
{

    public sealed class AzureDocumentIntelligenceReceiptExtractor : IReceiptExtractor
    {
        public string ModelVersion => "azure-di-prebuilt-receipt-v1";

        public async Task<NormalizedReceipt> ExtractAsync(Stream file, CancellationToken ct)
        {
            // TODO: Implement using Azure.AI.FormRecognizer.DocumentAnalysis (DocumentAnalysisClient)
            // Return RawJson as serialized result, and normalized fields from DI.

            //remove this test stub
            var testresult = Task.FromResult(new NormalizedReceipt(
                Date: DateOnly.FromDateTime(DateTime.UtcNow.Date),
                Vendor: "Azure DI Vendor",
                Total: 100.00m,
                Tax: 13.00m,
                Currency: "CAD",
                RawJson: "{\"azure_di\":true}"
            ));

            var test = await testresult;

            throw new NotImplementedException("Wire Azure Document Intelligence here.");
        }
    }
}
