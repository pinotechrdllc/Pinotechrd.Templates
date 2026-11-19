using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// The authentication scheme the API uses until a real one is registered: it never authenticates anyone, and it
/// challenges with 401 and <c>WWW-Authenticate: Bearer</c>, which the pipeline turns into an <c>unauthorized</c>
/// problem document. It is the default scheme only while no other default is configured, so registering a real scheme
/// as the default (<c>services.AddAuthentication("Bearer").AddJwtBearer(...)</c>, or setting
/// <c>DefaultAuthenticateScheme</c> and <c>DefaultChallengeScheme</c>) replaces it without any other change. With it,
/// nobody can log in: every endpoint that is not anonymous answers 401. Its name is
/// <see cref="ApiAuthorizationRegistration.PlaceholderScheme"/>.
/// </summary>
internal sealed class PlaceholderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerChallenge = "Bearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = BearerChallenge;
        return Task.CompletedTask;
    }
}
