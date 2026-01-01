

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

            // Ensures EF global tenant filters see this tenant
            tenantProvider.SetTenant(tenantId);

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["correlationId"] = correlationId,
                ["messageId"] = envelope.MessageId,
                ["ledgerEntryId"] = payload.LedgerEntryId
            });

            // EntryDate in payload is "yyyy-MM-dd"
            if (!DateOnly.TryParse(payload.EntryDate, out var entryDate))
            {
                logger.LogWarning("Invalid EntryDate format in ledger.posted.v1: {EntryDate}", payload.EntryDate);
                return;
            }

            // monthly key: YYYY-MM
            var monthlyKey = $"{entryDate.Year:D4}-{entryDate.Month:D2}";
            await ComputeAndUpsertAsync(tenantId, correlationId, periodType: "Monthly", periodKey: monthlyKey, ct);

            // ytd key: YYYY
            var ytdKey = $"{entryDate.Year:D4}";
            await ComputeAndUpsertAsync(tenantId, correlationId, periodType: "YTD", periodKey: ytdKey, ct);
        }

        private async Task ComputeAndUpsertAsync(Guid tenantId, string correlationId, string periodType, string periodKey, CancellationToken ct)
        {
            var dedupeKey = $"snapshot.compute:{periodType}:{periodKey}";

            // Track attempts/status, but do NOT use this to prevent recomputation (totals change).
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

                // Totals for the period
                var linesQuery =
                    from e in db.LedgerEntries
                    join l in db.LedgerLines on e.Id equals l.LedgerEntryId
                    where e.TenantId == tenantId
                    where e.EntryDate >= start && e.EntryDate < endExclusive
                    select new
                    {
                        e.Id,
                        e.SourceType,
                        e.SourceId,
                        l.Amount,
                        l.GstHst
                    };

                var lines = await linesQuery.ToListAsync(ct);

                var expensesTotal = lines.Sum(x => x.Amount);
                var itcTotal = lines.Sum(x => x.GstHst);

                var totalLines = lines.Count;

                // EvidencePct: lines from Receipt sources that have extraction and receipt not HOLD
                var receiptSourceIds = lines
                    .Where(x => x.SourceType == "Receipt")
                    .Select(x => x.SourceId)
                    .Distinct()
                    .ToList();

                var receiptIds = receiptSourceIds
                    .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty)
                    .ToList();

                var evidencedReceiptIds = await (
                    from r in db.Receipts
                    where r.TenantId == tenantId
                    where receiptIds.Contains(r.Id)
                    where r.Status != "HOLD"
                    join ex in db.ReceiptExtractions on r.Id equals ex.ReceiptId
                    select r.Id
                ).Distinct().ToListAsync(ct);

                var evidencedLines = lines.Count(x =>
                    x.SourceType == "Receipt" &&
                    Guid.TryParse(x.SourceId, out var rid) &&
                    evidencedReceiptIds.Contains(rid));

                var authority = AuthorityScoreCalculator.Compute(totalLines, evidencedLines);
                var evidencePct = authority.EvidencePct;
                var estimatedPct = authority.EstimatedPct;
                var authorityScore = authority.AuthorityScore;

                var totalsJson = JsonSerializer.Serialize(new
                {
                    revenue = 0m,
                    expenses = expensesTotal,
                    itc = itcTotal,
                    netTax = -itcTotal
                }, JsonOpts);

                // Upsert snapshot
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

                    snapshot.Details.Add(new SnapshotDetail
                    {
                        TenantId = tenantId,
                        SnapshotId = snapshot.Id,
                        MetricKey = "ExpensesTotal",
                        Value = expensesTotal,
                        EvidencePct = evidencePct,
                        EstimatedPct = estimatedPct
                    });

                    snapshot.Details.Add(new SnapshotDetail
                    {
                        TenantId = tenantId,
                        SnapshotId = snapshot.Id,
                        MetricKey = "ItcTotal",
                        Value = itcTotal,
                        EvidencePct = evidencePct,
                        EstimatedPct = estimatedPct
                    });

                    snapshot.Details.Add(new SnapshotDetail
                    {
                        TenantId = tenantId,
                        SnapshotId = snapshot.Id,
                        MetricKey = "NetTax",
                        Value = -itcTotal,
                        EvidencePct = evidencePct,
                        EstimatedPct = estimatedPct
                    });

                    db.LedgerSnapshots.Add(snapshot);
                }
                else
                {
                    snapshot.CalculatedAt = DateTimeOffset.UtcNow;
                    snapshot.AuthorityScore = authorityScore;
                    snapshot.EvidencePct = evidencePct;
                    snapshot.EstimatedPct = estimatedPct;
                    snapshot.TotalsJson = totalsJson;

                    UpsertDetail(snapshot, "ExpensesTotal", expensesTotal, evidencePct, estimatedPct);
                    UpsertDetail(snapshot, "ItcTotal", itcTotal, evidencePct, estimatedPct);
                    UpsertDetail(snapshot, "NetTax", -itcTotal, evidencePct, estimatedPct);
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
                        expensesTotal,
                        itcTotal
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
