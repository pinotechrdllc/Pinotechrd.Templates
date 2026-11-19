using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Access.Auth;

// POST /api/v1/access/auth/change-password: the signed-in user's own password.
[Collection(PostgresContainer.Name)]
public class ChangePasswordTests(PostgresFixture postgres)
{
    private const string NewPassword = "N3w-passw0rd-for-tests";

    private static async Task<JsonElement> ErrorsOfAsync(HttpResponseMessage response)
    {
        var body = await ProblemAssert.IsProblemAsync(response, 400, "validation_failed");
        return body.GetProperty("errors");
    }

    // Breaks when the change does not end every session of the user (both devices), keeps the old password, or leaves the
    // security stamp as it was; and when the answer is not a 204 that drops the cookie.
    [Fact]
    public async Task ChangingThePasswordAnswers204EndsEverySessionAndOnlyTheNewPasswordLogsIn()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (token, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        var (_, second) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        var (stampBefore, _, _) = await AccessDatabase.SecurityStateAsync(world.Services, world.TenantA, user);

        using var change = await client.ChangePasswordAsync(token, AccessDatabase.DefaultPassword, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        AuthScenarios.AssertCleared(change);
        var (stampAfter, _, _) = await AccessDatabase.SecurityStateAsync(world.Services, world.TenantA, user);
        Assert.NotEqual(stampBefore, stampAfter);
        foreach (var cookie in new[] { first, second })
        {
            using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);
            await AuthScenarios.AssertRefreshRefusedAsync(refresh);
        }

        using var old = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);
        await ProblemAssert.IsProblemAsync(old, 401, "invalid_credentials");
        await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email, NewPassword);
    }

    // Breaks when the change of one user ends the sessions of another user of the same tenant.
    [Fact]
    public async Task ChangingThePasswordDoesNotEndTheSessionsOfAnotherUser()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.EmailOf(1));
        await world.AddUserAsync(world.TenantA, AuthScenarios.EmailOf(2));
        using var client = world.Factory.AuthClient();
        var (token, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.EmailOf(1));
        var (_, bystander) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.EmailOf(2));

        using var change = await client.ChangePasswordAsync(token, AccessDatabase.DefaultPassword, NewPassword);
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, bystander);

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    // Breaks when a wrong current password is reported on the wrong field, accepted, or changes anything (session, password).
    [Fact]
    public async Task AWrongCurrentPasswordIs400ValidationFailedOnCurrentPasswordAndChangesNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (token, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var response = await client.ChangePasswordAsync(token, "Not-the-current-password", NewPassword);

        var errors = await ErrorsOfAsync(response);
        Assert.NotEqual(0, errors.GetProperty("currentPassword").GetArrayLength());
        Assert.False(errors.TryGetProperty("newPassword", out _));
        Assert.Empty(response.SetCookies());
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        using var oldLogin = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
    }

    // Breaks when a wrong current password is not counted as a failed access (a stolen access token could guess the password).
    [Fact]
    public async Task AWrongCurrentPasswordCountsAsAFailedAccessAndTenOfThemLockTheAccount()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (token, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var first = await client.ChangePasswordAsync(token, "Not-the-current-password", NewPassword);
        var (_, count, _) = await AccessDatabase.SecurityStateAsync(world.Services, world.TenantA, user);
        for (var i = 1; i < 10; i++)
        {
            using var more = await client.ChangePasswordAsync(token, "Not-the-current-password", NewPassword);
            await ErrorsOfAsync(more);
        }

        using var login = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);

        await ErrorsOfAsync(first);
        Assert.Equal(1, count);
        await ProblemAssert.IsProblemAsync(login, 401, "invalid_credentials");
    }

    // Breaks when a weak new password is accepted, or reported on the current password field, or ends the sessions anyway.
    [Theory]
    [InlineData("short1")]
    [InlineData("          ")]
    public async Task AWeakNewPasswordIs400ValidationFailedOnNewPasswordAndChangesNothing(string weak)
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (token, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var response = await client.ChangePasswordAsync(token, AccessDatabase.DefaultPassword, weak);

        var errors = await ErrorsOfAsync(response);
        Assert.NotEqual(0, errors.GetProperty("newPassword").GetArrayLength());
        Assert.False(errors.TryGetProperty("currentPassword", out _));
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
    }

    // Breaks when the endpoint answers an anonymous caller (it would change a password with no user) instead of 401.
    [Fact]
    public async Task ChangingThePasswordWithoutABearerTokenIs401()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        using var client = world.Factory.AuthClient();

        using var response = await client.PostAsync(AuthRequests.ChangePassword, body: new { currentPassword = "x", newPassword = NewPassword });

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // Breaks when missing fields reach the password check instead of being refused as invalid input.
    [Fact]
    public async Task ChangingThePasswordWithoutTheFieldsIs400ValidationFailed()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (token, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var response = await client.PostAsync(AuthRequests.ChangePassword, body: new { }, bearer: token);

        await ProblemAssert.IsProblemAsync(response, 400, "validation_failed");
    }
    // Breaks when concurrent wrong guesses lose counts (a read-modify-write of the failed count): 30 at once with one valid
    // token must lock the account after 10, and a locked account answers even the right current password as a wrong one.
    [Fact]
    public async Task ThirtyParallelWrongCurrentPasswordsAreCountedLockTheAccountAndTheRightOneIsThenRefused()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (token, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ =>
            Task.Run(() => client.ChangePasswordAsync(token, "Not-the-current-password", NewPassword), TestContext.Current.CancellationToken)));

        foreach (var response in responses)
        {
            var errors = await ErrorsOfAsync(response);
            Assert.NotEqual(0, errors.GetProperty("currentPassword").GetArrayLength());
            response.Dispose();
        }

        var (_, count, lockoutEnd) = await AccessDatabase.SecurityStateAsync(world.Services, world.TenantA, user);
        Assert.NotNull(lockoutEnd);
        Assert.True(lockoutEnd.Value > DateTimeOffset.UtcNow, "The account is not locked.");
        Assert.Equal(0, count);
        using var right = await client.ChangePasswordAsync(token, AccessDatabase.DefaultPassword, NewPassword);
        var rightErrors = await ErrorsOfAsync(right);
        Assert.NotEqual(0, rightErrors.GetProperty("currentPassword").GetArrayLength());
    }

    // Breaks when concurrent changes with the right current password do not exclude each other: exactly one wins; a request
    // that raced it is a 409 conflict problem and one that came after it a 400 on the current password, never a 500 (a
    // concurrency failure of the store leaking out). The hash is made slow here so the requests really overlap.
    [Fact]
    public async Task ParallelChangesWithTheRightCurrentPasswordGiveExactlyOne204AndNeverA500()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f.WithServices(services =>
            services.Configure<Microsoft.AspNetCore.Identity.PasswordHasherOptions>(options => options.IterationCount = 300_000)));
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (token, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => client.ChangePasswordAsync(token, AccessDatabase.DefaultPassword, NewPassword), TestContext.Current.CancellationToken)));

        var statuses = responses.Select(r => (int)r.StatusCode).Order().ToArray();
        Assert.Equal(1, statuses.Count(s => s == 204));
        Assert.True(statuses.All(s => s is 204 or 400 or 409), "Statuses: " + string.Join(", ", statuses));
        Assert.True(statuses.Contains(409), "No request raced the winner into a conflict: " + string.Join(", ", statuses));
        foreach (var response in responses.Where(r => (int)r.StatusCode == 409))
        {
            await ProblemAssert.IsProblemAsync(response, 409, "conflict");
        }

        foreach (var response in responses)
        {
            response.Dispose();
        }

        await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email, NewPassword);
    }

    // Breaks when the committed contract does not tell a client that the change of password can answer 409.
    [Fact]
    public void TheContractDeclaresConflictOnChangePassword()
    {
        var document = OpenApiContract.ReadCommitted();

        var operation = document["paths"]![AuthRequests.ChangePassword]!["post"]!.AsObject();

        Assert.NotNull(OpenApiContract.ResponseSchema(operation, "409", OpenApiContract.ProblemJson));
    }
}
