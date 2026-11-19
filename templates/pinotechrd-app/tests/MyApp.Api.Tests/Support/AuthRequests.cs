using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MyApp.Api.Tests.Support;

/// <summary>A cookie of a Set-Cookie header: its name, value and attributes (names in lower case, no value for a flag).</summary>
internal sealed record SetCookie(string Name, string Value, IReadOnlyDictionary<string, string?> Attributes)
{
    public bool Has(string attribute) => Attributes.ContainsKey(attribute);

    public string? this[string attribute] => Attributes.TryGetValue(attribute, out var value) ? value : null;
}

/// <summary>Requests to the authentication endpoints of the Access module, and readers of their answers.</summary>
internal static partial class AuthRequests
{
    public const string Base = "/api/v1/access/auth";

    public const string Login = Base + "/login";

    public const string Refresh = Base + "/refresh";

    public const string Logout = Base + "/logout";

    public const string ChangePassword = Base + "/change-password";

    public const string CookieName = "__Host-refresh";

    /// <summary>A client of https://localhost (so the Secure cookie flows and an Origin of the host is its own), that keeps no cookies: a test sends the ones it means to.</summary>
    public static HttpClient AuthClient(this ApiHostFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

    /// <summary>
    /// A POST. <paramref name="tenant"/> is the X-Tenant slug, <paramref name="cookie"/> the refresh cookie value and
    /// <paramref name="bearer"/> the access token; each header of <paramref name="headers"/> is sent with all its values.
    /// </summary>
    public static async Task<HttpResponseMessage> PostAsync(
        this HttpClient client,
        string path,
        string? tenant = null,
        object? body = null,
        string? cookie = null,
        string? bearer = null,
        params (string Name, string[] Values)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (tenant is not null)
        {
            request.Headers.Add("X-Tenant", tenant);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={cookie}");
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        foreach (var (name, values) in headers)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(name, values), $"Header {name} was rejected by the client.");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static Task<HttpResponseMessage> LoginAsync(
        this HttpClient client, string? tenant, string email, string password, params (string Name, string[] Values)[] headers) =>
        client.PostAsync(Login, tenant, new { email, password }, headers: headers);

    public static Task<HttpResponseMessage> RefreshAsync(
        this HttpClient client, string? tenant, string? cookie, params (string Name, string[] Values)[] headers) =>
        client.PostAsync(Refresh, tenant, cookie: cookie, headers: headers);

    public static Task<HttpResponseMessage> LogoutAsync(
        this HttpClient client, string? tenant, string? cookie, params (string Name, string[] Values)[] headers) =>
        client.PostAsync(Logout, tenant, cookie: cookie, headers: headers);

    public static Task<HttpResponseMessage> ChangePasswordAsync(
        this HttpClient client, string accessToken, string currentPassword, string newPassword) =>
        client.PostAsync(ChangePassword, body: new { currentPassword, newPassword }, bearer: accessToken);

    /// <summary>Every Set-Cookie header of the response, parsed.</summary>
    public static IReadOnlyList<SetCookie> SetCookies(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values.Select(Parse)] : [];

    /// <summary>The refresh cookie the response sets, if it does.</summary>
    public static SetCookie? RefreshCookie(this HttpResponseMessage response) =>
        response.SetCookies().SingleOrDefault(c => c.Name == CookieName);

    /// <summary>The value of the refresh cookie the response sets (not empty), or a failure of the test.</summary>
    public static string RefreshTokenOf(this HttpResponseMessage response)
    {
        var cookie = response.RefreshCookie();
        Assert.NotNull(cookie);
        Assert.False(string.IsNullOrEmpty(cookie.Value), "The refresh cookie is empty.");
        return cookie.Value;
    }

    /// <summary>The access token of a 200 answer of the sign-in or refresh.</summary>
    public static async Task<string> AccessTokenOfAsync(this HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    /// <summary>The value of a claim of a token (not validated).</summary>
    public static string? ClaimOf(string token, string type) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.FirstOrDefault(c => c.Type == type)?.Value;

    /// <summary>
    /// Everything a caller can tell from an answer: status, headers (without the date) and body, the trace id of the body
    /// replaced. Two answers with the same print cannot be told apart.
    /// </summary>
    public static async Task<string> PrintAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body = TraceId().Replace(body, "\"traceId\":\"-\"");
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key.ToLowerInvariant()}: {string.Join(", ", h.Value)}")
            .Order(StringComparer.Ordinal);
        return $"{(int)response.StatusCode}\n{string.Join('\n', headers)}\n{body}";
    }

    private static SetCookie Parse(string header)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var (name, value) = Split(parts[0]);
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var part in parts.Skip(1).Where(p => p.Length > 0))
        {
            var (key, attribute) = Split(part);
            attributes[key.ToLowerInvariant()] = part.Contains('=', StringComparison.Ordinal) ? attribute : null;
        }

        return new SetCookie(name, value, attributes);
    }

    private static (string Name, string Value) Split(string pair)
    {
        var at = pair.IndexOf('=', StringComparison.Ordinal);
        return at < 0 ? (pair, string.Empty) : (pair[..at], pair[(at + 1)..]);
    }

    [GeneratedRegex("\"traceId\"\\s*:\\s*\"[^\"]*\"")]
    private static partial Regex TraceId();
}
