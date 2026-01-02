
namespace DriverLedger.Application.Statements.Messages
{
    public sealed record StatementReconcileRequested(
    Guid StatementId,
    Guid TenantId
);
}
