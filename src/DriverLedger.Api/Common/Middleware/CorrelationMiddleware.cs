using System.Diagnostics;

namespace DriverLedger.Api.Common.Middleware
{
    public sealed class CorrelationMiddleware : IMiddleware
    {
        private const string HeaderName = "x-correlation-id";

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v.ToString()
                : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

            context.Items[HeaderName] = correlationId;
            context.Response.Headers[HeaderName] = correlationId;

            using (context.RequestServices.GetRequiredService<ILogger<CorrelationMiddleware>>()
                .BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = correlationId,
                    ["TraceId"] = Activity.Current?.TraceId.ToString() ?? "",
                }))
            {
                await next(context);
            }
        }
    }
}
