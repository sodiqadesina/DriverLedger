namespace DriverLedger.Api.Common.Auth
{
    public sealed class JwtOptions
    {
        public string JwtIssuer { get; set; } = default!;
        public string JwtAudience { get; set; } = default!;
        public string JwtKey { get; set; } = default!;
    }
}
