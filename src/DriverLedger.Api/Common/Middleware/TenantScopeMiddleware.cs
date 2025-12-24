using DriverLedger.Application.Common;
using System.Security.Claims;

namespace DriverLedger.Api.Common.Middleware
{
    public sealed class TenantScopeMiddleware : IMiddleware
    {
        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            // Only apply when authenticated
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var tenantIdStr = context.User.FindFirstValue("tenantId");
                if (Guid.TryParse(tenantIdStr, out var tenantId))
                {
                    var tenantProvider = context.RequestServices.GetRequiredService<ITenantProvider>();
                    tenantProvider.SetTenant(tenantId);
                }
            }

            await next(context);
        }
    }
}
