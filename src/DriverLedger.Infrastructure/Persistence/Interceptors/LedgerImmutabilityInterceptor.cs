using DriverLedger.Domain.Ledger;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DriverLedger.Infrastructure.Persistence.Interceptors
{
    public sealed class LedgerImmutabilityInterceptor : SaveChangesInterceptor
    {
        private static bool IsLedgerType(object entity) =>
           entity is LedgerEntry || entity is LedgerLine;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            var ctx = eventData.Context;
            if (ctx is null) return result;

            foreach (var entry in ctx.ChangeTracker.Entries())
            {
                if (!IsLedgerType(entry.Entity)) continue;

                if (entry.State is EntityState.Modified or EntityState.Deleted)
                    throw new InvalidOperationException("Ledger is append-only. Use reversal + re-post; updates/deletes are forbidden.");
            }

            return result;
        }
    }
}
