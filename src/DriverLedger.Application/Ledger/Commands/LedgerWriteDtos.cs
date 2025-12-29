

namespace DriverLedger.Application.Ledger.Commands
{
    public sealed record ManualLedgerEntryRequest(
     DateOnly EntryDate,
     List<ManualLedgerLineRequest> Lines,
     string? Memo,
     string? IdempotencyKey
 );

    public sealed record ManualLedgerLineRequest(
      Guid CategoryId,
      decimal Amount,
      decimal? GstHst,
      decimal? DeductiblePct,
      string? Memo,
      string? AccountCode
  );
    public sealed record AdjustmentRequest(
        Guid ReverseEntryId,
        ManualLedgerEntryRequest Corrected
    );
}
