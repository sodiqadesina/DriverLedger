

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DriverLedger.IntegrationTests.Helpers
{
    public static class TestAuth
    {
        public static async Task<string> RegisterAndLoginAsync(HttpClient client)
        {
            var email = $"{Guid.NewGuid():N}@test.local";
            var register = await client.PostAsJsonAsync("/auth/register", new
            {
                Email = email,
                Password = "P@ssw0rd123!"
            });

            register.EnsureSuccessStatusCode();

            var body = await register.Content.ReadFromJsonAsync<TokenResponse>();
            body.Should().NotBeNull();
            body!.Token.Should().NotBeNullOrWhiteSpace();

            return body.Token;
        }

        public static void ApplyBearer(HttpClient client, string token)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        private sealed record TokenResponse([property: JsonPropertyName("token")] string Token);
    }
}
