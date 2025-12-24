using DriverLedger.Domain.Files;
using DriverLedger.Infrastructure.Files;
using DriverLedger.Infrastructure.Persistence;
using System.Security.Claims;
using System.Security.Cryptography;

namespace DriverLedger.Api.Modules.Files
{

    public static class ApiFiles
    {
        public static void MapFileEndpoints(WebApplication app)
        {
            var group = app.MapGroup("/files").WithTags("Files").RequireAuthorization("RequireDriver");

            group.MapPost("/", async (HttpRequest request, DriverLedgerDbContext db, IBlobStorage blob, CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest("multipart/form-data required.");

                var form = await request.ReadFormAsync(ct);
                var file = form.Files["file"];
                if (file is null || file.Length == 0)
                    return Results.BadRequest("Missing file.");

                // Basic content validation (expand later)
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "application/pdf", "image/jpeg", "image/png", "image/heic" };

                if (!allowed.Contains(file.ContentType))
                    return Results.BadRequest("Unsupported content type.");

                var tenantId = GetTenantId(request.HttpContext);

                // Compute sha256 for dedupe
                string sha256;
                await using (var s = file.OpenReadStream())
                {
                    using var sha = SHA256.Create();
                    var hash = await sha.ComputeHashAsync(s, ct);
                    sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }

                var blobPath = $"{tenantId}/uploads/{Guid.NewGuid():N}-{Sanitize(file.FileName)}";

                // Upload (rewind stream)
                await using (var uploadStream = file.OpenReadStream())
                    await blob.UploadAsync(blobPath, uploadStream, file.ContentType, ct);

                var fo = new FileObject
                {
                    TenantId = tenantId,
                    BlobPath = blobPath,
                    Sha256 = sha256,
                    Size = file.Length,
                    ContentType = file.ContentType,
                    OriginalName = file.FileName,
                    Source = "UserUpload"
                };

                db.FileObjects.Add(fo);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new { fileObjectId = fo.Id });
            });
        }

        private static Guid GetTenantId(HttpContext ctx)
        {
            var tenantIdStr = ctx.User.FindFirstValue("tenantId");
            return Guid.Parse(tenantIdStr!);
        }

        private static string Sanitize(string name)
            => string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ')).Trim();
    }
}
