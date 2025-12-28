

namespace DriverLedger.Application.Messaging
{
    /// <summary>
    /// Service Bus message envelope. Keeping stable + versioned.
    /// </summary>
    public sealed record MessageEnvelope<T>(
        string MessageId,
        string Type,
        DateTimeOffset OccurredAt,
        Guid TenantId,
        string CorrelationId,
        T Data
    );
}
