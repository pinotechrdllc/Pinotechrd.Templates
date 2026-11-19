using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;

namespace MyApp.Migrator.Tests.Bootstrap;

// The password rules the API's user manager applies (AddAccessPasswordPolicy, the registration both the API module and the
// migrator use) agree with the bootstrap command's: 15 to 256 Unicode scalar values, valid UTF-16. The user manager is
// resolved from a container with Identity and the policy, the same pieces the API composes, over a store that holds nothing.
public class AccessPasswordPolicyTests
{
    private static async Task<bool> IsAcceptedAsync(string password)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityCore<AccessUser>();
        services.AddSingleton<IUserStore<AccessUser>, EmptyStore>();
        services.AddAccessPasswordPolicy();
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<UserManager<AccessUser>>();
        foreach (var validator in manager.PasswordValidators)
        {
            if (!(await validator.ValidateAsync(manager, new AccessUser(), password)).Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    // Breaks when the API counts UTF-16 characters (8 emoji are 16) or the migrator's scalar value count and the API's drift.
    [Fact]
    public async Task EightEmojiAreRefusedAndFifteenAreAccepted()
    {
        Assert.False(await IsAcceptedAsync(BootstrapRun.PasswordOfRunes(8, emoji: true)));
        Assert.False(await IsAcceptedAsync(BootstrapRun.PasswordOfRunes(14, emoji: true)));
        Assert.True(await IsAcceptedAsync(BootstrapRun.PasswordOfRunes(15, emoji: true)));
    }

    // Breaks when the API has no upper bound while the migrator refuses 257.
    [Fact]
    public async Task TheUpperBoundIs256Runes()
    {
        Assert.True(await IsAcceptedAsync(BootstrapRun.PasswordOfRunes(256, emoji: false)));
        Assert.False(await IsAcceptedAsync(BootstrapRun.PasswordOfRunes(257, emoji: false)));
        Assert.False(await IsAcceptedAsync(BootstrapRun.PasswordOfRunes(257, emoji: true)));
    }

    // Breaks when a password with a lone surrogate (invalid UTF-16) is accepted by the API though the migrator refuses it.
    [Fact]
    public async Task ALoneSurrogateIsRefused()
    {
        Assert.False(await IsAcceptedAsync(new string('a', 14) + "\uD800"));
        Assert.False(await IsAcceptedAsync(new string('a', 14) + "\uDC00" + "b"));
    }

    private sealed class EmptyStore : IUserStore<AccessUser>
    {
        public Task<string> GetUserIdAsync(AccessUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetUserNameAsync(AccessUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetUserNameAsync(AccessUser user, string? userName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetNormalizedUserNameAsync(AccessUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetNormalizedUserNameAsync(AccessUser user, string? normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentityResult> CreateAsync(AccessUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentityResult> UpdateAsync(AccessUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentityResult> DeleteAsync(AccessUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccessUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AccessUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
