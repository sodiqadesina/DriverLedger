using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Messaging.Events;
using DriverLedger.Infrastructure.Ledger;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriverLedger.IntegrationTests.Ledger
{
    public sealed class LedgerPostingIdempotencyTests : IClassFixture<SqlConnectionFixture>
    {
        private readonly SqlConnectionFixture _sql;

        public LedgerPostingIdempotencyTests(SqlConnectionFixture sql) => _sql = sql;

        [Fact]
        public async Task Same_receipt_extracted_twice_creates_single_ledger_entry()
        {
            await using var factory = new ApiFactory(_sql.ConnectionString);

            Guid tenantId;
            Guid receiptId;
            Guid fileId;

            // Arrange: seed DB (DO NOT set Ids manually)
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantProvider>();

                tenantId = Guid.NewGuid();
                tenant.SetTenant(tenantId);

                var file = new Domain.Files.FileObject
                {
                    TenantId = tenantId,
                    BlobPath = "fake/path.pdf",
                    ContentType = "application/pdf",
                    OriginalName = "test.pdf",
                    Size = 123,
                    Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                };

                db.FileObjects.Add(file);

                var receipt = new Domain.Receipts.Receipt
                {
                    TenantId = tenantId,
                    FileObjectId = file.Id,
                    Status = "ReadyForPosting"
                };

                db.Receipts.Add(receipt);

                await db.SaveChangesAsync();

                fileId = file.Id;
                receiptId = receipt.Id;

                if (receipt.FileObjectId != fileId)
                {
                    receipt.FileObjectId = fileId;
                    await db.SaveChangesAsync();
                }

                db.ReceiptExtractions.Add(new Domain.Receipts.Extraction.ReceiptExtraction
                {
                    TenantId = tenantId,
                    ReceiptId = receiptId,
                    ModelVersion = "fake",
                    RawJson = "{}",
                    NormalizedFieldsJson = """
                    {"date":"2025-01-10","vendor":"Shell","total":20.00,"tax":2.60,"currency":"CAD"}
                    """,
                    Confidence = 1.0m,
                    ExtractedAt = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync();
            }

            // Act: call handler twice
            using (var scope = factory.Services.CreateScope())
            {
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantProvider>();
                var handler = scope.ServiceProvider.GetRequiredService<ReceiptToLedgerPostingHandler>();

                tenant.SetTenant(tenantId);

                var env = new MessageEnvelope<ReceiptExtractedV1>(
                    MessageId: "m1",
                    Type: "receipt.extracted.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: "c1",
                    Data: new ReceiptExtractedV1(
                        ReceiptId: receiptId,
                        FileObjectId: fileId,
                        Confidence: 1.0m,
                        IsHold: false,
                        HoldReason: ""
                    )
                );

                await handler.HandleAsync(env, CancellationToken.None);
                await handler.HandleAsync(env, CancellationToken.None);
            }

            // Assert
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();

                var entries = await db.LedgerEntries
                    .Where(x => x.TenantId == tenantId
                                && x.SourceType == "Receipt"
                                && x.SourceId == receiptId.ToString("D"))
                    .ToListAsync();

                entries.Should().HaveCount(1);

                var entryId = entries[0].Id;

                var lines = await db.LedgerLines
                    .Where(x => x.LedgerEntryId == entryId)
                    .ToListAsync();

                // NEW behavior: receipt posts 2 lines (Expense + Itc)
                lines.Should().HaveCount(2);

                lines.Should().ContainSingle(l => l.LineType == "Expense");
                lines.Should().ContainSingle(l => l.LineType == "Itc");
            }
        }
    }
}
