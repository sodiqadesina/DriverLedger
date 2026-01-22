

namespace DriverLedger.Application.Statements.Messages
{
    /// <summary>
    /// Emitted when a reconciliation run has completed and the system may optionally
    /// post variance adjustments into the ledger so YTD aligns with the yearly statement.
    ///
    /// Note:
    /// - The yearly statement itself remains ReconciliationOnly and is never posted directly.
    /// - Adjustments are posted as a separate LedgerEntry with SourceType="Reconciliation".
    /// </summary>
    public sealed record ReconciliationCompleted(
        Guid ReconciliationRunId,
        Guid TenantId
    );
}
