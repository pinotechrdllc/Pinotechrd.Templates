using System.Text.RegularExpressions;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Security;
using Serilog.Events;

namespace MyApp.Api.Tests.Access.Auth;

// What the authentication endpoints write to the logs: the user id and tenant id of the event, never anything a caller sent
// or was given (email, password, slug, access token, refresh token).
[Collection(PostgresContainer.Name)]
public partial class AuthLogTests(PostgresFixture postgres)
{
    private const string NewPassword = "N3w-passw0rd-for-tests";

    private const string WrongPassword = "Wr0ng-password-for-test";

    private static readonly string AuthCategory = typeof(AccessAuthentication).FullName!;

    private const string LibraryEventsCategory = "Pinotechrd.Security.Events";

    private static string Message(LogEvent logEvent) => logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(Sign-in succeeded for user [0-9a-f-]{36} in tenant [0-9a-f-]{36}\.|Sign-in failed in tenant ([0-9a-f-]{36}|null)\.|Signed out of a session in tenant [0-9a-f-]{36}\.|Password changed for user [0-9a-f-]{36} in tenant [0-9a-f-]{36}; every session ended\.|A used refresh token was presented again in tenant ""?[0-9a-f-]{36}""? for user ""?[0-9a-f-]{36}""?; family ""?[0-9a-f-]{36}""? was revoked\.)$")]
    private static partial Regex AllowedMessage();

    // Breaks when a log line of the sign-in, refresh, logout, password change or token reuse carries the email, the slug, a
    // password or a token (the whole capture is searched, every property and the exception text), or logs a line not meant.
    [Fact]
    public async Task NoLogLineCarriesAnEmailASlugAPasswordOrAToken()
    {
        var sink = default(CollectingSink)!;
        using var world = await AccessWorld.StartAsync(postgres, f =>
        {
            sink = f.CaptureLogs();
            return f;
        });
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var secrets = new List<string> { AuthScenarios.Email, AccessDatabase.DefaultPassword, WrongPassword, NewPassword };

        using var failed = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, WrongPassword);
        using var unknown = await client.LoginAsync("nobody", AuthScenarios.Email, AccessDatabase.DefaultPassword);
        var (access, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        using var rotated = await client.RefreshAsync(AccessWorld.SlugA, first);
        var second = rotated.RefreshTokenOf();
        secrets.Add(await rotated.AccessTokenOfAsync());
        using var replay = await client.RefreshAsync(AccessWorld.SlugA, first);
        using var logout = await client.LogoutAsync(AccessWorld.SlugA, second);
        var (access2, third) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        using var change = await client.ChangePasswordAsync(access2, AccessDatabase.DefaultPassword, NewPassword);
        secrets.AddRange([access, access2, first, second, third]);

        var events = sink.Events;
        // The reuse of a refresh token is logged by the library (event 4204); its other events are covered by the whole-capture search below.
        var own = events
            .Where(e => e.IsFrom(AuthCategory) || (e.IsFrom(LibraryEventsCategory) && Message(e).StartsWith("A used refresh token", StringComparison.Ordinal)))
            .ToArray();
        Assert.Equal(System.Net.HttpStatusCode.NoContent, change.StatusCode);
        Assert.Contains(own, e => Message(e).StartsWith("Sign-in succeeded for user " + user.ToString("D"), StringComparison.Ordinal)
            && Message(e).Contains(world.TenantA.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(own, e => Message(e).StartsWith("Sign-in failed", StringComparison.Ordinal));
        Assert.Contains(own, e => Message(e).StartsWith("Signed out", StringComparison.Ordinal));
        Assert.Contains(own, e => Message(e).StartsWith("Password changed for user " + user.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(own, e => e.Level == LogEventLevel.Warning && Message(e).StartsWith("A used refresh token", StringComparison.Ordinal));
        foreach (var e in own)
        {
            Assert.True(AllowedMessage().IsMatch(Message(e)), $"Unexpected log line of {e.CategoryOf()}: {Message(e)}");
        }

        foreach (var e in events)
        {
            foreach (var text in e.TextsOf())
            {
                foreach (var secret in secrets)
                {
                    Assert.False(
                        text.Contains(secret, StringComparison.OrdinalIgnoreCase),
                        $"A log text of {e.CategoryOf()} carries a secret ('{secret[..Math.Min(secret.Length, 8)]}...'): {text}");
                }

                Assert.False(
                    text.Contains("__Host-refresh", StringComparison.OrdinalIgnoreCase),
                    $"A log text of {e.CategoryOf()} mentions the refresh cookie: {text}");
                Assert.False(
                    text is AccessWorld.SlugA or AccessWorld.SlugB or "nobody",
                    $"A log text of {e.CategoryOf()} is a tenant slug: {text}");
            }
        }
    }
}
