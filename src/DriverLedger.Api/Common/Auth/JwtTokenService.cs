using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DriverLedger.Domain.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DriverLedger.Api.Common.Auth
{
    public interface IJwtTokenService
    {
        string CreateToken(User user, IReadOnlyCollection<string> roles);
    }

    public sealed class JwtTokenService : IJwtTokenService
    {
        private readonly JwtOptions _opts;
        public JwtTokenService(IOptions<JwtOptions> options) => _opts = options.Value;

        public string CreateToken(User user, IReadOnlyCollection<string> roles)
        {
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),   // ✅ add this
                new(JwtRegisteredClaimNames.Email, user.Email),
                new("tenantId", user.Id.ToString())
            };

            foreach (var role in roles)
                claims.Add(new Claim(ClaimTypes.Role, role));

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.JwtKey))
            {
                KeyId = "driverledger-v1"
            };
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);


            var token = new JwtSecurityToken(
                issuer: _opts.JwtIssuer,
                audience: _opts.JwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(8),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
