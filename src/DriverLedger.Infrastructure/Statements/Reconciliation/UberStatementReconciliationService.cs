using DriverLedger.Application.Statements.Reconciliation;
using DriverLedger.Domain.Statements;
using DriverLedger.Infrastructure.Persistence;
namespace DriverLedger.Infrastructure.Statements.Reconciliation;

/// <summary>
/// MVP reconciliation engine:
/// - Only Uber
/// - Compares Yearly statement (PeriodType=Yearly, PeriodKey=YYYY)
///   with the sum of all Monthly statements in that year (PeriodType=Monthly, PeriodStart.Year=YYYY)
/// - Uses a small allowlist of stable Uber line descriptions + one metric key.
/// </summary>
public sealed class UberStatementReconciliationService : IStatementReconciliationService
{
    private const string ProviderUber = "Uber";
    private const string PeriodTypeYearly = "Yearly";
    private const string PeriodTypeMonthly = "Monthly";

    private readonly DriverLedgerDbContext _db;

    public UberStatementReconciliationService(DriverLedgerDbContext db) => _db = db;

    public async Task<ReconciliationRun> ReconcileUberYearAsync(Guid tenantId, int year, CancellationToken ct = default)
    {
        var yearKey = year.ToString();

        // 1) Yearly statement is required
        var yearlyStatementId = await _db.Statements
            .AsNoTracking()
            .Where(s =>
                s.TenantId == tenantId &&
                s.Provider == ProviderUber &&
                s.PeriodType == PeriodTypeYearly &&
                s.PeriodKey == yearKey)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        if (yearlyStatementId is null)
        {
            throw new InvalidOperationException(
                $"Uber yearly statement not found for {yearKey}. " +
                $"Expected Provider='{ProviderUber}', PeriodType='{PeriodTypeYearly}', PeriodKey='{yearKey}'.");
        }

        // 2) Monthly statements for the year
        var monthlyStatementIds = await _db.Statements
            .AsNoTracking()
            .Where(s =>
                s.TenantId == tenantId &&
                s.Provider == ProviderUber &&
                s.PeriodType == PeriodTypeMonthly &&
                s.PeriodStart.Year == year)
            .Select(s => s.Id)
            .ToListAsync(ct);

        // 3) Load lines (money + metrics)
        var yearlyLines = await _db.StatementLines
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.StatementId == yearlyStatementId.Value)
            .Select(l => new LineRow(
                l.LineType,
                l.Description,
                l.IsMetric,
                l.MetricKey,
                l.MetricValue,
                l.MoneyAmount))
            .ToListAsync(ct);

        var monthlyLines = monthlyStatementIds.Count == 0
            ? new List<LineRow>()
            : await _db.StatementLines
                .AsNoTracking()
                .Where(l => l.TenantId == tenantId && monthlyStatementIds.Contains(l.StatementId))
                .Select(l => new LineRow(
                    l.LineType,
                    l.Description,
                    l.IsMetric,
                    l.MetricKey,
                    l.MetricValue,
                    l.MoneyAmount))
                .ToListAsync(ct);

        // 4) Metric allowlist for MVP
        // NOTE: We reconcile by:
        // - money lines: match (LineType + Description)
        // - metric lines: match (MetricKey)
        var defs = new MetricDef[]
        {
            // money lines (Descriptions must match what extraction writes into StatementLine.Description)
            MetricDef.Money("Income.UberRidesGross",    "Income",       "Uber Rides Total (Gross)"),
            MetricDef.Money("Fee.UberRidesFees",        "Fee",          "Uber Rides Fees Total"),
            MetricDef.Money("TaxCollected.GSTHST",      "TaxCollected", "GST/HST you collected from Riders"),
            MetricDef.Money("ITC.GSTHSTPaidToUber",     "Itc",          "GST/HST you paid to Uber"),

            // metric line (reconcile by MetricKey)
            MetricDef.Metric("Metric.OnlineKilometers", "OnlineKilometers"),
        };

        decimal MonthlyTotal(MetricDef def) => def.Kind switch
        {
            MetricKind.Money => SumMoney(monthlyLines, def.LineType!, def.Description!),
            MetricKind.Metric => SumMetric(monthlyLines, def.MetricKey!),
            _ => 0m
        };

        decimal YearlyTotal(MetricDef def) => def.Kind switch
        {
            MetricKind.Money => SumMoney(yearlyLines, def.LineType!, def.Description!),
            MetricKind.Metric => SumMetric(yearlyLines, def.MetricKey!),
            _ => 0m
        };

        var variances = new List<ReconciliationVariance>(defs.Length);
        foreach (var def in defs)
        {
            var m = MonthlyTotal(def);
            var y = YearlyTotal(def);

            variances.Add(new ReconciliationVariance
            {
                TenantId = tenantId,
                MetricKey = def.MetricId,
                MonthlyTotal = m,
                YearlyTotal = y,
                VarianceAmount = m - y,
                Notes = null
            });
        }

        // Header totals based on gross income metric
        var monthlyIncomeTotal = variances.First(v => v.MetricKey == "Income.UberRidesGross").MonthlyTotal;
        var yearlyIncomeTotal = variances.First(v => v.MetricKey == "Income.UberRidesGross").YearlyTotal;
        var headerVariance = monthlyIncomeTotal - yearlyIncomeTotal;

        // 5) Upsert run (idempotent per tenant+provider+year)
        var run = await _db.ReconciliationRuns
            .Include(r => r.Variances)
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.Provider == ProviderUber &&
                r.PeriodType == PeriodTypeYearly &&
                r.PeriodKey == yearKey, ct);

        if (run is null)
        {
            run = new ReconciliationRun
            {
                TenantId = tenantId,
                Provider = ProviderUber,
                PeriodType = PeriodTypeYearly,
                PeriodKey = yearKey,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.ReconciliationRuns.Add(run);
        }
        else if (run.Variances.Count > 0)
        {
            // Replace variances for clean reruns
            _db.ReconciliationVariances.RemoveRange(run.Variances);
            run.Variances.Clear();
        }

        run.YearlyStatementId = yearlyStatementId.Value;
        run.MonthlyIncomeTotal = monthlyIncomeTotal;
        run.YearlyIncomeTotal = yearlyIncomeTotal;
        run.VarianceAmount = headerVariance;
        run.Status = "Completed";
        run.CompletedAt = DateTimeOffset.UtcNow;

        foreach (var v in variances)
        {
            v.ReconciliationRunId = run.Id;
            run.Variances.Add(v);
        }

        await _db.SaveChangesAsync(ct);
        return run;
    }

    private static decimal SumMoney(IEnumerable<LineRow> rows, string lineType, string description)
    {
        var targetType = Normalize(lineType);
        var targetDesc = Normalize(description);

        return rows
            .Where(r =>
                !r.IsMetric &&
                string.Equals(Normalize(r.LineType), targetType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Normalize(r.Description), targetDesc, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.MoneyAmount ?? 0m);
    }

    private static decimal SumMetric(IEnumerable<LineRow> rows, string metricKey)
    {
        var targetKey = Normalize(metricKey);

        return rows
            .Where(r =>
                r.IsMetric &&
                string.Equals(Normalize(r.MetricKey), targetKey, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.MetricValue ?? 0m);
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim().Replace("\u00A0", " "); // PDF NBSP safe

    private readonly record struct LineRow(
        string LineType,
        string? Description,
        bool IsMetric,
        string? MetricKey,
        decimal? MetricValue,
        decimal? MoneyAmount);

    private enum MetricKind { Money, Metric }

    private readonly record struct MetricDef(
        string MetricId,
        MetricKind Kind,
        string? LineType,
        string? Description,
        string? MetricKey)
    {
        public static MetricDef Money(string metricId, string lineType, string description)
            => new(metricId, MetricKind.Money, lineType, description, null);

        public static MetricDef Metric(string metricId, string metricKey)
            => new(metricId, MetricKind.Metric, null, null, metricKey);
    }
}
