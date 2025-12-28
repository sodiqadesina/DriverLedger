using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DriverLedger.Api.Modules.LiveStatement
{
    public static class ApiLiveStatement
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public static IEndpointRouteBuilder MapLiveStatement(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/live-statement").WithTags("live-statement")
                .RequireAuthorization("RequireDriver"); 

            // GET /live-statement?periodType=monthly&periodKey=YYYY-MM
            // GET /live-statement?periodType=ytd&periodKey=YYYY
            group.MapGet("", GetSnapshot);

            // GET /live-statement/timeline?periodType=monthly&year=YYYY
            group.MapGet("/timeline", GetTimeline);

            // GET /live-statement/drilldown?metricKey=...&periodType=monthly&periodKey=YYYY-MM
            group.MapGet("/drilldown", GetDrilldown);

            return app;
        }

        private static async Task<IResult> GetSnapshot(
            [FromQuery] string periodType,
            [FromQuery] string periodKey,
            DriverLedgerDbContext db,
            CancellationToken ct)
        {
            var pt = NormalizePeriodType(periodType);

            var snapshot = await db.LedgerSnapshots
                .AsNoTracking()
                .Include(s => s.Details)
                .SingleOrDefaultAsync(s => s.PeriodType == pt && s.PeriodKey == periodKey, ct);

            if (snapshot is null) return Results.NotFound();

            // TotalsJson is stored already (authoritative)
            var totals = JsonSerializer.Deserialize<JsonElement>(snapshot.TotalsJson, JsonOpts);

            var dto = new
            {
                snapshot.PeriodType,
                snapshot.PeriodKey,
                snapshot.CalculatedAt,
                snapshot.AuthorityScore,
                snapshot.EvidencePct,
                snapshot.EstimatedPct,
                Totals = totals,
                Details = snapshot.Details
                    .OrderBy(d => d.MetricKey)
                    .Select(d => new
                    {
                        d.MetricKey,
                        d.Value,
                        d.EvidencePct,
                        d.EstimatedPct
                    })
            };

            return Results.Ok(dto);
        }

        private static async Task<IResult> GetTimeline(
            [FromQuery] string periodType,
            [FromQuery] int year,
            DriverLedgerDbContext db,
            CancellationToken ct)
        {
            var pt = NormalizePeriodType(periodType);

            if (pt != "Monthly")
                return Results.BadRequest(new ProblemDetails { Title = "timeline supports periodType=monthly only (M1)" });

            var prefix = $"{year:D4}-";

            var items = await db.LedgerSnapshots
                .AsNoTracking()
                .Where(s => s.PeriodType == "Monthly" && s.PeriodKey.StartsWith(prefix))
                .OrderBy(s => s.PeriodKey)
                .Select(s => new
                {
                    s.PeriodKey,
                    s.CalculatedAt,
                    s.AuthorityScore,
                    s.EvidencePct,
                    s.EstimatedPct,
                    s.TotalsJson
                })
                .ToListAsync(ct);

            var dto = items.Select(x => new
            {
                x.PeriodKey,
                x.CalculatedAt,
                x.AuthorityScore,
                x.EvidencePct,
                x.EstimatedPct,
                Totals = JsonSerializer.Deserialize<JsonElement>(x.TotalsJson, JsonOpts)
            });

            return Results.Ok(dto);
        }

        private static async Task<IResult> GetDrilldown(
            [FromQuery] string metricKey,
            [FromQuery] string periodType,
            [FromQuery] string periodKey,
            DriverLedgerDbContext db,
            CancellationToken ct)
        {
            // M1 Drilldown: return ledger lines for the period + basic evidence flags.
            // MetricKey can be "ExpensesTotal", "ItcTotal", "NetTax" etc.
            var pt = NormalizePeriodType(periodType);
            var (start, endExclusive) = GetRange(pt, periodKey);

            var lines = await (
                from e in db.LedgerEntries.AsNoTracking()
                join l in db.LedgerLines.AsNoTracking() on e.Id equals l.LedgerEntryId
                where e.EntryDate >= start && e.EntryDate < endExclusive
                select new
                {
                    LedgerEntryId = e.Id,
                    e.EntryDate,
                    e.SourceType,
                    e.SourceId,
                    l.CategoryId,
                    l.Amount,
                    l.GstHst,
                    l.DeductiblePct,
                    l.Memo,
                    l.AccountCode
                }
            ).ToListAsync(ct);

            // For M1, map metricKey to a filter:
            // ExpensesTotal => all lines
            // ItcTotal => lines with GstHst > 0
            // NetTax => same as ItcTotal (since M1 NetTax = -ITC)
            var filtered = metricKey switch
            {
                "ExpensesTotal" => lines,
                "ItcTotal" => lines.Where(x => x.GstHst != 0m).ToList(),
                "NetTax" => lines.Where(x => x.GstHst != 0m).ToList(),
                _ => lines
            };

            return Results.Ok(new
            {
                metricKey,
                periodType = pt,
                periodKey,
                count = filtered.Count,
                items = filtered
            });
        }

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
