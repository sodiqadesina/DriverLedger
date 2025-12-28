
namespace DriverLedger.Application.Messaging.Events
{
       public sealed record LedgerPostedV1(
        Guid LedgerEntryId,
        string SourceType,
        string SourceId,
        DateOnly EntryDate
    );

}
