namespace DriverLedger.Api.Modules.Receipts
{
    public sealed class ResolveReceiptReviewRequest
    {
        public string ResolutionJson { get; set; } = "{}"; // user fixes, fields etc.
        public bool Resubmit { get; set; } = true;
    }
}
