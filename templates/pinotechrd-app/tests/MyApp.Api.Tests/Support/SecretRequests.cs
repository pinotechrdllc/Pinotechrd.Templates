using System.Net.Http.Headers;
using System.Text;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// Values that stand for a token, a session cookie, a custom header, a query token and a body field, all containing
/// <see cref="Marker"/> and unique to one instance, and the requests that carry every one of them.
/// </summary>
internal sealed class RequestSecrets
{
    /// <summary>Text that no legitimate log event contains; every secret holds it, so a partial leak is found too.</summary>
    public const string Marker = "s3cr3t";

    public const string CustomHeaderName = "X-Custom-Secret";

    private RequestSecrets(string id)
    {
        Token = $"{Marker}-token-{id}";
        Authorization = "Bearer " + Token;
        SessionCookie = $"{Marker}-cookie-{id}";
        Cookie = "session=" + SessionCookie;
        CustomHeader = $"{Marker}-header-{id}";
        QueryToken = $"{Marker}-query-{id}";
        BodyValue = $"{Marker}-body-{id}";
        EmailClaim = $"{Marker}-email-{id}@example.com";
        NameClaim = $"{Marker}-name-{id}";
        TokenClaim = $"{Marker}-claim-token-{id}";
        RoleClaim = $"{Marker}-role-{id}";
    }

    public string Token { get; }

    public string Authorization { get; }

    public string SessionCookie { get; }

    public string Cookie { get; }

    public string CustomHeader { get; }

    public string QueryToken { get; }

    public string BodyValue { get; }

    /// <summary>The value of the email claims of a principal that carries personal data (see <see cref="IdentityValues"/>).</summary>
    public string EmailClaim { get; }

    /// <summary>The value of the name claims, and the name the test scheme gives the principal.</summary>
    public string NameClaim { get; }

    /// <summary>The value of a claim that holds a token.</summary>
    public string TokenClaim { get; }

    /// <summary>The value of a claim that is no identifier (a role).</summary>
    public string RoleClaim { get; }

    /// <summary>The claim values of a principal that carries personal data: none of them may reach a log event or a span.</summary>
    public IReadOnlyList<string> IdentityValues => [EmailClaim, NameClaim, TokenClaim, RoleClaim];

    /// <summary>
    /// The claims header of the test scheme for a principal with valid identifiers (a user and a tenant) and the personal
    /// claims of <see cref="IdentityValues"/>, some of them under more than one claim type.
    /// </summary>
    public string PersonalClaims() =>
        TestAuthenticationHandler.ClaimsOf(
            ("sub", Guid.NewGuid().ToString("D")),
            ("tenant_id", TestAuthenticationHandler.DefaultTenant.ToString("D")),
            ("email", EmailClaim),
            (System.Security.Claims.ClaimTypes.Email, EmailClaim),
            ("name", NameClaim),
            ("given_name", NameClaim),
            ("access_token", TokenClaim),
            ("role", RoleClaim));

    /// <summary>Every value that must stay out of the logs and the spans, whole or in the part that identifies it.</summary>
    public IReadOnlyList<string> Values =>
        [Token, Authorization, SessionCookie, Cookie, CustomHeader, QueryToken, BodyValue, .. IdentityValues, Marker];

    public static RequestSecrets Create() => new(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A request with an <c>Authorization</c> header, a <c>Cookie</c> header, a custom header, a query string with a
    /// token and a JSON body, sent from <paramref name="remote"/>. <paramref name="queryPrefix"/> goes ahead of the
    /// token in the query string, for a route that reads one of its parameters.
    /// </summary>
    public HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string? remote = null,
        string? body = null,
        string queryPrefix = "",
        string? user = null,
        string? claims = null)
    {
        var request = new HttpRequestMessage(method, $"{path}?{queryPrefix}token={QueryToken}");
        request.Headers.TryAddWithoutValidation("Authorization", Authorization);
        request.Headers.TryAddWithoutValidation("Cookie", Cookie);
        request.Headers.TryAddWithoutValidation(CustomHeaderName, CustomHeader);
        AddIfSet(request, ApiHostFactory.RemoteAddressHeader, remote);
        AddIfSet(request, TestAuthenticationHandler.UserHeader, user);
        AddIfSet(request, TestAuthenticationHandler.ClaimsHeader, claims);
        request.Content = new StringContent(body ?? $$"""{"note":"{{BodyValue}}"}""", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        return request;
    }

    private static void AddIfSet(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
