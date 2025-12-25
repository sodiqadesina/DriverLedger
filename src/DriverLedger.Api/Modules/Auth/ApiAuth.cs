using DriverLedger.Api.Common.Auth;
using DriverLedger.Domain.Identity;
using DriverLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Linq;


namespace DriverLedger.Api.Modules.Auth
{
    public static class ApiAuth
    {
        public static void MapAuthEndpoints(WebApplication app)
        {
            var group = app.MapGroup("/auth").WithTags("Auth");

            group.MapPost("/register", async (RegisterRequest req, DriverLedgerDbContext db, IJwtTokenService tokens, CancellationToken ct) =>
            {
                var email = req.Email.Trim().ToLowerInvariant();

                var exists = await db.Users.AnyAsync(x => x.Email == email, ct);
                if (exists) return Results.Conflict("Email already exists.");

                var hash = BCrypt.Net.BCrypt.HashPassword(req.Password);
                var user = new User(email, hash);

                // Ensure role exists
                var driverRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Driver", ct);
                if (driverRole is null)
                {
                    driverRole = new Role("Driver");
                    db.Roles.Add(driverRole);
                    await db.SaveChangesAsync(ct); // ensure role has Id
                }

                db.Users.Add(user);
                await db.SaveChangesAsync(ct); // ensure user has Id

                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = driverRole.Id });
                await db.SaveChangesAsync(ct);


                var jwt = tokens.CreateToken(user, new[] { "Driver" });
                return Results.Ok(new { token = jwt });
            });

            group.MapPost("/login", async (LoginRequest req, DriverLedgerDbContext db, IJwtTokenService tokens, CancellationToken ct) =>
            {
                var email = req.Email.Trim().ToLowerInvariant();
                var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email, ct);
                if (user is null) return Results.Unauthorized();

                if (user.Status != "Active") return Results.Forbid();

                var ok = BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash);
                if (!ok) return Results.Unauthorized();

                var roles = await (from ur in db.UserRoles
                                   join r in db.Roles on ur.RoleId equals r.Id
                                   where ur.UserId == user.Id
                                   select r.Name).ToListAsync(ct);

                var jwt = tokens.CreateToken(user, roles);
                return Results.Ok(new { token = jwt });
            });

            group.MapGet("/me", (ClaimsPrincipal user) =>
            {
                // Minimal "who am I" response for MVP + tests
                var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
                var email = user.FindFirstValue(JwtRegisteredClaimNames.Email) ?? user.FindFirstValue(ClaimTypes.Email);
                var tenantId = user.FindFirstValue("tenantId");
                var roles = user.FindAll(ClaimTypes.Role).Select(r => r.Value).ToArray();

                return Results.Ok(new
                {
                    userId,
                    email,
                    tenantId,
                    roles
                });
            }).RequireAuthorization("RequireDriver");


        }

        public sealed record RegisterRequest(string Email, string Password);
        public sealed record LoginRequest(string Email, string Password);
    }
}
