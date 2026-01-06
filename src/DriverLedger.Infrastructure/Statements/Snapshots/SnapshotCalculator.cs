using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Ops;
using DriverLedger.Domain.Statements.Snapshots;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Statements.Snapshots
{
    public sealed class SnapshotCalculator(
       DriverLedgerDbContext db,
       ITenantProvider tenantProvider,
       ILogger<SnapshotCalculator> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public async Task HandleAsync(MessageEnvelope<LedgerPosted> envelope, CancellationToken ct)
        {
            var tenantId = envelope.TenantId;
            var correlationId = envelope.CorrelationId;
            var payload = envelope.Data;

            tenantProvider.SetTenant(tenantId);

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = correlationId,
                ["messageId"] = envelope.MessageId,
                ["ledgerEntryId"] = payload.LedgerEntryId,
                ["sourceType"] = payload.SourceType,
                ["sourceId"] = payload.SourceId
            });

            if (!DateOnly.TryParse(payload.EntryDate, out var entryDate))
            {
                logger.LogWarning("Invalid EntryDate format in ledger.posted.v1: {EntryDate}", payload.EntryDate);
                return;
            }

            // Receipts affect YTD only
            if (string.Equals(payload.SourceType, "Receipt", StringComparison.OrdinalIgnoreCase))
            {
                var ytdKey = $"{entryDate.Year:D4}";
                await ComputeAndUpsertAsync(tenantId, correlationId, periodType: "YTD", periodKey: ytdKey, ct);
                return;
            }

            // Statements: decide which bucket they affect based on the Statement record
            if (string.Equals(payload.SourceType, "Statement", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(payload.SourceId, out var statementId))
            {
                var st = await db.Statements
                    .AsNoTracking()
                    .Where(s => s.TenantId == tenantId && s.Id == statementId)
                    .Select(s => new { s.PeriodType, s.PeriodKey, s.PeriodStart })
                    .SingleOrDefaultAsync(ct);

                if (st is null)
                {
                    logger.LogWarning("Statement not found for SourceId={SourceId}", payload.SourceId);
                    var ytdFallback = $"{entryDate.Year:D4}";
                    await ComputeAndUpsertAsync(tenantId, correlationId, "YTD", ytdFallback, ct);
                    return;
                }

                // Always update YTD for statement postings too
                var ytdKey = $"{st.PeriodStart.Year:D4}";
                await ComputeAndUpsertAsync(tenantId, correlationId, periodType: "YTD", periodKey: ytdKey, ct);

                if (string.Equals(st.PeriodType, "Monthly", StringComparison.OrdinalIgnoreCase))
                {
                    await ComputeAndUpsertAsync(tenantId, correlationId, periodType: "Monthly", periodKey: st.PeriodKey, ct);
                    return;
                }

                if (string.Equals(st.PeriodType, "Quarterly", StringComparison.OrdinalIgnoreCase))
                {
                    await ComputeAndUpsertAsync(tenantId, correlationId, periodType: "Quarterly", periodKey: st.PeriodKey, ct);
                    return;
                }

                // YTD/Yearly statement => YTD already computed
                return;
            }

            // Unknown source => safest: YTD only
            var ytd = $"{entryDate.Year:D4}";
            await ComputeAndUpsertAsync(tenantId, correlationId, periodType: "YTD", periodKey: ytd, ct);
        }


        private async Task ComputeAndUpsertAsync(Guid tenantId, string correlationId, string periodType, string periodKey, CancellationToken ct)
        {
            var dedupeKey = $"snapshot.compute:{periodType}:{periodKey}";

            ProcessingJob job;

            var existingJob = await db.ProcessingJobs
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.JobType == "snapshot.compute" &&
                    x.DedupeKey == dedupeKey, ct);

            if (existingJob is null)
            {
                job = new ProcessingJob
                {
                    TenantId = tenantId,
                    JobType = "snapshot.compute",
                    DedupeKey = dedupeKey,
                    Status = "Started",
                    Attempts = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.ProcessingJobs.Add(job);
            }
            else
            {
                job = existingJob;
                job.Attempts += 1;
                job.Status = "Started";
                job.LastError = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }

            try
            {
                var (start, endExclusive) = GetRange(periodType, periodKey);

                // ======================================================
                // Source filtering per snapshot type
                // Monthly   -> ONLY Monthly Statements for that PeriodKey
                // Quarterly -> ONLY Quarterly Statements for that PeriodKey
                // YTD       -> Everything (Statements + Receipts)
                // ======================================================

                List<string>? allowedStatementSourceIds = null;

                if (periodType is "Monthly" or "Quarterly")
                {
                    allowedStatementSourceIds = await db.Statements
                        .AsNoTracking()
                        .Where(s => s.TenantId == tenantId
                                 && s.PeriodType == periodType
                                 && s.PeriodKey == periodKey)
                        .Select(s => s.Id.ToString()) // SourceId is Guid.ToString("D") -> ToString() matches
                        .ToListAsync(ct);
                }

                // ===== 1) Pull ledger lines in the period =====
                var linesQuery =
                    from e in db.LedgerEntries
                    join l in db.LedgerLines on e.Id equals l.LedgerEntryId
                    where e.TenantId == tenantId
                    where e.EntryDate >= start && e.EntryDate < endExclusive
                    select new
                    {
                        LedgerEntryId = e.Id,
                        e.SourceType,
                        e.SourceId,
                        LedgerLineId = l.Id,
                        l.Amount,
                        l.GstHst,
                        l.LineType
                    };

                // Apply source rules:
                // Apply source inclusion rules:
                // Monthly   -> ONLY Monthly statement ledger entries for that PeriodKey
                // Quarterly -> ONLY Quarterly statement ledger entries for that PeriodKey
                // YTD       -> receipts + statements (everything)
                if (periodType == "Monthly" || periodType == "Quarterly")
                {
                    // Find statements that match this exact bucket
                    var statementIdsForBucket = await db.Statements
                        .AsNoTracking()
                        .Where(s => s.TenantId == tenantId
                                 && s.PeriodType == periodType
                                 && s.PeriodKey == periodKey
                                 && s.Status == "Posted") // optional but recommended
                        .Select(s => s.Id.ToString("D"))
                        .ToListAsync(ct);

                    linesQuery = linesQuery.Where(x =>
                        x.SourceType == "Statement" &&
                        statementIdsForBucket.Contains(x.SourceId));
                }

                // else YTD: no filter (includes receipts + statements)

                var lines = await linesQuery.ToListAsync(ct);

                var revenueTotal = lines
                    .Where(x => x.LineType == "Income")
                    .Sum(x => x.Amount);

                // IMPORTANT: your existing logic treats "Fee" as expenses.
                // If you want receipt Expense lines to count as expenses in YTD,
                // you should include LineType == "Expense" here too.
                var expensesTotal = lines
                    .Where(x => x.LineType == "Fee" || x.LineType == "Expense")
                    .Sum(x => x.Amount);

                var itcTotal = lines
                    .Where(x => x.LineType == "Itc")
                    .Sum(x => x.GstHst);

                var taxCollectedTotal = lines
                    .Where(x => x.LineType == "TaxCollected")
                    .Sum(x => x.GstHst);

                var netTax = taxCollectedTotal - itcTotal;

                // ===== 2) EvidencePct/EstimatedPct from STATEMENT evidence, not receipts =====
                // This already excludes receipts because it joins to StatementLines via LedgerSourceLink.
                var statementEvidence = await (
                    from e in db.LedgerEntries
                    join l in db.LedgerLines on e.Id equals l.LedgerEntryId
                    join link in db.Set<DriverLedger.Domain.Ledger.LedgerSourceLink>() on l.Id equals link.LedgerLineId
                    join sl in db.StatementLines on link.StatementLineId equals sl.Id
                    where e.TenantId == tenantId
                    where e.EntryDate >= start && e.EntryDate < endExclusive
                    where link.StatementLineId != null
                    where sl.IsMetric == false
                    where sl.LineType == "Income" || sl.LineType == "Fee" || sl.LineType == "Expense"
                    select new
                    {
                        sl.CurrencyEvidence,
                        sl.ClassificationEvidence
                    }
                ).ToListAsync(ct);

                var totalMonetaryStatementLines = statementEvidence.Count;

                var evidencedMonetaryStatementLines = statementEvidence.Count(x =>
                    string.Equals(x.CurrencyEvidence, "Extracted", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.ClassificationEvidence, "Extracted", StringComparison.OrdinalIgnoreCase));

                var authority = AuthorityScoreCalculator.Compute(totalMonetaryStatementLines, evidencedMonetaryStatementLines);
                var evidencePct = authority.EvidencePct;
                var estimatedPct = authority.EstimatedPct;
                var authorityScore = authority.AuthorityScore;

                var totalsJson = JsonSerializer.Serialize(new
                {
                    revenue = revenueTotal,
                    expenses = expensesTotal,
                    itc = itcTotal,
                    taxCollected = taxCollectedTotal,
                    netTax
                }, JsonOpts);

                var snapshot = await db.LedgerSnapshots
                    .Include(s => s.Details)
                    .SingleOrDefaultAsync(s =>
                        s.TenantId == tenantId &&
                        s.PeriodType == periodType &&
                        s.PeriodKey == periodKey, ct);

                if (snapshot is null)
                {
                    snapshot = new LedgerSnapshot
                    {
                        TenantId = tenantId,
                        PeriodType = periodType,
                        PeriodKey = periodKey,
                        CalculatedAt = DateTimeOffset.UtcNow,
                        AuthorityScore = authorityScore,
                        EvidencePct = evidencePct,
                        EstimatedPct = estimatedPct,
                        TotalsJson = totalsJson,
                        Details = new List<SnapshotDetail>()
                    };

                    snapshot.Details.Add(new SnapshotDetail { TenantId = tenantId, SnapshotId = snapshot.Id, MetricKey = "RevenueTotal", Value = revenueTotal, EvidencePct = evidencePct, EstimatedPct = estimatedPct });
                    snapshot.Details.Add(new SnapshotDetail { TenantId = tenantId, SnapshotId = snapshot.Id, MetricKey = "ExpensesTotal", Value = expensesTotal, EvidencePct = evidencePct, EstimatedPct = estimatedPct });
                    snapshot.Details.Add(new SnapshotDetail { TenantId = tenantId, SnapshotId = snapshot.Id, MetricKey = "TaxCollectedTotal", Value = taxCollectedTotal, EvidencePct = evidencePct, EstimatedPct = estimatedPct });
                    snapshot.Details.Add(new SnapshotDetail { TenantId = tenantId, SnapshotId = snapshot.Id, MetricKey = "ItcTotal", Value = itcTotal, EvidencePct = evidencePct, EstimatedPct = estimatedPct });
                    snapshot.Details.Add(new SnapshotDetail { TenantId = tenantId, SnapshotId = snapshot.Id, MetricKey = "NetTax", Value = netTax, EvidencePct = evidencePct, EstimatedPct = estimatedPct });

                    db.LedgerSnapshots.Add(snapshot);
                }
                else
                {
                    snapshot.CalculatedAt = DateTimeOffset.UtcNow;
                    snapshot.AuthorityScore = authorityScore;
                    snapshot.EvidencePct = evidencePct;
                    snapshot.EstimatedPct = estimatedPct;
                    snapshot.TotalsJson = totalsJson;

                    UpsertDetail(snapshot, "RevenueTotal", revenueTotal, evidencePct, estimatedPct);
                    UpsertDetail(snapshot, "ExpensesTotal", expensesTotal, evidencePct, estimatedPct);
                    UpsertDetail(snapshot, "TaxCollectedTotal", taxCollectedTotal, evidencePct, estimatedPct);
                    UpsertDetail(snapshot, "ItcTotal", itcTotal, evidencePct, estimatedPct);
                    UpsertDetail(snapshot, "NetTax", netTax, evidencePct, estimatedPct);
                }

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "snapshot.updated",
                    EntityType = "LedgerSnapshot",
                    EntityId = $"{periodType}:{periodKey}",
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        periodType,
                        periodKey,
                        authorityScore,
                        evidencePct,
                        revenueTotal,
                        expensesTotal,
                        taxCollectedTotal,
                        itcTotal,
                        netTax,
                        totalMonetaryStatementLines,
                        evidencedMonetaryStatementLines
                    }, JsonOpts)
                });

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                job.LastError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "snapshot.compute.failed",
                    EntityType = "LedgerSnapshot",
                    EntityId = $"{periodType}:{periodKey}",
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts)
                });

                await db.SaveChangesAsync(ct);
                throw;
            }
        }

        private static void UpsertDetail(LedgerSnapshot snapshot, string metricKey, decimal value, decimal evidencePct, decimal estimatedPct)
        {
            var d = snapshot.Details.SingleOrDefault(x => x.MetricKey == metricKey);
            if (d is null)
            {
                snapshot.Details.Add(new SnapshotDetail
                {
                    TenantId = snapshot.TenantId,
                    SnapshotId = snapshot.Id,
                    MetricKey = metricKey,
                    Value = value,
                    EvidencePct = evidencePct,
                    EstimatedPct = estimatedPct
                });
            }
            else
            {
                d.Value = value;
                d.EvidencePct = evidencePct;
                d.EstimatedPct = estimatedPct;
            }
        }

        private static (DateOnly start, DateOnly endExclusive) GetRange(string periodType, string periodKey)
        {
            if (periodType == "Monthly")
            {
                var year = int.Parse(periodKey[..4]);
                var month = int.Parse(periodKey[5..7]);
                var start = new DateOnly(year, month, 1);
                var end = start.AddMonths(1);
                return (start, end);
            }

            if (periodType == "Quarterly")
            {
                var year = int.Parse(periodKey[..4]);
                var q = int.Parse(periodKey[^1..]);
                var startMonth = (q - 1) * 3 + 1;
                var start = new DateOnly(year, startMonth, 1);
                var end = start.AddMonths(3);
                return (start, end);
            }

            if (periodType == "YTD")
            {
                var year = int.Parse(periodKey);
                var start = new DateOnly(year, 1, 1);
                var end = start.AddYears(1);
                return (start, end);
            }

            throw new InvalidOperationException($"Unknown periodType '{periodType}'.");
        }
    }
}
