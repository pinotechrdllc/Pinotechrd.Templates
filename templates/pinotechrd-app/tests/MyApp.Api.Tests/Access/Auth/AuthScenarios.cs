using System.Globalization;
using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Access.Auth;

/// <summary>The sign-ins the auth tests start from.</summary>
internal static class AuthScenarios
{
    public const string Email = "person@example.test";

    public static string EmailOf(int n) => $"person{n}@example.test";

    /// <summary>Signs in and returns the access token and the refresh cookie value; fails the test when the sign-in is not a 200.</summary>
    public static async Task<(string AccessToken, string RefreshToken)> SignInAsync(
        HttpClient client, string slug, string email, string password = AccessDatabase.DefaultPassword)
    {
        using var response = await client.LoginAsync(slug, email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.AccessTokenOfAsync(), response.RefreshTokenOf());
    }

    /// <summary>Asserts the answer is the refusal of a missing, spent or foreign refresh token and that it drops the cookie.</summary>
    public static async Task AssertRefreshRefusedAsync(HttpResponseMessage response)
    {
        await ProblemAssert.IsProblemAsync(response, 401, "invalid_refresh_token");
        AssertCleared(response);
    }

    /// <summary>Asserts the response drops the refresh cookie: empty value, a date in the past, the attributes it was set with.</summary>
    public static void AssertCleared(HttpResponseMessage response)
    {
        var cookie = response.RefreshCookie();
        Assert.NotNull(cookie);
        Assert.Equal(string.Empty, cookie.Value);
        Assert.True(cookie.Has("expires"), "A cleared cookie needs an Expires in the past.");
        Assert.True(DateTimeOffset.Parse(cookie["expires"]!, CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal("/", cookie["path"]);
        Assert.True(cookie.Has("secure"));
        Assert.True(cookie.Has("httponly"));
        Assert.Equal("strict", cookie["samesite"]?.ToLowerInvariant());
        Assert.False(cookie.Has("domain"));
    }
}
