

using DriverLedger.Infrastructure.Persistence;
using DriverLedger.IntegrationTests.Fixtures;
using DriverLedger.IntegrationTests.Helpers;
using DriverLedger.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DriverLedger.IntegrationTests.Receipts
{
    public sealed class ReceiptSubmitIntegrationTests : IClassFixture<SqlConnectionFixture>
    {
        private readonly SqlConnectionFixture _sql;

        public ReceiptSubmitIntegrationTests(SqlConnectionFixture sql) => _sql = sql;

        private sealed record CreateReceiptResponse(Guid receiptId, string status);

        private sealed record MeResponse(Guid TenantId);


        [Fact]
        public async Task Submitting_receipt_publishes_receipt_received_message()
        {
            await using var factory = new ApiFactory(_sql.ConnectionString);
            var client = factory.CreateClient();

            // register/login
            var token = await TestAuth.RegisterAndLoginAsync(client);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Seed a real FileObject for the tenant used by the token
            Guid fileObjectId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();

                // IMPORTANT: tenantId comes from the JWT claim your auth issues.
                // If your TestAuth helper can return tenantId, use it.
                // Otherwise, read it from /auth/me.
                var meRes = await client.GetAsync("/auth/me");
                meRes.EnsureSuccessStatusCode();

                var me = await meRes.Content.ReadFromJsonAsync<MeResponse>();
                me.Should().NotBeNull();

                var tenantId = me!.TenantId;

                var file = new DriverLedger.Domain.Files.FileObject
                {
                    TenantId = tenantId,
                    BlobPath = "fake/path.pdf",
                    ContentType = "application/pdf",
                    OriginalName = "test.pdf",
                    Size = 123,
                    Sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
                };

                db.FileObjects.Add(file);
                await db.SaveChangesAsync();

                fileObjectId = file.Id;
            }

            // Create receipt (now file exists so API returns 200)
            var create = await client.PostAsJsonAsync("/receipts", new { fileObjectId });
            create.StatusCode.Should().Be(HttpStatusCode.OK);

            var created = await create.Content.ReadFromJsonAsync<CreateReceiptResponse>();
            created.Should().NotBeNull();
            var receiptId = created!.receiptId;

            // Submit
            var submit = await client.PostAsync($"/receipts/{receiptId}/submit", content: null);
            submit.StatusCode.Should().Be(HttpStatusCode.OK);

            // Assert publish happened (requires API factory override to InMemoryMessagePublisher)
            var pub = factory.Services.GetRequiredService<InMemoryMessagePublisher>();
            pub.Messages.Should().Contain(m =>
                m.EntityName == "q.receipt.received" &&
                m.Type == "receipt.received.v1" &&
                m.TenantId != Guid.Empty);
        }
    }
}
