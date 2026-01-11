using DriverLedger.Application.Common;
using DriverLedger.Application.Statements.Reconciliation;

namespace DriverLedger.Api.Modules.Reconciliation
{
    public static class ApiReconciliation
    {
        public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/reconciliation")
                .WithTags("Reconciliation")
                .RequireAuthorization("RequireDriver");

            // POST /api/reconciliation/uber/{year}
            // Runs reconciliation: sum monthly Uber statements for {year} and compare to yearly Uber statement {year}.
            group.MapPost("/uber/{year:int}", async (
                int year,
                ITenantProvider tenantProvider,
                IStatementReconciliationService reconciliationService,
                DriverLedgerDbContext db,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;
                if (tenantId == Guid.Empty)
                    return Results.Unauthorized();

                if (year < 2000 || year > 2100)
                    return Results.BadRequest("Invalid year.");

                try
                {
                    var run = await reconciliationService.ReconcileUberYearAsync(tenantId, year, ct);

                    // Return with variances
                    var hydrated = await db.ReconciliationRuns
                        .AsNoTracking()
                        .Include(r => r.Variances)
                        .Where(r => r.Id == run.Id && r.TenantId == tenantId)
                        .Select(r => new
                        {
                            r.Id,
                            r.Provider,
                            r.PeriodType,
                            r.PeriodKey,
                            r.YearlyStatementId,
                            r.MonthlyIncomeTotal,
                            r.YearlyIncomeTotal,
                            r.VarianceAmount,
                            r.Status,
                            r.CreatedAt,
                            r.CompletedAt,
                            variances = r.Variances
                                .OrderBy(v => v.MetricKey)
                                .Select(v => new
                                {
                                    v.Id,
                                    v.MetricKey,
                                    v.MonthlyTotal,
                                    v.YearlyTotal,
                                    v.VarianceAmount,
                                    v.Notes
                                })
                                .ToList()
                        })
                        .FirstAsync(ct);

                    return Results.Ok(hydrated);
                }
                catch (InvalidOperationException ex)
                {
                    // e.g. yearly statement missing
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            // GET /api/reconciliation/runs
            group.MapGet("/runs", async (
                ITenantProvider tenantProvider,
                DriverLedgerDbContext db,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;
                if (tenantId == Guid.Empty)
                    return Results.Unauthorized();

                var items = await db.ReconciliationRuns
                    .AsNoTracking()
                    .Where(r => r.TenantId == tenantId)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new
                    {
                        r.Id,
                        r.Provider,
                        r.PeriodType,
                        r.PeriodKey,
                        r.YearlyStatementId,
                        r.MonthlyIncomeTotal,
                        r.YearlyIncomeTotal,
                        r.VarianceAmount,
                        r.Status,
                        r.CreatedAt,
                        r.CompletedAt
                    })
                    .ToListAsync(ct);

                return Results.Ok(items);
            });

            // GET /api/reconciliation/runs/{runId}
            group.MapGet("/runs/{runId:guid}", async (
                Guid runId,
                ITenantProvider tenantProvider,
                DriverLedgerDbContext db,
                CancellationToken ct) =>
            {
                var tenantId = tenantProvider.TenantId ?? Guid.Empty;
                if (tenantId == Guid.Empty)
                    return Results.Unauthorized();

                var run = await db.ReconciliationRuns
                    .AsNoTracking()
                    .Include(r => r.Variances)
                    .Where(r => r.Id == runId && r.TenantId == tenantId)
                    .Select(r => new
                    {
                        r.Id,
                        r.Provider,
                        r.PeriodType,
                        r.PeriodKey,
                        r.YearlyStatementId,
                        r.MonthlyIncomeTotal,
                        r.YearlyIncomeTotal,
                        r.VarianceAmount,
                        r.Status,
                        r.CreatedAt,
                        r.CompletedAt,
                        variances = r.Variances
                            .OrderBy(v => v.MetricKey)
                            .Select(v => new
                            {
                                v.Id,
                                v.MetricKey,
                                v.MonthlyTotal,
                                v.YearlyTotal,
                                v.VarianceAmount,
                                v.Notes
                            })
                            .ToList()
                    })
                    .FirstOrDefaultAsync(ct);

                return run is null ? Results.NotFound() : Results.Ok(run);
            });

            return app;
        }
    }
}
