using Microsoft.AspNetCore.Mvc.Testing;

namespace MyApp.Api.Tests.Support;

/// <summary>Helpers to send requests with the headers the security tests care about, and to read the answers.</summary>
internal static class SecurityRequests
{
    public const string Version = "/api/v1/version";

    public const string Throwing = ProblemFixtureEndpoint.Base + "/throw";

    public const string Unknown = "/api/v1/no-such-route";

    public static HttpClient ClientFor(this ApiHostFactory factory, string baseAddress) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress) });

    public static async Task<HttpResponseMessage> SendAsync(
        this HttpClient client,
        string path,
        string? remote = null,
        string? origin = null,
        string? forwardedFor = null,
        string? forwardedProto = null,
        string? user = null,
        string? roles = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        Add(request, TestAuthenticationHandler.UserHeader, user);
        Add(request, TestAuthenticationHandler.RolesHeader, roles);
        Add(request, ApiHostFactory.RemoteAddressHeader, remote);
        Add(request, "Origin", origin);
        Add(request, "X-Forwarded-For", forwardedFor);
        Add(request, "X-Forwarded-Proto", forwardedProto);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<HttpResponseMessage> PreflightAsync(
        this HttpClient client,
        string origin,
        string method,
        string? requestHeaders = null,
        string path = Version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        Add(request, "Origin", origin);
        Add(request, "Access-Control-Request-Method", method);
        Add(request, "Access-Control-Request-Headers", requestHeaders);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Sends requests until the client with this address has used its two permits, so the next one is rejected.</summary>
    public static async Task ExhaustAsync(this HttpClient client, string remote, string? forwardedFor = null, string? forwardedProto = null)
    {
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.SendAsync(Version, remote, forwardedFor: forwardedFor, forwardedProto: forwardedProto);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>All values of a header from the response or its content headers, or an empty list.</summary>
    public static string[] Values(this HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
        {
            return [.. values];
        }

        return [];
    }

    /// <summary>The value of a header (several values joined with a comma), or null when it is absent.</summary>
    public static string? Value(this HttpResponseMessage response, string name) =>
        response.Values(name) is { Length: > 0 } values ? string.Join(", ", values) : null;

    private static void Add(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(name, value), $"Header {name} was rejected by the client.");
        }
    }
}
