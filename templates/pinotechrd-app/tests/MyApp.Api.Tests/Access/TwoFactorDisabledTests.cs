using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Access.Auth;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Models;

namespace MyApp.Api.Tests.Access;

// Two-factor sign-in is not offered by the API: the template's attempt store refuses every attempt, so the library's second
// step fails closed whatever code is given, and the first step answers like wrong credentials.
public class TwoFactorDisabledTests
{
    private static readonly DateTimeOffset InFiveMinutes = DateTimeOffset.UtcNow.AddMinutes(5);

    // Breaks when the store counts an attempt (two-factor completion would then be possible with no shared attempt count).
    [Fact]
    public async Task RegisteringAnAttemptThrowsInvalidOperationException()
    {
        var store = new DisabledTwoFactorAttemptStore();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAttemptAsync("challenge", InFiveMinutes, TestContext.Current.CancellationToken));
    }

    // Breaks when exhausting a challenge throws (the library calls it, with no token, after a failed attempt it could not
    // record) or when it lets a later attempt be counted: the contract asks that every later attempt be refused.
    [Fact]
    public async Task ExhaustingAChallengeCompletesAndLaterAttemptsAreStillRefused()
    {
        var store = new DisabledTwoFactorAttemptStore();

        await store.ExhaustAsync("challenge", InFiveMinutes, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAttemptAsync("challenge", InFiveMinutes, TestContext.Current.CancellationToken));
    }

    [Collection(PostgresContainer.Name)]
    public class WithADatabase(PostgresFixture postgres)
    {
        private static readonly string Provider = TokenOptions.DefaultEmailProvider;

        // Turns two-factor on for the user, with a confirmed email so the Email provider is one of its valid providers.
        private static Task EnableTwoFactorAsync(AccessWorld world, Guid user) =>
            AccessDatabase.InTenantAsync(world.Services, world.TenantA, async scope =>
            {
                var db = scope.GetRequiredService<AccessDbContext>();
                var row = await db.Users.SingleAsync(u => u.Id == user, TestContext.Current.CancellationToken);
                row.TwoFactorEnabled = true;
                row.EmailConfirmed = true;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            });

        private static async Task<int> RefreshTokenCountAsync(AccessWorld world, Guid user)
        {
            var count = -1;
            await AccessDatabase.InTenantAsync(world.Services, world.TenantA, async scope =>
                count = await scope.GetRequiredService<AccessDbContext>().RefreshTokens
                    .CountAsync(t => t.UserId == user, TestContext.Current.CancellationToken));
            return count;
        }

        // Breaks when a two-factor account signs in with its password alone, when the refusal differs from a wrong
        // password's (it would tell that the account has two-factor on), or when a refresh token or cookie is issued.
        [Fact]
        public async Task ATwoFactorAccountIs401InvalidCredentialsWithNoTokenAndNoCookie()
        {
            using var world = await AccessWorld.StartAsync(postgres);
            var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
            await EnableTwoFactorAsync(world, user);
            using var client = world.Factory.AuthClient();

            using var response = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);

            var body = await ProblemAssert.IsProblemAsync(response, 401, "invalid_credentials");
            Assert.False(body.TryGetProperty("accessToken", out _));
            Assert.True(response.RefreshCookie() is null or { Value: "" }, "A refused sign-in set a refresh cookie.");
            Assert.Equal(0, await RefreshTokenCountAsync(world, user));
        }

        // Breaks when the library's second step can complete: with a valid code (the Email provider's TOTP) or any code, the
        // verification must throw or fail, issue no token, and store no refresh token.
        [Fact]
        public async Task TheLibrarysSecondStepNeverIssuesATokenWithAValidOrAnyCode()
        {
            using var world = await AccessWorld.StartAsync(postgres);
            var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
            await EnableTwoFactorAsync(world, user);
            var tenant = world.TenantA.ToString("D");

            foreach (var validCode in new[] { true, false })
            {
                await AccessDatabase.InTenantAsync(world.Services, world.TenantA, async scope =>
                {
                    // The sign-in manager reads the request: the scope gets one, as a request's scope would have it.
                    scope.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext { RequestServices = scope };
                    var auth = scope.GetRequiredService<IPinotechrdAuthManager<AccessUser>>();
                    var users = scope.GetRequiredService<UserManager<AccessUser>>();
                    var first = await auth.LoginAsync(
                        AuthScenarios.Email, AccessDatabase.DefaultPassword, tenant, TestContext.Current.CancellationToken);
                    Assert.True(first.RequiresTwoFactor, first.ErrorMessage);
                    Assert.Null(first.Token);
                    var code = validCode
                        ? await users.GenerateTwoFactorTokenAsync((await users.FindByIdAsync(user.ToString("D")))!, Provider)
                        : "000000";

                    AuthResult? result = null;
                    var thrown = await Record.ExceptionAsync(async () => result = await auth.VerifyTwoFactorCodeAsync(
                        first.TwoFactorToken!, Provider, code, tenant, TestContext.Current.CancellationToken));

                    if (thrown is null)
                    {
                        Assert.NotNull(result);
                        Assert.False(result.Succeeded, "The second step succeeded.");
                        Assert.Null(result.Token);
                    }
                    else
                    {
                        Assert.IsType<InvalidOperationException>(thrown);
                    }
                });
            }

            Assert.Equal(0, await RefreshTokenCountAsync(world, user));
        }
    }
}
