
namespace DriverLedger.Application.Statements.Messages
{
    public sealed record StatementReceived(
        Guid StatementId,
        Guid TenantId,
        string Provider,
        string PeriodType,
        string PeriodKey,
        Guid FileObjectId,
        DateOnly PeriodStart,
        DateOnly PeriodEnd
    );

}
