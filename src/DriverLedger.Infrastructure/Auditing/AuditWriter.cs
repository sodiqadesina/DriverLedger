using DriverLedger.Application.Auditing;
using DriverLedger.Application.Common;
using DriverLedger.Domain.Auditing;
using DriverLedger.Infrastructure.Persistence;
using System.Text.Json;

namespace DriverLedger.Infrastructure.Auditing
{
    public sealed class AuditWriter : IAuditWriter
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        private readonly DriverLedgerDbContext _db;
        private readonly ITenantProvider _tenant;
        private readonly IRequestContext _ctx;
        private readonly IClock _clock;

        public AuditWriter(
            DriverLedgerDbContext db,
            ITenantProvider tenant,
            IRequestContext ctx,
            IClock clock)
        {
            _db = db;
            _tenant = tenant;
            _ctx = ctx;
            _clock = clock;
        }

        public async Task WriteAsync(
            string action,
            string entityType,
            string entityId,
            object? metadata = null,
            CancellationToken ct = default)
        {
            var tenantId = _tenant.TenantId
                ?? throw new InvalidOperationException("TenantId not set (ITenantProvider.TenantId is null).");

            var userId = _ctx.UserId ?? "unknown";
            var correlationId = _ctx.CorrelationId ?? Guid.NewGuid().ToString("N");

            var ev = new AuditEvent
            {
                TenantId = tenantId,
                ActorUserId = userId,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                OccurredAt = _clock.UtcNow,
                CorrelationId = correlationId,
                MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOpts)
            };

            _db.Add(ev);
            await _db.SaveChangesAsync(ct);
        }
    }
}
