using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// A trivial authentication scheme: a request with the user header is that user, any other is anonymous (challenge 401,
/// forbid 403). The principal carries the claims the API reads: <c>sub</c> (a GUID derived from the user name, see
/// <see cref="UserIdOf"/>) and <c>tenant_id</c> (<see cref="DefaultTenant"/> unless the request says otherwise). The
/// request can replace both with <see cref="ClaimsHeader"/>, to send a missing, malformed, empty or repeated claim or a
/// claim of another type.
/// </summary>
internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>Request header with the name of the user to authenticate; without it the request stays anonymous.</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>
    /// Request header with the claims of the principal in place of the default <c>sub</c> and <c>tenant_id</c>, as
    /// <c>type=value</c> pairs separated by <c>|</c> (a type may repeat; a value may be empty). Only the first
    /// <c>=</c> of a pair separates type and value.
    /// </summary>
    public const string ClaimsHeader = "X-Test-Claims";

    /// <summary>The tenant of an authenticated test request that names none.</summary>
    public static readonly Guid DefaultTenant = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    /// <summary>Registers the scheme and authorization, the way an application adding authentication would.</summary>
    public static void Register(IServiceCollection services)
    {
        services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(SchemeName, _ => { });
        MakeDefault(services, SchemeName);
    }

    /// <summary>
    /// Makes <paramref name="scheme"/> the scheme that authenticates, challenges and forbids. The Access module makes the
    /// bearer scheme the default for each of the three, and those specific defaults win over <c>DefaultScheme</c>
    /// (<c>AddAuthentication(name)</c>): a host that swaps in a scheme of its own says so for each of them.
    /// </summary>
    public static void MakeDefault(IServiceCollection services, string scheme) =>
        services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultAuthenticateScheme = scheme;
            options.DefaultChallengeScheme = scheme;
            options.DefaultForbidScheme = scheme;
        });

    /// <summary>The <c>sub</c> the scheme issues for <paramref name="user"/>: always the same GUID for the same name.</summary>
    public static Guid UserIdOf(string user) => new(SHA256.HashData(Encoding.UTF8.GetBytes(user)).AsSpan(0, 16));

    /// <summary>The value of <see cref="ClaimsHeader"/> for the given claims.</summary>
    public static string ClaimsOf(params (string Type, string Value)[] claims) =>
        string.Join('|', claims.Select(c => $"{c.Type}={c.Value}"));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
        if (Request.Headers.TryGetValue(ClaimsHeader, out var custom))
        {
            foreach (var pair in custom.ToString().Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                claims.Add(new Claim(pair[..separator], pair[(separator + 1)..]));
            }
        }
        else
        {
            claims.Add(new Claim("sub", UserIdOf(user.ToString()).ToString("D")));
            claims.Add(new Claim("tenant_id", DefaultTenant.ToString("D")));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
