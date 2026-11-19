using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyApp.Api.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Security;
using Pinotechrd.Security.Options;

namespace MyApp.Api.Tests.Access;

// The settings the Access module needs to start: the JWT section (secret, issuer, audience) and one claim type for the
// tenant, shared by the API and the security library. A bad value stops the host at start, with a message that names it
// and never quotes the secret.
public class AccessSettingsTests
{
    private const string ShortSecret = "fake-short-secret-0123456789abc";

    private static ApiHostFactory Host() => AccessHost.WithoutDatabase();

    // ---- Criterion 6: the signing secret ----

    // Breaks when a host starts with no signing key (every token would then be signed or checked with nothing).
    [Fact]
    public void AHostWithoutASigningSecretDoesNotStart()
    {
        using var factory = Host().WithSetting(AccessJwt.SecretKey, string.Empty);

        StartupFailure.AssertFailsNaming(factory, "Jwt.Secret");
    }

    // Breaks when the secret may be shorter than 32 bytes (UTF-8), counted in bytes and not in characters.
    [Fact]
    public void ASecretOfLessThan32BytesStopsTheHostAndIsNeverQuoted()
    {
        Assert.Equal(31, System.Text.Encoding.UTF8.GetByteCount(ShortSecret));
        using var factory = Host().WithSetting(AccessJwt.SecretKey, ShortSecret);

        var failure = StartupFailure.Capture(factory);

        Assert.Contains(failure.All, e => e.Message.Contains("Jwt.Secret", StringComparison.Ordinal) && e.Message.Contains("32", StringComparison.Ordinal));
        Assert.DoesNotContain(ShortSecret, failure.Everything, StringComparison.Ordinal);
    }

    // The boundary: exactly 32 bytes is accepted. Breaks when the floor is raised or counted in a way that refuses it.
    [Fact]
    public async Task ASecretOfExactly32BytesStartsTheHost()
    {
        var secret = new string('k', 32);
        using var factory = Host().WithSetting(AccessJwt.SecretKey, secret);
        using var client = factory.CreateClient();

        var token = AccessJwt.Token([new Claim("sub", Guid.NewGuid().ToString("D")), new Claim("tenant_id", Guid.NewGuid().ToString("D"))], secret: secret);
        using var response = await client.GetWithBearerAsync(AccessHost.WhoAmIRoute, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the settings are not read from the Access:Jwt section: the secret of the section signs what the host accepts,
    // and a token signed with the fake default key is then refused.
    [Fact]
    public async Task TheHostAcceptsTokensSignedWithTheSecretOfTheAccessJwtSection()
    {
        var secret = "another-secret-of-the-section-0123456789ab";
        using var factory = Host().WithSetting(AccessJwt.SecretKey, secret);
        using var client = factory.CreateClient();
        var claims = new[] { new Claim("sub", Guid.NewGuid().ToString("D")), new Claim("tenant_id", Guid.NewGuid().ToString("D")) };

        using var signedWithSection = await client.GetWithBearerAsync(AccessHost.WhoAmIRoute, AccessJwt.Token(claims, secret: secret));
        using var signedWithDefault = await client.GetWithBearerAsync(AccessHost.WhoAmIRoute, AccessJwt.Token(claims));

        Assert.Equal(HttpStatusCode.OK, signedWithSection.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signedWithDefault.StatusCode);
    }

    // Breaks when a host starts with no issuer or no audience (an unchecked issuer would accept any signer of the key).
    [Theory]
    [InlineData(AccessJwt.IssuerKey, "Jwt.Issuer")]
    [InlineData(AccessJwt.AudienceKey, "Jwt.Audience")]
    public void AHostWithoutAnIssuerOrAnAudienceDoesNotStart(string key, string named)
    {
        using var factory = Host().WithSetting(key, string.Empty);

        StartupFailure.AssertFailsNaming(factory, named);
    }

    // ---- Criterion 5: one claim type for the tenant ----

    // Breaks when the API reads the tenant under one claim type while the library writes and reads another: every token would be
    // a 401 or a 403 with nothing at start to say why. The message names both values.
    [Fact]
    public void ATenantClaimTypeDifferentFromTheLibrariesStopsTheHostAndNamesBothValues()
    {
        using var factory = Host().WithSetting("Identity:TenantIdClaimType", "tid");

        var failure = StartupFailure.Capture(factory);

        Assert.Contains(
            failure.All,
            e => e.Message.Contains("'tid'", StringComparison.Ordinal) && e.Message.Contains("'tenant_id'", StringComparison.Ordinal));
        Assert.Contains(failure.All, e => e is OptionsValidationException);
    }

    // Breaks when the start check refuses the default: the API and the library agree on tenant_id when nothing is set.
    [Fact]
    public async Task TheDefaultClaimTypesStartTheHost()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await client.GetWithBearerAsync(AccessHost.WhoAmIRoute, AccessJwt.TokenFor(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the claim type of the tenant is not taken from the Identity section for the library too: set before the host
    // is built, where the module reads it, the one value moves both the API and the library, and the host starts.
    [Fact]
    public async Task ATenantClaimTypeSetBeforeTheHostIsBuiltMovesTheApiAndTheLibraryTogether()
    {
        using var factory = Host().WithEarlySetting("Identity:TenantIdClaimType", "tid");
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var token = AccessJwt.Token([new Claim("sub", user.ToString("D")), new Claim("tid", tenant.ToString("D"))]);
        var defaultNamed = AccessJwt.TokenFor(user, tenant);

        using var named = await client.GetWithBearerAsync(AccessHost.WhoAmIRoute, token);
        using var unnamed = await client.GetWithBearerAsync(AccessHost.WhoAmIRoute, defaultNamed);

        var seen = await named.ReadWhoAmIAsync();
        Assert.Equal(tenant, seen.TenantId);
        Assert.Equal(HttpStatusCode.Unauthorized, unnamed.StatusCode);
    }

    // ---- Criterion 6, second half: the build-time contract generation ----

    // Breaks when the generation of the contract needs a secret, an issuer or a database: it runs the host with no
    // configuration at all. Runs the real command (dotnet build -p:UpdateContract=true) in a scratch copy of the output
    // location, with the Access settings removed from the environment, and compares the file it writes with the committed
    // contract: unchanged, byte for byte.
    [Fact]
    public async Task TheContractIsGeneratedWithNoConfigurationAndEqualsTheCommittedFile()
    {
        var root = SolutionLocator.SolutionRoot();
        var host = Path.Combine(root, "src", HostName(), HostName() + ".csproj");
        var scratch = Directory.CreateTempSubdirectory("access-contract-").FullName;
        try
        {
            var output = Path.Combine(scratch, "openapi.v1.json");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            foreach (var argument in new[] { "build", host, "-p:UpdateContract=true", $"-p:ContractFile={output}", "-nologo", "-v:q" })
            {
                start.ArgumentList.Add(argument);
            }

            // No configuration: nothing of Access, nothing of the database, from the process environment.
            foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("Access__", StringComparison.OrdinalIgnoreCase)
                                                                   || k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                start.Environment.Remove(name);
            }

            using var process = Process.Start(start)!;
            var text = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(process.ExitCode == 0, await text + await errors);
            Assert.True(File.Exists(output), "The generation wrote no file.");
            Assert.Equal(
                await File.ReadAllTextAsync(OpenApiContract.ContractPath, TestContext.Current.CancellationToken),
                await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    // ---- Refresh tokens: lifetimes and lockout ----

    private const string AbsoluteKey = "Access:RefreshTokens:AbsoluteLifetime";

    private const string SlidingKey = "Access:Jwt:RefreshTokenExpiryDays";

    // Breaks when the shipped defaults change: access tokens last 10 minutes, a refresh token slides 7 days, a session lasts at
    // most 30 days.
    [Fact]
    public void TheShippedSettingsGiveTenMinuteAccessTokensSevenDaySlidingAndThirtyDayAbsoluteRefreshTokens()
    {
        using var factory = Host();

        var security = factory.Services.GetRequiredService<IOptions<PinotechrdSecurityOptions>>().Value;
        var refresh = factory.Services.GetRequiredService<IOptions<RefreshTokenOptions>>().Value;

        Assert.Equal(10, security.Jwt.ExpiryMinutes);
        Assert.Equal(7, security.Jwt.RefreshTokenExpiryDays);
        Assert.Equal(TimeSpan.FromDays(30), refresh.AbsoluteLifetime);
    }

    // Breaks when the section is not read from Access:RefreshTokens.
    [Fact]
    public void TheAbsoluteLifetimeIsReadFromTheAccessRefreshTokensSection()
    {
        using var factory = Host().WithSetting(AbsoluteKey, "12.00:00:00");

        Assert.Equal(TimeSpan.FromDays(12), factory.Services.GetRequiredService<IOptions<RefreshTokenOptions>>().Value.AbsoluteLifetime);
    }

    // Breaks when a session may end before its first token would: an absolute lifetime shorter than the sliding one stops the
    // host and the message names the absolute setting.
    [Fact]
    public void AnAbsoluteLifetimeShorterThanTheSlidingOneStopsTheHost()
    {
        using var factory = Host().WithSetting(AbsoluteKey, "3.00:00:00");

        var failure = StartupFailure.Capture(factory);

        StartupFailure.AssertOptionsInvalid(failure, "AbsoluteLifetime");
    }

    // Breaks when the comparison is off by one at the boundary: equal is accepted, one second less is refused.
    [Fact]
    public void AnAbsoluteLifetimeEqualToTheSlidingOneStartsTheHostAndOneSecondLessDoesNot()
    {
        using (var equal = Host().WithSetting(AbsoluteKey, "7.00:00:00"))
        {
            Assert.Equal(TimeSpan.FromDays(7), equal.Services.GetRequiredService<IOptions<RefreshTokenOptions>>().Value.AbsoluteLifetime);
        }

        using var shorter = Host().WithSetting(AbsoluteKey, "6.23:59:59");
        StartupFailure.AssertOptionsInvalid(StartupFailure.Capture(shorter), "AbsoluteLifetime");
    }

    // Breaks when the sliding lifetime is not checked to be positive (zero or negative days).
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void ASlidingLifetimeOfZeroOrLessStopsTheHost(string days)
    {
        using var factory = Host().WithSetting(SlidingKey, days);

        var failure = StartupFailure.Capture(factory);

        StartupFailure.AssertOptionsInvalid(failure, "RefreshTokenExpiryDays");
    }

    // Breaks when the lockout rules of the module change: 10 failed sign-ins lock an account, new ones included, for 15 minutes.
    [Fact]
    public void TenFailedSignInsLockAnAccountForFifteenMinutesNewUsersIncluded()
    {
        using var factory = Host();

        var lockout = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout;

        Assert.Equal(10, AccessPasswordPolicy.MaxFailedAccessAttempts);
        Assert.Equal(10, lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), lockout.DefaultLockoutTimeSpan);
        Assert.True(lockout.AllowedForNewUsers);
    }

    private static string HostName() => Architecture.ReferenceRules.NameOf(Architecture.ReferenceRules.Api);
}
