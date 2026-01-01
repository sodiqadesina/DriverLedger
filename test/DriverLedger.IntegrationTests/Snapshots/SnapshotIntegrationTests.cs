

using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Statements.Snapshots;
using DriverLedger.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriverLedger.IntegrationTests.Snapshots
{

    public sealed class SnapshotIntegrationTests : IClassFixture<SqlConnectionFixture>
    {
        private readonly SqlConnectionFixture _sql;

        public SnapshotIntegrationTests(SqlConnectionFixture sql) => _sql = sql;

        [Fact]
        public async Task Ledger_posted_creates_monthly_and_ytd_snapshots()
        {
            await using var factory = new ApiFactory(_sql.ConnectionString);

            var tenantId = Guid.NewGuid();
            var entryDate = new DateOnly(2025, 12, 10);

            Guid ledgerEntryId;
            Guid receiptId;

            // Arrange: seed ledger + line + evidence receipt/extraction (NO manual Id assignment)
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantProvider>();

                tenant.SetTenant(tenantId);

                // Create receipt evidence
                var receipt = new Domain.Receipts.Receipt // <-- adjust namespace/type to your actual Receipt class
                {
                    TenantId = tenantId,
                    FileObjectId = Guid.NewGuid(), // ok if FileObjectId is a Guid FK and doesn't require existing FileObject for this test
                    Status = "ReadyForPosting"
                };
                db.Receipts.Add(receipt);

                await db.SaveChangesAsync();
                receiptId = receipt.Id;

                db.ReceiptExtractions.Add(new Domain.Receipts.Extraction.ReceiptExtraction // <-- adjust namespace/type
                {
                    TenantId = tenantId,
                    ReceiptId = receiptId,
                    ModelVersion = "fake",
                    RawJson = "{}",
                    NormalizedFieldsJson = "{}",
                    Confidence = 1.0m,
                    ExtractedAt = DateTimeOffset.UtcNow
                });

                // Create ledger entry + line (source points to receipt)
                var entry = new Domain.Ledger.LedgerEntry // <-- adjust namespace/type
                {
                    TenantId = tenantId,
                    EntryDate = entryDate,
                    SourceType = "Receipt",
                    SourceId = receiptId.ToString("D"),
                    PostedByType = "System",
                    CorrelationId = "c1"
                };
                db.LedgerEntries.Add(entry);

                await db.SaveChangesAsync();
                ledgerEntryId = entry.Id;

                db.LedgerLines.Add(new Domain.Ledger.LedgerLine // <-- adjust namespace/type
                {
                    LedgerEntryId = ledgerEntryId,
                    CategoryId = Guid.NewGuid(),
                    Amount = 100m,
                    GstHst = 13m,
                    DeductiblePct = 1.0m,
                    Memo = "test",
                    AccountCode = "EXP"
                });

                await db.SaveChangesAsync();
            }

            // Act: run SnapshotCalculator (what LedgerPostedFunction would do)
            using (var scope = factory.Services.CreateScope())
            {
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantProvider>();
                var calc = scope.ServiceProvider.GetRequiredService<SnapshotCalculator>();

                tenant.SetTenant(tenantId);

                var env = new MessageEnvelope<LedgerPosted>(
                    MessageId: "m1",
                    Type: "ledger.posted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: "c1",
                    Data: new LedgerPosted(
                        LedgerEntryId: ledgerEntryId,
                        SourceType: "Receipt",
                        SourceId: receiptId.ToString("D"),
                        EntryDate: entryDate.ToString("yyyy-MM-dd")
                    )
                );

                await calc.HandleAsync(env, CancellationToken.None);
            }

            // Assert: snapshots exist + authority score
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();

                var monthly = await db.LedgerSnapshots.SingleOrDefaultAsync(s =>
                    s.TenantId == tenantId && s.PeriodType == "Monthly" && s.PeriodKey == "2025-12");

                var ytd = await db.LedgerSnapshots.SingleOrDefaultAsync(s =>
                    s.TenantId == tenantId && s.PeriodType == "YTD" && s.PeriodKey == "2025");

                monthly.Should().NotBeNull();
                ytd.Should().NotBeNull();

                // With 1 line, and receipt has extraction + not HOLD => evidencePct=1 => authority=100
                monthly!.AuthorityScore.Should().Be(100);
                ytd!.AuthorityScore.Should().Be(100);
            }
        }
    }
}
