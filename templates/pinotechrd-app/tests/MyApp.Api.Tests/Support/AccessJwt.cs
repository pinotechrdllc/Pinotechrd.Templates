using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The fake JWT settings of the Access module for the tests, and the tokens signed with them. The key is a fixed
/// constant of this file, long enough for the module (at least 32 bytes) and never a real secret: it protects nothing.
/// <para>
/// Every test host needs these settings or the Access module stops it at start. <see cref="ApiHostFactory"/> adds them to
/// its configuration; the process environment gets the same values once (<see cref="ProvideToHostsBuiltWithoutTheFactory"/>)
/// for the few hosts a test project builds straight from the application's entry point.
/// </para>
/// </summary>
internal static class AccessJwt
{
    /// <summary>The configuration key of the signing key.</summary>
    public const string SecretKey = "Access:Jwt:Secret";

    /// <summary>The configuration key of the issuer.</summary>
    public const string IssuerKey = "Access:Jwt:Issuer";

    /// <summary>The configuration key of the audience.</summary>
    public const string AudienceKey = "Access:Jwt:Audience";

    /// <summary>A fixed fake key, longer than the 32 bytes the module requires.</summary>
    public const string Secret = "fake-access-signing-key-for-tests-only-0123456";

    public const string Issuer = "https://issuer.test.invalid";

    public const string Audience = "access-tests";

    /// <summary>The settings a test host starts with.</summary>
    public static IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        [SecretKey] = Secret,
        [IssuerKey] = Issuer,
        [AudienceKey] = Audience,
    };

    // The environment is read by every host the process builds. Values set here are overridden by a host's own settings,
    // so a test that removes the secret does it with WithSetting (an empty value), not by touching the environment.
    [ModuleInitializer]
    internal static void ProvideToHostsBuiltWithoutTheFactory()
    {
        foreach (var (key, value) in Settings)
        {
            Environment.SetEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal), value);
        }
    }

    /// <summary>
    /// Signs a token with the fake key, HS256, as the library does, until <paramref name="lifetime"/> from now (one hour by default; negative for an expired token).
    /// </summary>
    public static string Token(IEnumerable<Claim> claims, TimeSpan? lifetime = null, string? secret = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? Secret)), SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromHours(1));
        var notBefore = (expires < now ? expires : now).AddMinutes(-1);
        var token = new JwtSecurityToken(Issuer, Audience, claims, notBefore, expires, credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>A token with the claims the API reads: <c>sub</c> (the user) and <c>tenant_id</c>.</summary>
    public static string TokenFor(Guid userId, Guid tenantId) =>
        Token([new Claim("sub", userId.ToString("D")), new Claim("tenant_id", tenantId.ToString("D"))]);
}
