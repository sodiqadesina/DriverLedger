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
            Guid fileId;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantProvider>();

                tenant.SetTenant(tenantId);

                // 1) File (SAVE first so file.Id is real)
                var file = new Domain.Files.FileObject
                {
                    TenantId = tenantId,
                    BlobPath = "fake/receipt.pdf",
                    ContentType = "application/pdf",
                    OriginalName = "receipt.pdf",
                    Size = 123,
                    Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                };
                db.FileObjects.Add(file);
                await db.SaveChangesAsync();

                fileId = file.Id;

                // 2) Receipt (now reference real fileId)
                var receipt = new Domain.Receipts.Receipt
                {
                    TenantId = tenantId,
                    FileObjectId = fileId,
                    Status = "ReadyForPosting"
                };
                db.Receipts.Add(receipt);
                await db.SaveChangesAsync();

                receiptId = receipt.Id;

                // 3) Extraction evidence
                db.ReceiptExtractions.Add(new Domain.Receipts.Extraction.ReceiptExtraction
                {
                    TenantId = tenantId,
                    ReceiptId = receiptId,
                    ModelVersion = "fake",
                    RawJson = "{}",
                    NormalizedFieldsJson = """
        {"date":"2025-12-10","vendor":"TestVendor","total":113.00,"tax":13.00,"currency":"CAD"}
        """,
                    Confidence = 1.0m,
                    ExtractedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();

                // 4) Ledger entry (save to materialize entry.Id)
                var entry = new Domain.Ledger.LedgerEntry
                {
                    TenantId = tenantId,
                    EntryDate = entryDate,
                    SourceType = "Receipt",
                    SourceId = receiptId.ToString("D"),
                    PostedByType = "System",
                    CorrelationId = "c1",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.LedgerEntries.Add(entry);
                await db.SaveChangesAsync();

                ledgerEntryId = entry.Id;

                // 5) Ledger lines (save to materialize line Ids)
                var expenseLine = new Domain.Ledger.LedgerLine
                {
                    TenantId = tenantId,
                    LedgerEntryId = ledgerEntryId,
                    CategoryId = Guid.NewGuid(),
                    Amount = 100.00m,
                    GstHst = 0.00m,
                    DeductiblePct = 1.0m,
                    Memo = "TestVendor",
                    AccountCode = "EXP",
                    LineType = "Expense"
                };

                var itcLine = new Domain.Ledger.LedgerLine
                {
                    TenantId = tenantId,
                    LedgerEntryId = ledgerEntryId,
                    CategoryId = Guid.NewGuid(),
                    Amount = 0.00m,
                    GstHst = 13.00m,
                    DeductiblePct = 1.0m,
                    Memo = "GST/HST ITC (receipt)",
                    AccountCode = "ITC",
                    LineType = "Itc"
                };

                db.LedgerLines.Add(expenseLine);
                db.LedgerLines.Add(itcLine);
                await db.SaveChangesAsync();

                // 6) Source links (no DbSet property needed)
                db.Set<Domain.Ledger.LedgerSourceLink>().Add(new Domain.Ledger.LedgerSourceLink
                {
                    TenantId = tenantId,
                    LedgerLineId = expenseLine.Id,
                    ReceiptId = receiptId,
                    FileObjectId = fileId,
                    StatementLineId = null
                });

                db.Set<Domain.Ledger.LedgerSourceLink>().Add(new Domain.Ledger.LedgerSourceLink
                {
                    TenantId = tenantId,
                    LedgerLineId = itcLine.Id,
                    ReceiptId = receiptId,
                    FileObjectId = fileId,
                    StatementLineId = null
                });

                await db.SaveChangesAsync();
            }


            // Act
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

            // Assert
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();

                var monthly = await db.LedgerSnapshots.SingleOrDefaultAsync(s =>
                    s.TenantId == tenantId && s.PeriodType == "Monthly" && s.PeriodKey == "2025-12");

                var ytd = await db.LedgerSnapshots.SingleOrDefaultAsync(s =>
                    s.TenantId == tenantId && s.PeriodType == "YTD" && s.PeriodKey == "2025");

                monthly.Should().NotBeNull();
                ytd.Should().NotBeNull();

                monthly!.AuthorityScore.Should().Be(100);
                ytd!.AuthorityScore.Should().Be(100);
            }
        }
    }
}
