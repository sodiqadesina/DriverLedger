
namespace DriverLedger.Domain.Ledger
{
    public sealed class LedgerSourceLink : Entity
    {
        public required Guid LedgerLineId { get; set; }
        public LedgerLine LedgerLine { get; set; } = default!;

        public Guid? ReceiptId { get; set; }
        public Guid? StatementLineId { get; set; }
        public Guid? FileObjectId { get; set; }
    }
}
