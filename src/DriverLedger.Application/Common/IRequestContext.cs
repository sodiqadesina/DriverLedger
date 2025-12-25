

namespace DriverLedger.Application.Common
{
    public interface IRequestContext
    {
        string? UserId { get; }
        string? CorrelationId { get; }
    }

}

