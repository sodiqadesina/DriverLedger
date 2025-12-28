using DriverLedger.IntegrationTests.Fixtures;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DriverLedger.IntegrationTests;

public sealed class AuthProtectedEndpointTests : IClassFixture<SqlConnectionFixture>
{
    private readonly SqlConnectionFixture _sql;
    public AuthProtectedEndpointTests(SqlConnectionFixture sql) => _sql = sql;

    [Fact]
    public async Task Me_endpoint_requires_auth_and_allows_with_jwt()
    {
        await using var factory = new ApiFactory(_sql.ConnectionString);
        var client = factory.CreateClient();

        // 1) No auth => 401
        var unauth = await client.GetAsync("/auth/me");
        unauth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 2) Register => token
        var email = $"{Guid.NewGuid():N}@test.local";
        var register = await client.PostAsJsonAsync("/auth/register", new { Email = email, Password = "P@ssw0rd123!" });
        register.EnsureSuccessStatusCode();

        var body = await register.Content.ReadFromJsonAsync<TokenResponse>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrWhiteSpace();

        // 3) With auth => 200
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.Token);

        var authed = await client.GetAsync("/auth/me");
        authed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record TokenResponse([property: JsonPropertyName("token")] string Token);
}
