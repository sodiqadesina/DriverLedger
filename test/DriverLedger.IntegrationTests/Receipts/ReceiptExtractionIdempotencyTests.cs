
using DriverLedger.Application.Common;
using DriverLedger.Application.Messaging;
using DriverLedger.Application.Receipts.Messages;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Persistence;
using DriverLedger.Infrastructure.Receipts;
using DriverLedger.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace DriverLedger.IntegrationTests.Receipts
{

    public sealed class ReceiptExtractionIdempotencyTests : IClassFixture<SqlConnectionFixture>
    {
        private readonly SqlConnectionFixture _sql;

        public ReceiptExtractionIdempotencyTests(SqlConnectionFixture sql) => _sql = sql;

        [Fact]
        public async Task Same_receipt_received_envelope_twice_creates_single_extraction()
        {
            await using var factory = new ApiFactory(_sql.ConnectionString);

            var tenantId = Guid.NewGuid();
            Guid receiptId;
            Guid fileId;

            var blobPath = "fake/path.pdf";

            // Arrange
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantProvider>();
                var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

                tenant.SetTenant(tenantId);

                var file = new Domain.Files.FileObject
                {
                    TenantId = tenantId,
                    BlobPath = blobPath,
                    ContentType = "application/pdf",
                    OriginalName = "test.pdf",
                    Size = 123,
                    Sha256 = new string('0', 64)
                };
                db.FileObjects.Add(file);

                // Seed blob bytes so OpenReadAsync works
                await blob.UploadAsync(
                    blobPath: blobPath,
                    content: new MemoryStream(Encoding.UTF8.GetBytes("fake pdf bytes")),
                    contentType: "application/pdf",
                    ct: CancellationToken.None);

                var receipt = new Domain.Receipts.Receipt
                {
                    TenantId = tenantId,
                    FileObjectId = file.Id,
                    Status = "Submitted"
                };
                db.Receipts.Add(receipt);

                await db.SaveChangesAsync();

                fileId = file.Id;
                receiptId = receipt.Id;
            }

            // Act
            using (var scope = factory.Services.CreateScope())
            {
                var handler = scope.ServiceProvider.GetRequiredService<ReceiptExtractionHandler>();

                var env = new MessageEnvelope<ReceiptReceived>(
                    MessageId: "m1",
                    Type: "receipt.received.v1",
                    OccurredAt: DateTimeOffset.UtcNow,
                    TenantId: tenantId,
                    CorrelationId: "c1",
                    Data: new ReceiptReceived(receiptId, fileId)
                );

                await handler.HandleAsync(env, CancellationToken.None);
                await handler.HandleAsync(env, CancellationToken.None);
            }

            // Assert
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();

                var extractions = await db.ReceiptExtractions
                    .Where(x => x.TenantId == tenantId && x.ReceiptId == receiptId)
                    .ToListAsync();

                extractions.Should().HaveCount(1);

                var job = await db.ProcessingJobs.SingleAsync(x =>
                    x.TenantId == tenantId &&
                    x.JobType == "receipt.extract" &&
                    x.DedupeKey == $"receipt.extract:{receiptId}");

                job.Status.Should().Be("Succeeded");
                job.Attempts.Should().BeGreaterThanOrEqualTo(1);
            }
        }
    }
}
