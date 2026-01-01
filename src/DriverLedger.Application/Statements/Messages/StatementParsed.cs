

namespace DriverLedger.Application.Statements.Messages
{
    public sealed record StatementParsed(
       Guid StatementId,
       Guid TenantId,
       string Provider,
       string PeriodType,
       string PeriodKey,
       int LineCount,
       decimal IncomeTotal,
       decimal FeeTotal,
       decimal TaxTotal
   );
}
