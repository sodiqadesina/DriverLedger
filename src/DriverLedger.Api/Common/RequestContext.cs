using System.IdentityModel.Tokens.Jwt;
using DriverLedger.Application.Common;

namespace DriverLedger.Api.Common
{
    public sealed class RequestContext : IRequestContext
    {
        private const string CorrelationKey = "x-correlation-id";
        private readonly IHttpContextAccessor _http;

        public RequestContext(IHttpContextAccessor http) => _http = http;

        public string? UserId
        {
            get
            {
                var ctx = _http.HttpContext;
                if (ctx?.User?.Identity?.IsAuthenticated != true) return null;

                return ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                       ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            }
        }

        public string? CorrelationId
        {
            get
            {
                var ctx = _http.HttpContext;
                if (ctx is null) return null;

                // prefer items (set by CorrelationMiddleware)
                if (ctx.Items.TryGetValue(CorrelationKey, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                    return s;

                // fallback to header
                if (ctx.Request.Headers.TryGetValue(CorrelationKey, out var hv) && !string.IsNullOrWhiteSpace(hv))
                    return hv.ToString();

                return null;
            }
        }
    }

}


