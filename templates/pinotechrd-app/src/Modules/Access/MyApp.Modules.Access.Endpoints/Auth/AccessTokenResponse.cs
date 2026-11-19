using Microsoft.AspNetCore.Http;
using MyApp.Modules.Access.Application.Authentication;

namespace MyApp.Modules.Access.Endpoints.Auth;

/// <summary>
/// Response of a sign-in and of a refresh. The refresh token is not here: it travels only in the
/// <see cref="RefreshCookie"/>.
/// </summary>
/// <param name="AccessToken">The access token, sent as <c>Authorization: Bearer</c>.</param>
/// <param name="ExpiresAt">When the access token expires (UTC); refresh before then.</param>
public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Answers a session: the access token in the body, the refresh token in the cookie.</summary>
    internal static AccessTokenResponse Issue(AuthSession session, HttpResponse response)
    {
        RefreshCookie.Write(response, session.RefreshToken, session.RefreshTokenLifetime);
        return new AccessTokenResponse(session.AccessToken, session.AccessTokenExpiresAt);
    }
}
