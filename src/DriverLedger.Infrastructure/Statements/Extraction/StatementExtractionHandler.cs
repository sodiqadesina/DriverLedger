using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Files;
using DriverLedger.Domain.Notifications;
using DriverLedger.Domain.Statements;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriverLedger.Infrastructure.Statements.Extraction
{
    /// <summary>
    /// Handles incoming statement files, runs the correct extractor, normalizes/collapses line output,
    /// applies provider-specific pruning rules, and persists the Statement + StatementLines.
    ///
    /// Notes on evidence fields:
    /// - CurrencyEvidence/ClassificationEvidence are end-to-end signals. Do not silently downgrade them.
    /// - If a line inherits CurrencyCode from the statement-level currency, it should inherit the
    ///   statement-level CurrencyEvidence as well.
    /// - Provider-specific canonical lines (e.g., Uber gross fares anchors) may be upgraded to Extracted
    ///   when the classification is deterministically known by business rules.
    /// </summary>
    public sealed class StatementExtractionHandler(
        DriverLedgerDbContext db,
        ITenantProvider tenantProvider,
        IBlobStorage blobStorage,
        IEnumerable<IStatementExtractor> extractors,
        IMessagePublisher publisher,
        ILogger<StatementExtractionHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public async Task HandleAsync(MessageEnvelope<StatementReceived> envelope, CancellationToken ct)
        {
            var tenantId = envelope.TenantId;
            var correlationId = envelope.CorrelationId;
            var msg = envelope.Data;

            tenantProvider.SetTenant(tenantId);

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = correlationId,
                ["messageId"] = envelope.MessageId,
                ["fileObjectId"] = msg.FileObjectId,
                ["provider"] = msg.Provider,
                ["periodKey"] = msg.PeriodKey
            });

            // Load file metadata
            var file = await db.FileObjects.SingleAsync(x => x.TenantId == tenantId && x.Id == msg.FileObjectId, ct);

            // Choose extractor by content type
            var extractor = extractors.FirstOrDefault(x => x.CanHandleContentType(file.ContentType));
            if (extractor is null)
                throw new InvalidOperationException($"No statement extractor found for content type: {file.ContentType}");

            // Open blob stream
            await using var content = await blobStorage.OpenReadAsync(file.BlobPath, ct);

            // Extract normalized lines
            var normalized = await extractor.ExtractAsync(content, ct);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                try
                {
                    logger.LogDebug("Raw normalized lines before collapse: {lines}", JsonSerializer.Serialize(normalized, JsonOpts));
                }
                catch
                {
                    // swallow
                }
            }

            // --------------------
            // Robust collapse helpers
            // --------------------

            static (string? MetricKey, string? Unit) InferMetricKeyAndUnit(string? description)
            {
                if (string.IsNullOrWhiteSpace(description)) return (null, null);
                var desc = description.Trim().ToLowerInvariant();

                if (desc.Contains("total rides") || (desc.Contains("rides") && desc.Contains("total")))
                    return ("Trips", "trips");

                if (desc.Contains("trip") && (desc.Contains("count") || desc.Contains("total")))
                    return ("Trips", "trips");

                if (desc.Contains("kilomet") || desc.Contains(" km"))
                    return ("RideKilometers", "km");

                if (desc.Contains(" mile") || desc.Contains(" mi"))
                    return ("RideMiles", "mi");

                if (desc.Contains("online hour") || desc.Contains("hours online") || desc.Contains("online time"))
                    return ("OnlineHours", "hours");

                return (null, null);
            }

            static decimal? NormalizeMetricValueForKey(string? metricKey, decimal? value)
            {
                if (!value.HasValue) return null;

                if (string.Equals(metricKey, "Trips", StringComparison.OrdinalIgnoreCase))
                    return decimal.Round(value.Value, 0, MidpointRounding.AwayFromZero);

                if (string.Equals(metricKey, "RideKilometers", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(metricKey, "RideMiles", StringComparison.OrdinalIgnoreCase))
                    return decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);

                if (string.Equals(metricKey, "OnlineHours", StringComparison.OrdinalIgnoreCase))
                    return decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);

                return decimal.Round(value.Value, 3, MidpointRounding.AwayFromZero);
            }

            static string CanonicalMetricKey(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                var key = raw.Trim().ToLowerInvariant();
                return key switch
                {
                    "trips" => "Trips",
                    "ridekilometers" => "RideKilometers",
                    "ridemiles" => "RideMiles",
                    "onlinehours" => "OnlineHours",
                    _ => char.ToUpperInvariant(key[0]) + key.Substring(1)
                };
            }

            static string CanonicalMetricDescription(string? metricKey)
            {
                if (string.IsNullOrWhiteSpace(metricKey)) return metricKey ?? string.Empty;
                return metricKey switch
                {
                    "Trips" => "Total Rides",
                    "RideKilometers" => "Ride distance",
                    "RideMiles" => "Ride distance",
                    "OnlineHours" => "Online hours",
                    _ => metricKey
                };
            }

            // PREPARE
            var prepared = normalized
                .Select(x =>
                {
                    var key = x.MetricKey?.Trim();
                    var unit = x.Unit?.Trim();

                    if (string.IsNullOrWhiteSpace(key) && x.IsMetric)
                    {
                        var inferred = InferMetricKeyAndUnit(x.Description);
                        if (!string.IsNullOrWhiteSpace(inferred.MetricKey))
                        {
                            key = inferred.MetricKey;
                            unit = inferred.Unit;
                        }
                    }

                    decimal? normMetricValue = null;
                    if (x.MetricValue.HasValue)
                        normMetricValue = NormalizeMetricValueForKey(key, x.MetricValue);

                    var (canonMoney, canonTax) = StatementExtractionParsing.NormalizeTaxColumns(
                        resolvedLineType: x.LineType,
                        moneyAmount: x.MoneyAmount,
                        taxAmount: x.TaxAmount);

                    decimal? moneyGroup = canonMoney.HasValue ? decimal.Round(canonMoney.Value, 2, MidpointRounding.AwayFromZero) : null;
                    decimal? taxGroup = canonTax.HasValue ? decimal.Round(canonTax.Value, 2, MidpointRounding.AwayFromZero) : null;

                    return (Orig: x, MetricKeyNorm: key, MetricValueNorm: normMetricValue, UnitNorm: unit, MoneyNorm: moneyGroup, TaxNorm: taxGroup, CanonMoney: canonMoney, CanonTax: canonTax);
                })
                .ToList();

            // COLLAPSE
            var collapsed = prepared
                .GroupBy(t => (
                    LineType: t.Orig.LineType,
                    IsMetric: t.Orig.IsMetric,
                    Money: t.MoneyNorm,
                    Tax: t.TaxNorm,
                    MetricKey: (t.MetricKeyNorm ?? string.Empty).ToLowerInvariant(),
                    MetricValue: t.MetricValueNorm,
                    Unit: (t.UnitNorm ?? string.Empty).ToLowerInvariant(),
                    Currency: t.Orig.CurrencyCode))
                .Select(g =>
                {
                    // Choose the "best evidenced" representative for the group.
                    var best = g
                        .OrderByDescending(x => string.Equals(x.Orig.ClassificationEvidence, "Extracted", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        .ThenByDescending(x => string.Equals(x.Orig.CurrencyEvidence, "Extracted", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        .First();

                    var groupKey = g.Key;
                    var canonicalMetricKey = string.IsNullOrWhiteSpace(groupKey.MetricKey) ? null : CanonicalMetricKey(groupKey.MetricKey);
                    var canonicalUnit = string.IsNullOrWhiteSpace(groupKey.Unit) ? null : groupKey.Unit;
                    var canonicalMetricValue = groupKey.MetricValue;

                    decimal? chosenMoney = g.Select(x => x.CanonMoney).FirstOrDefault(v => v.HasValue);
                    decimal? chosenTax = g.Select(x => x.CanonTax).FirstOrDefault(v => v.HasValue);

                    var chosenDesc = g.Select(x => x.Orig.Description)
                                      .Where(d => !string.IsNullOrWhiteSpace(d))
                                      .FirstOrDefault() ?? best.Orig.Description;

                    if (!string.IsNullOrWhiteSpace(canonicalMetricKey))
                        chosenDesc = CanonicalMetricDescription(canonicalMetricKey);

                    return new StatementLineNormalized(
                        LineDate: best.Orig.LineDate,
                        LineType: best.Orig.LineType,
                        Description: chosenDesc,
                        CurrencyCode: best.Orig.CurrencyCode,
                        CurrencyEvidence: best.Orig.CurrencyEvidence,
                        ClassificationEvidence: best.Orig.ClassificationEvidence,
                        IsMetric: best.Orig.IsMetric,
                        MetricKey: canonicalMetricKey,
                        MetricValue: canonicalMetricValue,
                        Unit: canonicalUnit,
                        MoneyAmount: chosenMoney.HasValue ? Math.Abs(chosenMoney.Value) : (decimal?)null,
                        TaxAmount: chosenTax.HasValue ? Math.Abs(chosenTax.Value) : (decimal?)null
                    );
                })
                .ToList();

            // ==========================================================
            // PROVIDER-SPECIFIC PRUNE
            // ==========================================================

            static bool LooksLikeGstRegistrationLine(string? description)
            {
                if (string.IsNullOrWhiteSpace(description)) return false;
                // e.g. "Your GST/HST Number 789675428RT0001"
                return Regex.IsMatch(description, @"\bRT\d{4}\b", RegexOptions.IgnoreCase)
                       && Regex.IsMatch(description, @"\bGST\s*/\s*HST\b|\bGST\b|\bHST\b", RegexOptions.IgnoreCase);
            }

            IReadOnlyList<StatementLineNormalized> pruned;

            // 1) Uber: HARD allowlist + drop RT0001 lines + keep ONLY OnlineKilometers metric
            if (string.Equals(msg.Provider, "Uber", StringComparison.OrdinalIgnoreCase))
            {
                var kept = new List<StatementLineNormalized>(capacity: collapsed.Count);

                static bool IsGrossUberRidesFares(string d)
                {
                    if (string.IsNullOrWhiteSpace(d)) return false;

                    // Covers:
                    // - "Gross Uber rides fares"
                    // - "Gross Uber rides fares1"
                    // - any future minor suffix variants
                    return d.StartsWith("Gross Uber rides fares", StringComparison.OrdinalIgnoreCase);
                }

                static StatementLineNormalized UpgradeEvidenceForGrossFares(StatementLineNormalized l)
                {
                    // Business rule: when we deterministically match the canonical Uber gross fares label,
                    // we treat classification/currency evidence as Extracted (do not downgrade if already extracted).
                    var d = (l.Description ?? string.Empty).Trim();

                    if (!string.Equals(l.LineType, "Income", StringComparison.OrdinalIgnoreCase))
                        return l;

                    if (!IsGrossUberRidesFares(d))
                        return l;

                    var curEv = string.Equals(l.CurrencyEvidence, "Extracted", StringComparison.OrdinalIgnoreCase)
                        ? l.CurrencyEvidence
                        : "Extracted";

                    var classEv = string.Equals(l.ClassificationEvidence, "Extracted", StringComparison.OrdinalIgnoreCase)
                        ? l.ClassificationEvidence
                        : "Extracted";

                    return l with
                    {
                        CurrencyEvidence = curEv,
                        ClassificationEvidence = classEv
                    };
                }

                foreach (var line in collapsed)
                {
                    // Drop GST registration noise always
                    if (LooksLikeGstRegistrationLine(line.Description))
                        continue;

                    if (line.IsMetric)
                    {
                        // Like Lyft: keep ONLY one mileage metric -> OnlineKilometers
                        if (string.Equals(line.MetricKey, "OnlineKilometers", StringComparison.OrdinalIgnoreCase))
                            kept.Add(line);

                        continue;
                    }

                    // Monetary allowlist for Uber
                    var desc = (line.Description ?? string.Empty).Trim();

                    var allowed =
                        string.Equals(line.LineType, "TaxCollected", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(line.LineType, "Itc", StringComparison.OrdinalIgnoreCase) ||
                        (string.Equals(line.LineType, "Income", StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(desc, "Uber Rides Total (Gross)", StringComparison.OrdinalIgnoreCase)
                             || IsGrossUberRidesFares(desc))) ||
                        (string.Equals(line.LineType, "Fee", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(desc, "Uber Rides Fees Total", StringComparison.OrdinalIgnoreCase));

                    if (!allowed)
                        continue;

                    // Ensure gross fares anchors do not get persisted as "Inferred/Inferred" when we can deterministically match them.
                    kept.Add(UpgradeEvidenceForGrossFares(line));
                }

                // IMPORTANT: for Uber we want these lines to exist even if they were missing (so snapshots stay stable).
                bool HasLine(string lineType, string desc) =>
                    kept.Any(x => !x.IsMetric &&
                                  string.Equals(x.LineType, lineType, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals((x.Description ?? string.Empty).Trim(), desc, StringComparison.OrdinalIgnoreCase));

                bool HasMetric(string metricKey) =>
                    kept.Any(x => x.IsMetric && string.Equals(x.MetricKey, metricKey, StringComparison.OrdinalIgnoreCase));

                if (!HasLine("Income", "Uber Rides Total (Gross)"))
                {
                    kept.Add(new StatementLineNormalized(
                        LineDate: DateOnly.MinValue,
                        LineType: "Income",
                        Description: "Uber Rides Total (Gross)",
                        CurrencyCode: "CAD",
                        CurrencyEvidence: "Inferred",
                        ClassificationEvidence: "Extracted",
                        IsMetric: false,
                        MetricKey: null,
                        MetricValue: null,
                        Unit: null,
                        MoneyAmount: 0m,
                        TaxAmount: null
                    ));
                }

                if (!HasLine("Fee", "Uber Rides Fees Total"))
                {
                    kept.Add(new StatementLineNormalized(
                        LineDate: DateOnly.MinValue,
                        LineType: "Fee",
                        Description: "Uber Rides Fees Total",
                        CurrencyCode: "CAD",
                        CurrencyEvidence: "Inferred",
                        ClassificationEvidence: "Extracted",
                        IsMetric: false,
                        MetricKey: null,
                        MetricValue: null,
                        Unit: null,
                        MoneyAmount: 0m,
                        TaxAmount: null
                    ));
                }

                if (!HasLine("TaxCollected", "GST/HST you collected from Riders"))
                {
                    kept.Add(new StatementLineNormalized(
                        LineDate: DateOnly.MinValue,
                        LineType: "TaxCollected",
                        Description: "GST/HST you collected from Riders",
                        CurrencyCode: "CAD",
                        CurrencyEvidence: "Inferred",
                        ClassificationEvidence: "Extracted",
                        IsMetric: false,
                        MetricKey: null,
                        MetricValue: null,
                        Unit: null,
                        MoneyAmount: 0m,
                        TaxAmount: 0m
                    ));
                }

                if (!HasLine("Itc", "GST/HST you paid to Uber"))
                {
                    kept.Add(new StatementLineNormalized(
                        LineDate: DateOnly.MinValue,
                        LineType: "Itc",
                        Description: "GST/HST you paid to Uber",
                        CurrencyCode: "CAD",
                        CurrencyEvidence: "Inferred",
                        ClassificationEvidence: "Extracted",
                        IsMetric: false,
                        MetricKey: null,
                        MetricValue: null,
                        Unit: null,
                        MoneyAmount: 0m,
                        TaxAmount: 0m
                    ));
                }

                if (!HasMetric("OnlineKilometers"))
                {
                    kept.Add(new StatementLineNormalized(
                        LineDate: DateOnly.MinValue,
                        LineType: "Metric",
                        Description: "Online Mileage (Uber statement)",
                        CurrencyCode: "CAD",
                        CurrencyEvidence: "Inferred",
                        ClassificationEvidence: "Extracted",
                        IsMetric: true,
                        MetricKey: "OnlineKilometers",
                        MetricValue: 0m,
                        Unit: "km",
                        MoneyAmount: null,
                        TaxAmount: null
                    ));
                }

                pruned = kept;
            }
            // 2) Lyft: keep your existing prune logic
            else if (string.Equals(msg.Provider, "Lyft", StringComparison.OrdinalIgnoreCase))
            {
                var feeAmountSet = collapsed
                    .Where(x => !x.IsMetric &&
                                (string.Equals(x.LineType, "Fee", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(x.LineType, "Expense", StringComparison.OrdinalIgnoreCase)))
                    .Where(x => x.MoneyAmount.HasValue)
                    .Select(x => decimal.Round(x.MoneyAmount!.Value, 2, MidpointRounding.AwayFromZero))
                    .ToHashSet();

                if (feeAmountSet.Any())
                {
                    var dropped = 0;
                    var kept = new List<StatementLineNormalized>(capacity: collapsed.Count);
                    foreach (var line in collapsed)
                    {
                        if (!line.IsMetric &&
                            string.Equals(line.LineType, "Income", StringComparison.OrdinalIgnoreCase) &&
                            line.MoneyAmount.HasValue)
                        {
                            var desc = (line.Description ?? string.Empty).Trim();
                            if (string.Equals(desc, "Bonuses", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(desc, "bonus", StringComparison.OrdinalIgnoreCase))
                            {
                                var amt = decimal.Round(line.MoneyAmount.Value, 2, MidpointRounding.AwayFromZero);
                                if (feeAmountSet.Contains(amt))
                                {
                                    dropped++;
                                    continue;
                                }
                            }
                        }

                        kept.Add(line);
                    }

                    pruned = kept;
                    if (dropped > 0)
                        logger.LogInformation("Pruned {Dropped} Lyft suspect Income 'Bonuses' lines for provider={Provider} periodKey={PeriodKey}", dropped, msg.Provider, msg.PeriodKey);
                }
                else
                {
                    pruned = collapsed;
                }
            }
            else
            {
                pruned = collapsed;
            }

            // Determine statement-level currency (once) from pruned set
            var resolvedStatementCurrency = ResolveStatementCurrency(file, pruned, out var stmtCurrencyEvidence);

            // Upsert statement (by Provider/PeriodType/PeriodKey unique index)
            var statement = await db.Statements.SingleOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.Provider == msg.Provider &&
                x.PeriodType == msg.PeriodType &&
                x.PeriodKey == msg.PeriodKey, ct);

            if (statement is null)
            {
                statement = new Statement
                {
                    TenantId = tenantId,
                    FileObjectId = msg.FileObjectId,
                    Provider = msg.Provider,
                    PeriodType = msg.PeriodType,
                    PeriodKey = msg.PeriodKey,
                    PeriodStart = msg.PeriodStart,
                    PeriodEnd = msg.PeriodEnd,
                    VendorName = msg.Provider,
                    Status = "Draft",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Statements.Add(statement);
            }
            else
            {
                statement.FileObjectId = msg.FileObjectId;
                statement.PeriodStart = msg.PeriodStart;
                statement.PeriodEnd = msg.PeriodEnd;
                statement.Status = "Draft";
            }

            statement.CurrencyCode = resolvedStatementCurrency;
            statement.CurrencyEvidence = stmtCurrencyEvidence;

            // Replace existing lines for idempotency
            var existingLines = await db.StatementLines
                .Where(x => x.TenantId == tenantId && x.StatementId == statement.Id)
                .ToListAsync(ct);

            db.StatementLines.RemoveRange(existingLines);

            var createdLines = new List<StatementLine>(capacity: pruned.Count);

            static bool EquivalentMetricValue(decimal? a, decimal? b, string? metricKey)
            {
                if (!a.HasValue && !b.HasValue) return true;
                if (!a.HasValue || !b.HasValue) return false;

                var prec = string.Equals(metricKey, "Trips", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
                return decimal.Round(a.Value, prec, MidpointRounding.AwayFromZero) ==
                       decimal.Round(b.Value, prec, MidpointRounding.AwayFromZero);
            }

            static bool IsEquivalent(StatementLine existing, StatementLineNormalized cand)
            {
                if (existing.IsMetric && cand.IsMetric)
                {
                    var k1 = existing.MetricKey?.Trim().ToLowerInvariant();
                    var k2 = cand.MetricKey?.Trim().ToLowerInvariant();
                    if (k1 != k2) return false;

                    var u1 = existing.Unit?.Trim().ToLowerInvariant();
                    var u2 = cand.Unit?.Trim().ToLowerInvariant();
                    if (u1 != u2) return false;

                    return EquivalentMetricValue(existing.MetricValue, cand.MetricValue, existing.MetricKey ?? cand.MetricKey);
                }

                if (!existing.IsMetric && !cand.IsMetric)
                {
                    var lt = (existing.LineType ?? string.Empty).Trim().ToLowerInvariant();
                    var ctLineType = (cand.LineType ?? string.Empty).Trim().ToLowerInvariant();

                    if ((lt == "taxcollected" || lt == "itc") && (ctLineType == "taxcollected" || ctLineType == "itc"))
                    {
                        var t1 = existing.TaxAmount ?? 0m;
                        var t2 = cand.TaxAmount ?? 0m;
                        return decimal.Round(t1, 2, MidpointRounding.AwayFromZero) == decimal.Round(t2, 2, MidpointRounding.AwayFromZero);
                    }

                    if (!string.Equals(existing.LineType, cand.LineType, StringComparison.OrdinalIgnoreCase)) return false;

                    var m1 = existing.MoneyAmount;
                    var m2 = cand.MoneyAmount;
                    if (m1.HasValue || m2.HasValue)
                    {
                        if (decimal.Round(m1 ?? 0m, 2, MidpointRounding.AwayFromZero) != decimal.Round(m2 ?? 0m, 2, MidpointRounding.AwayFromZero))
                            return false;
                    }

                    var tt1 = existing.TaxAmount;
                    var tt2 = cand.TaxAmount;
                    if (tt1.HasValue || tt2.HasValue)
                    {
                        if (decimal.Round(tt1 ?? 0m, 2, MidpointRounding.AwayFromZero) != decimal.Round(tt2 ?? 0m, 2, MidpointRounding.AwayFromZero))
                            return false;
                    }

                    return true;
                }

                return false;
            }

            foreach (var n in pruned)
            {
                if (createdLines.Any(e => IsEquivalent(e, n)))
                    continue;

                // Currency inheritance:
                // - If line provides its own CurrencyCode, use it and keep its evidence.
                // - If line does not provide CurrencyCode, inherit statement currency AND statement currency evidence.
                var lineCurrency = n.CurrencyCode ?? statement.CurrencyCode;
                var lineCurrencyEvidence = n.CurrencyCode is null
                    ? statement.CurrencyEvidence
                    : n.CurrencyEvidence;

                decimal? moneyAmount = n.MoneyAmount;
                decimal? taxAmount = n.TaxAmount;

                if (n.IsMetric)
                {
                    moneyAmount = null;
                    taxAmount = null;
                }
                else
                {
                    // For Uber we purposely kept some zeros; for other providers skip empty
                    var m = moneyAmount ?? 0m;
                    var t = taxAmount ?? 0m;

                    var keepZeroBecauseUber = string.Equals(msg.Provider, "Uber", StringComparison.OrdinalIgnoreCase)
                                              && (string.Equals(n.LineType, "TaxCollected", StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(n.LineType, "Itc", StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals((n.Description ?? string.Empty).Trim(), "Uber Rides Total (Gross)", StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals((n.Description ?? string.Empty).Trim(), "Uber Rides Fees Total", StringComparison.OrdinalIgnoreCase));

                    if (m == 0m && t == 0m && !keepZeroBecauseUber)
                        continue;
                }

                var line = new StatementLine
                {
                    TenantId = tenantId,
                    StatementId = statement.Id,
                    LineDate = n.LineDate,

                    LineType = n.IsMetric ? "Metric" : n.LineType,
                    Description = n.Description,

                    CurrencyCode = lineCurrency,
                    CurrencyEvidence = lineCurrencyEvidence,
                    ClassificationEvidence = n.ClassificationEvidence,

                    IsMetric = n.IsMetric,
                    MetricKey = n.MetricKey,
                    MetricValue = n.MetricValue,
                    Unit = n.Unit,

                    MoneyAmount = moneyAmount,
                    TaxAmount = taxAmount
                };

                db.StatementLines.Add(line);
                createdLines.Add(line);
            }

            var incomeTotal = createdLines.Where(x => x.LineType == "Income").Sum(x => x.MoneyAmount ?? 0m);
            var feeTotal = createdLines.Where(x => x.LineType == "Fee" || x.LineType == "Expense").Sum(x => x.MoneyAmount ?? 0m);
            var taxTotal = createdLines.Where(x => !x.IsMetric).Sum(x => x.TaxAmount ?? 0m);

            // ==========================================================
            // Statement-level totals
            // ==========================================================
            // StatementTotalAmount is a provider-facing "statement total" number,
            // not the same as snapshot revenue. For Uber, we keep this aligned with
            // the historical "Total" line users expect ("Uber Rides Total (Gross)").
            // Snapshots may use a different revenue anchor ("Gross Uber rides fares...").
            // ==========================================================

            statement.TaxAmount = taxTotal;

            if (string.Equals(msg.Provider, "Uber", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer the old statement "Total" line (previous revenue behavior).
                // This is what users expect to see as the statement's total figure.
                var uberTotalGross = createdLines
                    .Where(x => x.LineType == "Income")
                    .Where(x => string.Equals((x.Description ?? string.Empty).Trim(), "Uber Rides Total (Gross)", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.MoneyAmount ?? 0m)
                    .FirstOrDefault();

                // Fallback: if missing, fall back to legacy net calculation.
                statement.StatementTotalAmount = uberTotalGross > 0m
                    ? uberTotalGross
                    : (incomeTotal - feeTotal);
            }
            else
            {
                // Default behavior for other providers: net statement total (income - fees/expenses).
                statement.StatementTotalAmount = incomeTotal - feeTotal;
            }


            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                ActorUserId = "system",
                Action = "statement.parsed",
                EntityType = "Statement",
                EntityId = statement.Id.ToString("D"),
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = correlationId,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    provider = statement.Provider,
                    periodType = statement.PeriodType,
                    periodKey = statement.PeriodKey,
                    lineCount = pruned.Count,
                    statementCurrency = statement.CurrencyCode,
                    currencyEvidence = statement.CurrencyEvidence,
                    modelVersion = extractor.ModelVersion
                }, JsonOpts)
            });

            db.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                Type = "StatementParsed",
                Severity = "Info",
                Title = "Statement parsed",
                Body = $"Parsed {pruned.Count} lines ({statement.Provider} {statement.PeriodKey}).",
                DataJson = JsonSerializer.Serialize(new { statementId = statement.Id }, JsonOpts),
                Status = "New",
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);

            var parsed = new StatementParsed(
                StatementId: statement.Id,
                TenantId: tenantId,
                Provider: statement.Provider,
                PeriodType: statement.PeriodType,
                PeriodKey: statement.PeriodKey,
                LineCount: pruned.Count,
                IncomeTotal: incomeTotal,
                FeeTotal: feeTotal,
                TaxTotal: taxTotal
            );

            var outEnvelope = new MessageEnvelope<StatementParsed>(
                MessageId: Guid.NewGuid().ToString("N"),
                Type: "statement.parsed.v1",
                OccurredAt: DateTimeOffset.UtcNow,
                TenantId: tenantId,
                CorrelationId: correlationId,
                Data: parsed
            );

            await publisher.PublishAsync("q.statement.parsed", outEnvelope, ct);
        }

        private static string ResolveStatementCurrency(
            FileObject file,
            IReadOnlyList<StatementLineNormalized> lines,
            out string evidence)
        {
            var explicitCur = lines
                .Select(x => x.CurrencyCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x!)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(explicitCur))
            {
                evidence = "Extracted";
                return explicitCur!;
            }

            evidence = "Inferred";
            return "CAD";
        }
    }
}
