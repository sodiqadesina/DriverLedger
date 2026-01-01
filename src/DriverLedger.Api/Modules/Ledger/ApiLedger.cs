using DriverLedger.Application.Ledger.Commands;
using DriverLedger.Infrastructure.Ledger;
using Microsoft.AspNetCore.Mvc;

namespace DriverLedger.Api.Modules.Ledger

{
    public static class ApiLedger
    {
        public static IEndpointRouteBuilder MapLedger(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/ledger")
                .RequireAuthorization()
                .WithTags("ledger")
                .RequireAuthorization("RequireDriver");

            // GET /ledger?periodType=monthly&periodKey=YYYY-MM
            group.MapGet("", GetLedgerForPeriod);

            // GET /ledger/{entryId}
            group.MapGet("/{entryId:guid}", GetLedgerEntry);

            // GET /ledger/audit?sourceType=Receipt&sourceId=...
            group.MapGet("/audit", GetLedgerAudit);

            // POST /ledger/manual  (M1.3 CreateManualEntry)
            group.MapPost("/manual", CreateManualEntry);

            // POST /ledger/adjustments (M1.3 CreateAdjustment)
            group.MapPost("/adjustments", CreateAdjustment);

            return app;
        }

        private static async Task<IResult> GetLedgerForPeriod(
            [FromQuery] string periodType,
            [FromQuery] string periodKey,
            DriverLedgerDbContext db,
            CancellationToken ct)
        {
            var pt = NormalizePeriodType(periodType);
            var (start, endExclusive) = GetRange(pt, periodKey);

            var entries = await db.LedgerEntries
                .AsNoTracking()
                .Where(e => e.EntryDate >= start && e.EntryDate < endExclusive)
                .OrderByDescending(e => e.EntryDate)
                .Select(e => new
                {
                    e.Id,
                    e.EntryDate,
                    e.SourceType,
                    e.SourceId,
                    e.PostedByType,
                    e.CorrelationId,
                    e.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(entries);
        }

        private static async Task<IResult> GetLedgerEntry(
            Guid entryId,
            DriverLedgerDbContext db,
            CancellationToken ct)
        {
            var entry = await db.LedgerEntries
                .AsNoTracking()
                .Include(e => e.Lines)
                .SingleOrDefaultAsync(e => e.Id == entryId, ct);

            if (entry is null) return Results.NotFound();

            var lines = entry.Lines.Select(l => new
            {
                l.Id,
                l.CategoryId,
                l.Amount,
                l.GstHst,
                l.DeductiblePct,
                l.Memo,
                l.AccountCode,
                SourceLinks = l.SourceLinks.Select(s => new { s.ReceiptId, s.StatementLineId, s.FileObjectId })
            });

            return Results.Ok(new
            {
                entry.Id,
                entry.EntryDate,
                entry.SourceType,
                entry.SourceId,
                entry.PostedByType,
                entry.CorrelationId,
                entry.CreatedAt,
                Lines = lines
            });
        }

        private static async Task<IResult> GetLedgerAudit(
            [FromQuery] string sourceType,
            [FromQuery] string sourceId,
            DriverLedgerDbContext db,
            CancellationToken ct)
        {
            var events = await db.AuditEvents
                .AsNoTracking()
                .Where(a => a.EntityType == "LedgerEntry" || a.EntityType == "Receipt")
                .Where(a => a.MetadataJson != null && a.MetadataJson.Contains(sourceId))
                .OrderByDescending(a => a.OccurredAt)
                .Select(a => new
                {
                    a.Id,
                    a.Action,
                    a.EntityType,
                    a.EntityId,
                    a.OccurredAt,
                    a.CorrelationId,
                    a.ActorUserId,
                    a.MetadataJson
                })
                .ToListAsync(ct);

            return Results.Ok(new { sourceType, sourceId, events });
        }

        // ---------------- Writes (M1.3 “public use-cases”) ----------------

        private static async Task<IResult> CreateManualEntry(
            ManualLedgerEntryRequest req,
            HttpContext http,
            ManualLedgerPostingHandler handler,
            CancellationToken ct)
        {
            var correlationId =
                (http.Items.TryGetValue("x-correlation-id", out var v) ? v?.ToString() : null)
                ?? Guid.NewGuid().ToString("N");

            var result = await handler.CreateManualAsync(req, correlationId, ct);
            return Results.Ok(result);
        }

        private static async Task<IResult> CreateAdjustment(
            AdjustmentRequest req,
            HttpContext http,
            AdjustmentLedgerPostingHandler handler,
            CancellationToken ct)
        {
            var correlationId =
                (http.Items.TryGetValue("x-correlation-id", out var v) ? v?.ToString() : null)
                ?? Guid.NewGuid().ToString("N");

            var result = await handler.CreateAdjustmentAsync(req, correlationId, ct);
            return Results.Ok(result);
        }


        // ---------------- Helpers ----------------

        private static string NormalizePeriodType(string periodType)
        {
            return periodType.Trim().ToLowerInvariant() switch
            {
                "monthly" => "Monthly",
                "ytd" => "YTD",
                _ => throw new ArgumentException("periodType must be monthly or ytd")
            };
        }

        private static (DateOnly start, DateOnly endExclusive) GetRange(string periodType, string periodKey)
        {
            if (periodType == "Monthly")
            {
                var year = int.Parse(periodKey[..4]);
                var month = int.Parse(periodKey[5..7]);
                var start = new DateOnly(year, month, 1);
                return (start, start.AddMonths(1));
            }

            if (periodType == "YTD")
            {
                var year = int.Parse(periodKey);
                var start = new DateOnly(year, 1, 1);
                return (start, start.AddYears(1));
            }

            throw new InvalidOperationException($"Unknown periodType '{periodType}'.");
        }
    }
}
