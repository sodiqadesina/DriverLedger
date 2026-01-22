using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Application.Statements.Messages;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Ledger;
using DriverLedger.Domain.Ops;
using DriverLedger.Infrastructure.Messaging;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Ledger
{
    /// <summary>
    /// Posts reconciliation variances into the ledger as a single adjustment entry so that
    /// the ledger-based YTD aligns with the yearly statement totals (for the chosen revenue anchor).
    ///
    /// Core rules:
    /// - The yearly statement itself stays ReconciliationOnly and is NEVER posted directly.
    /// - We post ONE ledger entry per reconciliation run (idempotent by SourceType+SourceId).
    /// - We post ONLY a strict allowlist of variances that are meant to affect snapshots/YTD.
    ///
    /// IMPORTANT:
    /// Uber reconciliation can compute multiple "income-like" metrics (e.g., statement display total vs revenue anchor).
    /// We only post the snapshot revenue anchor variance:
    ///   - Income.GrossUberRidesFares
    /// We DO NOT post Income.UberRidesGross (display total) to avoid double-adjusting revenue.
    ///
    /// Variance meaning:
    /// - VarianceAmount in DB is (MonthlyTotal - YearlyTotal)
    /// - We want ledger to match yearly -> AdjustmentDelta = (YearlyTotal - MonthlyTotal) = -VarianceAmount
    ///
    /// Tax representation:
    /// - TaxCollected.* and Itc/ITC.* variances are carried in LedgerLine.GstHst (Amount=0).
    /// </summary>
    public sealed class ReconciliationToLedgerPostingHandler(
        DriverLedgerDbContext db,
        ITenantProvider tenantProvider,
        IMessagePublisher publisher,
        ILogger<ReconciliationToLedgerPostingHandler> logger)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        // TODO: Replace with a real “Reconciliation Adjustments” category (seed/config/table lookup).
        private static readonly Guid ReconciliationCategoryId =
            Guid.Parse("00000000-0000-0000-0000-000000000001");

        // Small tolerance to avoid posting noise caused by rounding/pennies.
        private const decimal AmountTolerance = 0.01m;

        // Only these variance keys are allowed to affect the ledger/YTD.
        // This prevents "double revenue" adjustments (e.g., posting both Income.* metrics).
        private static readonly HashSet<string> PostableMetricKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Income.GrossUberRidesFares",
            "Fee.UberRidesFees",
            "TaxCollected.GSTHST",
            "ITC.GSTHSTPaidToUber"
        };

        public async Task HandleAsync(MessageEnvelope<ReconciliationCompleted> envelope, CancellationToken ct)
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
                ["reconciliationRunId"] = msg.ReconciliationRunId
            });

            // -------------------------------------------------------
            // Load run + variances
            // -------------------------------------------------------
            var run = await db.ReconciliationRuns
                .Include(r => r.Variances)
                .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == msg.ReconciliationRunId, ct);

            if (run is null)
            {
                logger.LogWarning("ReconciliationRun not found. RunId={RunId}", msg.ReconciliationRunId);
                return;
            }

            if (!string.Equals(run.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Skipping reconciliation ledger posting because run is not Completed. Status={Status}",
                    run.Status);
                return;
            }

            // -------------------------------------------------------
            // Idempotency gates
            // -------------------------------------------------------
            const string sourceType = "Reconciliation";
            var sourceId = run.Id.ToString("D"); // strict idempotency per run

            var existingLedgerEntry = await db.LedgerEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(e =>
                    e.TenantId == tenantId &&
                    e.SourceType == sourceType &&
                    e.SourceId == sourceId, ct);

            if (existingLedgerEntry is not null)
            {
                logger.LogInformation(
                    "Reconciliation adjustment already posted. LedgerEntryId={LedgerEntryId}",
                    existingLedgerEntry.Id);
                return;
            }

            var dedupeKey = $"ledger.post:reconciliation:{run.Id:D}";
            var job = await db.ProcessingJobs
                .SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.JobType == "ledger.post.reconciliation" &&
                    x.DedupeKey == dedupeKey, ct);

            if (job is not null && string.Equals(job.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Reconciliation ledger posting already succeeded (job dedupe). DedupeKey={DedupeKey}",
                    dedupeKey);
                return;
            }

            if (job is null)
            {
                job = new ProcessingJob
                {
                    TenantId = tenantId,
                    JobType = "ledger.post.reconciliation",
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
                job.Attempts += 1;
                job.Status = "Started";
                job.LastError = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }

            try
            {
                // -------------------------------------------------------
                // Build adjustment entry
                // -------------------------------------------------------
                if (!int.TryParse(run.PeriodKey, out var year))
                    throw new InvalidOperationException($"ReconciliationRun.PeriodKey must be YYYY. Got '{run.PeriodKey}'.");

                // Place on last day of year so it lands within the reconciled year.
                var entryDate = new DateOnly(year, 12, 31);

                var entry = new LedgerEntry
                {
                    TenantId = tenantId,
                    EntryDate = entryDate,
                    SourceType = sourceType,
                    SourceId = sourceId,
                    PostedByUserId = null,
                    PostedByType = "System",
                    CorrelationId = correlationId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Lines = new List<LedgerLine>()
                };

                var postedLines = 0;
                var skippedNonPostable = 0;
                var skippedNoise = 0;

                foreach (var v in run.Variances)
                {
                    // Ignore metric variances entirely (no ledger impact).
                    if (v.MetricKey.StartsWith("Metric.", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Strict allowlist: only post the metrics that are intended to drive YTD/snapshots.
                    if (!PostableMetricKeys.Contains(v.MetricKey))
                    {
                        skippedNonPostable++;
                        continue;
                    }

                    // Convert stored variance (Monthly - Yearly) to ledger delta (Yearly - Monthly).
                    var delta = decimal.Round(-v.VarianceAmount, 2, MidpointRounding.AwayFromZero);

                    if (Math.Abs(delta) < AmountTolerance)
                    {
                        skippedNoise++;
                        continue;
                    }

                    var (lineType, amount, gstHst, memo) = MapVarianceToLedgerLine(v.MetricKey, delta);

                    entry.Lines.Add(new LedgerLine
                    {
                        TenantId = tenantId,
                        LedgerEntryId = entry.Id,
                        CategoryId = ReconciliationCategoryId,
                        LineType = lineType,
                        Amount = amount,
                        GstHst = gstHst,
                        DeductiblePct = 1.0m,
                        Memo = memo,
                        SourceLinks = new List<LedgerSourceLink>()
                    });

                    postedLines++;
                }

                if (postedLines == 0)
                {
                    logger.LogInformation(
                        "No material postable variances for reconciliation run {RunId}. (skippedNonPostable={SkippedNonPostable}, skippedNoise={SkippedNoise})",
                        run.Id, skippedNonPostable, skippedNoise);

                    job.Status = "Succeeded";
                    job.UpdatedAt = DateTimeOffset.UtcNow;

                    db.AuditEvents.Add(new AuditEvent
                    {
                        TenantId = tenantId,
                        ActorUserId = "system",
                        Action = "ledger.reconciliation.noop",
                        EntityType = "ReconciliationRun",
                        EntityId = run.Id.ToString("D"),
                        OccurredAt = DateTimeOffset.UtcNow,
                        CorrelationId = correlationId,
                        MetadataJson = JsonSerializer.Serialize(new
                        {
                            runId = run.Id.ToString("D"),
                            run.Provider,
                            run.PeriodType,
                            run.PeriodKey,
                            postedLines = 0,
                            skippedNonPostable,
                            skippedNoise,
                            postableMetricKeys = PostableMetricKeys.OrderBy(x => x).ToArray()
                        }, JsonOpts)
                    });

                    await db.SaveChangesAsync(ct);
                    return;
                }

                db.LedgerEntries.Add(entry);

                db.AuditEvents.Add(new AuditEvent
                {
                    TenantId = tenantId,
                    ActorUserId = "system",
                    Action = "ledger.reconciliation.posted",
                    EntityType = "LedgerEntry",
                    EntityId = entry.Id.ToString("D"),
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = correlationId,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        runId = run.Id.ToString("D"),
                        run.Provider,
                        run.PeriodType,
                        run.PeriodKey,
                        entryDate = entryDate.ToString("yyyy-MM-dd"),
                        postedLines,
                        skippedNonPostable,
                        skippedNoise
                    }, JsonOpts)
                });

                job.Status = "Succeeded";
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);

                // -------------------------------------------------------
                // Publish ledger.posted so snapshots recompute
                // -------------------------------------------------------
                var posted = new LedgerPosted(
                    LedgerEntryId: entry.Id,
                    SourceType: sourceType,
                    SourceId: sourceId,
                    EntryDate: entryDate.ToString("yyyy-MM-dd")
                );

                var outEnvelope = new MessageEnvelope<LedgerPosted>(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Type: "ledger.posted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: correlationId,
                    Data: posted
                );

                await publisher.PublishAsync("q.ledger.posted", outEnvelope, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciliation ledger posting failed for RunId={RunId}", run.Id);

                job.Status = "Failed";
                job.LastError = ex.Message;
                job.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);
                throw;
            }
        }

        private static (string LineType, decimal Amount, decimal GstHst, string Memo) MapVarianceToLedgerLine(string metricKey, decimal delta)
        {
            // Snapshot revenue anchor: Amount
            if (metricKey.Equals("Income.GrossUberRidesFares", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    LineType: "Income",
                    Amount: delta,
                    GstHst: 0m,
                    Memo: $"Reconciliation adjustment: {metricKey}"
                );
            }

            // Fees: Amount
            if (metricKey.Equals("Fee.UberRidesFees", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    LineType: "Fee",
                    Amount: delta,
                    GstHst: 0m,
                    Memo: $"Reconciliation adjustment: {metricKey}"
                );
            }

            // TaxCollected: GstHst (Amount=0)
            if (metricKey.Equals("TaxCollected.GSTHST", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    LineType: "TaxCollected",
                    Amount: 0m,
                    GstHst: delta,
                    Memo: $"Reconciliation adjustment: {metricKey}"
                );
            }

            // ITC: GstHst (Amount=0)
            if (metricKey.Equals("ITC.GSTHSTPaidToUber", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    LineType: "Itc",
                    Amount: 0m,
                    GstHst: delta,
                    Memo: $"Reconciliation adjustment: {metricKey}"
                );
            }

            // Should never happen due to allowlist, but keep safe.
            return (
                LineType: "Other",
                Amount: delta,
                GstHst: 0m,
                Memo: $"Reconciliation adjustment (unexpected): {metricKey}"
            );
        }
    }
}
