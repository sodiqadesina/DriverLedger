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
///
/// IMPORTANT:
/// Uber statements can contain multiple "income-like" lines.
/// We reconcile both:
/// - "Uber Rides Total (Gross)" (legacy / statement display total line)
/// - "Gross Uber rides fares" (aka "Gross Uber rides fares1") (revenue anchor used for snapshots)
///
/// Reconciliation matching:
/// - Income/Fee are reconciled via StatementLine.MoneyAmount
/// - TaxCollected/Itc are reconciled via StatementLine.TaxAmount (MoneyAmount is usually 0)
///
/// HEADERS:
/// - The run header totals are aligned with snapshots by using the revenue anchor:
///   "Gross Uber rides fares*".
///
/// NOTE ON TAX:
/// We calculate tax variances independently. This implementation keeps:
/// - TaxCollected variance independent (TaxCollected.GSTHST)
/// - ITC variance independent (ITC.GSTHSTPaidToUber)
/// (No combining / netting is performed in reconciliation variances.)
/// </summary>
public sealed class UberStatementReconciliationService : IStatementReconciliationService
{
    private const string ProviderUber = "Uber";
    private const string PeriodTypeYearly = "Yearly";
    private const string PeriodTypeMonthly = "Monthly";

    // We only want monthly truth coming from actually-posted monthlies.
    // (Prevents Draft/Uploaded/ReconciliationOnly from polluting totals.)
    private const string StatementStatusPosted = "Posted";

    // Canonical descriptors (match what extraction writes into StatementLine.Description)
    private const string UberRidesTotalGrossDesc = "Uber Rides Total (Gross)";
    private const string UberRidesFeesTotalDesc = "Uber Rides Fees Total";
    private const string GstHstCollectedDesc = "GST/HST you collected from Riders";
    private const string GstHstPaidToUberDesc = "GST/HST you paid to Uber";

    // Revenue anchor: some PDFs/extractors emit "Gross Uber rides fares1"
    private const string GrossUberRidesFaresPrefix = "Gross Uber rides fares";

    private readonly DriverLedgerDbContext _db;

    public UberStatementReconciliationService(DriverLedgerDbContext db) => _db = db;

    public async Task<ReconciliationRun> ReconcileUberYearAsync(Guid tenantId, int year, CancellationToken ct = default)
    {
        var yearKey = year.ToString();

        // 1) Yearly statement is required (can be ReconciliationOnly and still valid for comparing)
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

        // 2) Monthly statements for the year (only Posted)
        var monthlyStatementIds = await _db.Statements
            .AsNoTracking()
            .Where(s =>
                s.TenantId == tenantId &&
                s.Provider == ProviderUber &&
                s.PeriodType == PeriodTypeMonthly &&
                s.PeriodStart.Year == year &&
                s.Status == StatementStatusPosted)
            .Select(s => s.Id)
            .ToListAsync(ct);

        // 3) Load lines (money + taxes + metrics)
        var yearlyLines = await _db.StatementLines
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.StatementId == yearlyStatementId.Value)
            .Select(l => new LineRow(
                l.LineType,
                l.Description,
                l.IsMetric,
                l.MetricKey,
                l.MetricValue,
                l.MoneyAmount,
                l.TaxAmount))
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
                    l.MoneyAmount,
                    l.TaxAmount))
                .ToListAsync(ct);

        // 4) Allowlist for MVP reconciliation
        //
        //  variances is calculated independently (especially tax).
        // This means:
        // - TaxCollected variance is its own row (TaxCollected.GSTHST)
        // - ITC variance is its own row (ITC.GSTHSTPaidToUber)
        // - We do NOT compute or net them into a single "NetTax" variance here.
        var defs = new MetricDef[]
        {
            // Revenue anchor used by snapshots (prefix match: "Gross Uber rides fares" or "...fares1")
            MetricDef.MoneyPrefix("Income.GrossUberRidesFares", "Income", GrossUberRidesFaresPrefix),

            // Legacy / statement display total (exact description)
            MetricDef.Money("Income.UberRidesGross", "Income", UberRidesTotalGrossDesc),

            // Other stable money line
            MetricDef.Money("Fee.UberRidesFees", "Fee", UberRidesFeesTotalDesc),

            // TAX lines (independent variances; sum TaxAmount, not MoneyAmount)
            MetricDef.Tax("TaxCollected.GSTHST", "TaxCollected", GstHstCollectedDesc),
            MetricDef.Tax("ITC.GSTHSTPaidToUber", "Itc", GstHstPaidToUberDesc),

            // Metric line
            MetricDef.Metric("Metric.OnlineKilometers", "OnlineKilometers"),
        };

        decimal MonthlyTotal(MetricDef def) => def.Kind switch
        {
            MetricKind.Money => def.Match switch
            {
                MoneyMatch.Exact => SumMoneyExact(monthlyLines, def.LineType!, def.DescriptionOrPrefix!),
                MoneyMatch.Prefix => SumMoneyPrefix(monthlyLines, def.LineType!, def.DescriptionOrPrefix!),
                _ => 0m
            },

            MetricKind.Tax => def.Match switch
            {
                MoneyMatch.Exact => SumTaxExact(monthlyLines, def.LineType!, def.DescriptionOrPrefix!),
                MoneyMatch.Prefix => SumTaxPrefix(monthlyLines, def.LineType!, def.DescriptionOrPrefix!),
                _ => 0m
            },

            MetricKind.Metric => SumMetric(monthlyLines, def.MetricKey!),
            _ => 0m
        };

        decimal YearlyTotal(MetricDef def) => def.Kind switch
        {
            MetricKind.Money => def.Match switch
            {
                MoneyMatch.Exact => SumMoneyExact(yearlyLines, def.LineType!, def.DescriptionOrPrefix!),
                MoneyMatch.Prefix => SumMoneyPrefix(yearlyLines, def.LineType!, def.DescriptionOrPrefix!),
                _ => 0m
            },

            MetricKind.Tax => def.Match switch
            {
                MoneyMatch.Exact => SumTaxExact(yearlyLines, def.LineType!, def.DescriptionOrPrefix!),
                MoneyMatch.Prefix => SumTaxPrefix(yearlyLines, def.LineType!, def.DescriptionOrPrefix!),
                _ => 0m
            },

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

        // Header totals: align with snapshot revenue anchor.
        var monthlyIncomeTotal = variances.First(v => v.MetricKey == "Income.GrossUberRidesFares").MonthlyTotal;
        var yearlyIncomeTotal = variances.First(v => v.MetricKey == "Income.GrossUberRidesFares").YearlyTotal;
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

    private static decimal SumMoneyExact(IEnumerable<LineRow> rows, string lineType, string description)
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

    /// <summary>
    /// Prefix match is required for lines that can appear with suffix variants in PDFs,
    /// e.g. "Gross Uber rides fares" vs "Gross Uber rides fares1".
    /// </summary>
    private static decimal SumMoneyPrefix(IEnumerable<LineRow> rows, string lineType, string descriptionPrefix)
    {
        var targetType = Normalize(lineType);
        var targetPrefix = Normalize(descriptionPrefix);

        return rows
            .Where(r =>
                !r.IsMetric &&
                string.Equals(Normalize(r.LineType), targetType, StringComparison.OrdinalIgnoreCase) &&
                Normalize(r.Description).StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.MoneyAmount ?? 0m);
    }

    private static decimal SumTaxExact(IEnumerable<LineRow> rows, string lineType, string description)
    {
        var targetType = Normalize(lineType);
        var targetDesc = Normalize(description);

        return rows
            .Where(r =>
                !r.IsMetric &&
                string.Equals(Normalize(r.LineType), targetType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Normalize(r.Description), targetDesc, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.TaxAmount ?? 0m);
    }

    private static decimal SumTaxPrefix(IEnumerable<LineRow> rows, string lineType, string descriptionPrefix)
    {
        var targetType = Normalize(lineType);
        var targetPrefix = Normalize(descriptionPrefix);

        return rows
            .Where(r =>
                !r.IsMetric &&
                string.Equals(Normalize(r.LineType), targetType, StringComparison.OrdinalIgnoreCase) &&
                Normalize(r.Description).StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.TaxAmount ?? 0m);
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
        decimal? MoneyAmount,
        decimal? TaxAmount);

    private enum MetricKind { Money, Tax, Metric }

    private enum MoneyMatch { Exact, Prefix }

    private readonly record struct MetricDef(
        string MetricId,
        MetricKind Kind,
        MoneyMatch Match,
        string? LineType,
        string? DescriptionOrPrefix,
        string? MetricKey)
    {
        public static MetricDef Money(string metricId, string lineType, string description)
            => new(metricId, MetricKind.Money, MoneyMatch.Exact, lineType, description, null);

        public static MetricDef MoneyPrefix(string metricId, string lineType, string descriptionPrefix)
            => new(metricId, MetricKind.Money, MoneyMatch.Prefix, lineType, descriptionPrefix, null);

        public static MetricDef Tax(string metricId, string lineType, string description)
            => new(metricId, MetricKind.Tax, MoneyMatch.Exact, lineType, description, null);

        public static MetricDef TaxPrefix(string metricId, string lineType, string descriptionPrefix)
            => new(metricId, MetricKind.Tax, MoneyMatch.Prefix, lineType, descriptionPrefix, null);

        public static MetricDef Metric(string metricId, string metricKey)
            => new(metricId, MetricKind.Metric, MoneyMatch.Exact, null, null, metricKey);
    }
}
