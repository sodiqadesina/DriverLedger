

namespace DriverLedger.Application.Receipts.Messages

{
    // Service Bus payload for ledger.posted.v1 (M1)
    public sealed record LedgerPosted(
        Guid LedgerEntryId,
        string SourceType,
        string SourceId,
        string EntryDate
    );
}
