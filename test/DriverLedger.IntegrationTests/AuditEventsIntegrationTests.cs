using DriverLedger.Infrastructure.Persistence;
using DriverLedger.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DriverLedger.IntegrationTests
{
    public sealed class AuditEventsIntegrationTests : IClassFixture<SqlConnectionFixture>
    {
        private readonly SqlConnectionFixture _sql;
        public AuditEventsIntegrationTests(SqlConnectionFixture sql) => _sql = sql;

        [Fact]
        public async Task Upload_file_writes_audit_event_file_uploaded()
        {
            await using var factory = new ApiFactory(_sql.ConnectionString);
            var client = factory.CreateClient();

            // Register and auth
            var (token, userId) = await RegisterAndGetTokenAndUserIdAsync(client);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Upload file (use allowed content-type)
            var (fileId, _) = await UploadFakePdfAsync(client);

            // Assert audit written
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();

            // TenantId == userId in your current model (tenant claim is user id)
            var tenantId = userId;

            var audit = await db.AuditEvents
                .IgnoreQueryFilters()
                .OrderByDescending(x => x.OccurredAt)
                .FirstOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.Action == "file.uploaded" &&
                    x.EntityType == "FileObject" &&
                    x.EntityId == fileId.ToString("D"));

            audit.Should().NotBeNull("upload should write audit event file.uploaded");
            audit!.ActorUserId.Should().NotBeNullOrWhiteSpace();
            audit.CorrelationId.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task Submit_receipt_writes_audit_event_receipt_submitted()
        {
            await using var factory = new ApiFactory(_sql.ConnectionString);
            var client = factory.CreateClient();

            // Register and auth
            var (token, userId) = await RegisterAndGetTokenAndUserIdAsync(client);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Upload file (needed to create receipt draft)
            var (fileId, _) = await UploadFakePdfAsync(client);

            // Create receipt draft
            var createRes = await client.PostAsJsonAsync("/receipts", new { fileObjectId = fileId });
            createRes.EnsureSuccessStatusCode();
            var receiptId = await ReadGuidPropertyAsync(createRes, "receiptId", fallback: "id");


            // Submit receipt
            var submitRes = await client.PostAsync($"/receipts/{receiptId}/submit", content: null);
            submitRes.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Accepted, HttpStatusCode.NoContent);

            // Assert audit written
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DriverLedgerDbContext>();

            var tenantId = userId;

            var audit = await db.AuditEvents
                .IgnoreQueryFilters()
                .OrderByDescending(x => x.OccurredAt)
                .FirstOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.Action == "receipt.submitted" &&
                    x.EntityType == "Receipt" &&
                    x.EntityId == receiptId.ToString("D"));

            audit.Should().NotBeNull("submit should write audit event receipt.submitted");
            audit!.ActorUserId.Should().NotBeNullOrWhiteSpace();
            audit.CorrelationId.Should().NotBeNullOrWhiteSpace();
        }

        // ---------------- helpers ----------------

        private static async Task<(string token, Guid userId)> RegisterAndGetTokenAndUserIdAsync(HttpClient client)
        {
            var email = $"{Guid.NewGuid():N}@test.local";
            var password = "Pass123$";

            var registerRes = await client.PostAsJsonAsync("/auth/register", new { email, password });
            registerRes.EnsureSuccessStatusCode();

            // token property could be "token" or "accessToken" depending on your DTO
            var token = await ReadStringPropertyAsync(registerRes, "token", fallback: "accessToken");

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var meRes = await client.GetAsync("/auth/me");
            meRes.EnsureSuccessStatusCode();

            // user id property could be "id" or "userId"
            var userId = await ReadGuidPropertyAsync(meRes, "id", fallback: "userId");

            return (token, userId);
        }

        private static async Task<(Guid fileId, HttpResponseMessage response)> UploadFakePdfAsync(HttpClient client)
        {
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

            using var form = new MultipartFormDataContent();

            var fileContent = new ByteArrayContent(pdfBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            form.Add(fileContent, "file", "test.pdf");

            var res = await client.PostAsync("/files", form);
            var body = await res.Content.ReadAsStringAsync();

            res.IsSuccessStatusCode.Should().BeTrue($"upload failed: {(int)res.StatusCode} {res.StatusCode}. Body: {body}");

            // IMPORTANT: your API returns {"fileObjectId": "..."} not {"id": "..."}
            var id = await ReadGuidPropertyAsync(res, "fileObjectId", fallback: "id");
            return (id, res);
        }


        private static async Task<Guid> ReadGuidPropertyAsync(HttpResponseMessage res, string property, string? fallback = null)
        {
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (TryGetProperty(doc.RootElement, property, out var el) && el.ValueKind == JsonValueKind.String &&
                Guid.TryParse(el.GetString(), out var g))
                return g;

            if (fallback is not null &&
                TryGetProperty(doc.RootElement, fallback, out el) && el.ValueKind == JsonValueKind.String &&
                Guid.TryParse(el.GetString(), out g))
                return g;

            throw new InvalidOperationException($"Response JSON did not contain guid property '{property}' (or fallback '{fallback}'). JSON: {json}");
        }

        private static async Task<string> ReadStringPropertyAsync(HttpResponseMessage res, string property, string? fallback = null)
        {
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (TryGetProperty(doc.RootElement, property, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString()!;

            if (fallback is not null && TryGetProperty(doc.RootElement, fallback, out el) && el.ValueKind == JsonValueKind.String)
                return el.GetString()!;

            throw new InvalidOperationException($"Response JSON did not contain string property '{property}' (or fallback '{fallback}'). JSON: {json}");
        }

        private static bool TryGetProperty(JsonElement root, string name, out JsonElement element)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out element))
                return true;

            // Handle wrapper objects like { data: { ... } }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out element))
                return true;

            element = default;
            return false;
        }
    }
}
