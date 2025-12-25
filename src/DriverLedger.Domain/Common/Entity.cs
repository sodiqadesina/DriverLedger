

namespace DriverLedger.Domain.Common
{
    public abstract class Entity
    {
        public Guid Id { get; protected set; } = Guid.NewGuid();
    }

    public interface ITenantScoped
    {
        Guid TenantId { get; set; }
    }

}
