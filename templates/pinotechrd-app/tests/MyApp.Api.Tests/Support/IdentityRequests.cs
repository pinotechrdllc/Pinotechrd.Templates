using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

namespace MyApp.Api.Tests.Support;

/// <summary>Helpers to send requests as a principal of the test scheme with chosen claims, and to read what the fixtures saw.</summary>
internal static class IdentityRequests
{
    public static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public static readonly Guid TenantB = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The claims header for a principal with exactly the given user and tenant, under the default claim types.</summary>
    public static string ClaimsFor(Guid userId, Guid tenantId) =>
        TestAuthenticationHandler.ClaimsOf(("sub", userId.ToString("D")), ("tenant_id", tenantId.ToString("D")));

    /// <summary>
    /// Sends a request. <paramref name="user"/> authenticates it as that user under the test scheme; <paramref name="claims"/>
    /// replaces the default <c>sub</c> and <c>tenant_id</c> (see <see cref="TestAuthenticationHandler.ClaimsHeader"/>).
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        this HttpClient client,
        HttpMethod method,
        string path,
        string? user = null,
        string? claims = null,
        IReadOnlyDictionary<string, string>? headers = null,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (user is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(TestAuthenticationHandler.UserHeader, user));
        }

        if (claims is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(TestAuthenticationHandler.ClaimsHeader, claims));
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            Assert.True(request.Headers.TryAddWithoutValidation(name, value));
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A GET as a principal of the test scheme.</summary>
    public static Task<HttpResponseMessage> GetAsAsync(this HttpClient client, string path, string user, string? claims = null) =>
        client.SendAsync(HttpMethod.Get, path, user, claims);

    /// <summary>Reads the answer of an identity fixture endpoint.</summary>
    public static async Task<WhoAmI> ReadWhoAmIAsync(this HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WhoAmI>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Reads a GUID answered as a JSON string.</summary>
    public static async Task<Guid> ReadGuidAsync(this HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>The type of the long-URI claim a platform handler produces for the subject.</summary>
    public const string NameIdentifierClaimType = ClaimTypes.NameIdentifier;

    /// <summary>The body of a JSON request that names a tenant, which the API must ignore.</summary>
    public static StringContent BodyNamingTenant(Guid tenantId) =>
        new(JsonSerializer.Serialize(new { tenantId, tenant_id = tenantId, tenant = tenantId }), System.Text.Encoding.UTF8, "application/json");
}
