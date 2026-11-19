using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyApp.Modules.Access.Infrastructure.Persistence;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The password and user rules of the Access module, shared by the API (<c>AccessModule</c>) and the migrator's
/// <c>bootstrap</c> command, so a password set by either is hashed and checked the same way (ADR 0013): Identity's V3
/// format (PBKDF2 with HMAC-SHA512) at <see cref="IterationCount"/> iterations; <see cref="MinimumLength"/> to
/// <see cref="MaximumLength"/> Unicode scalar values, not only white space, and no composition rule (length, not character
/// classes, makes a password strong), checked by <see cref="AccessPasswordValidator"/>; one account per email, the user
/// name being the email (<see cref="IsValidUserEmail"/>). Registered after the security library, it overrides the
/// library's rules (8 characters and a digit). The bootstrap command checks its settings at start with
/// <see cref="IsValidPassword"/> and <see cref="IsValidUserEmail"/>, the same functions.
/// </summary>
public static class AccessPasswordPolicy
{
    /// <summary>The PBKDF2 iterations of a new hash.</summary>
    public const int IterationCount = 210_000;

    /// <summary>The shortest password, in Unicode scalar values.</summary>
    public const int MinimumLength = 15;

    /// <summary>The longest password, in Unicode scalar values.</summary>
    public const int MaximumLength = 256;

    /// <summary>The longest email, the length of the users table's column.</summary>
    public const int MaxEmailLength = 256;

    /// <summary>The characters of a user name (Identity's default set, set explicitly so this policy owns it).</summary>
    public const string AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";

    /// <summary>Configures <see cref="PasswordHasherOptions"/>, the password and user parts of <see cref="IdentityOptions"/> and the <see cref="AccessPasswordValidator"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAccessPasswordPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<PasswordHasherOptions>(hasher =>
        {
            hasher.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            hasher.IterationCount = IterationCount;
        });
        services.Configure<IdentityOptions>(identity =>
        {
            identity.Password.RequiredLength = MinimumLength;
            identity.Password.RequiredUniqueChars = 1;
            identity.Password.RequireDigit = false;
            identity.Password.RequireLowercase = false;
            identity.Password.RequireUppercase = false;
            identity.Password.RequireNonAlphanumeric = false;
            identity.User.AllowedUserNameCharacters = AllowedUserNameCharacters;
            identity.User.RequireUniqueEmail = true;
        });
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPasswordValidator<AccessUser>, AccessPasswordValidator>());
        return services;
    }

    /// <summary>
    /// Whether <paramref name="password"/> is <see cref="MinimumLength"/> to <see cref="MaximumLength"/> Unicode scalar
    /// values of valid UTF-16 (no lone surrogate) and not only white space.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <returns><see langword="true"/> when the policy accepts it.</returns>
    public static bool IsValidPassword(string? password) =>
        password is not null
        && !string.IsNullOrWhiteSpace(password)
        && CountRunes(password) is >= MinimumLength and <= MaximumLength;

    /// <summary>
    /// Whether <paramref name="email"/> is accepted both as an email and as the user name Identity derives from it: at most
    /// <see cref="MaxEmailLength"/> characters, only <see cref="AllowedUserNameCharacters"/>, an address Identity's email
    /// rule accepts, and one plain address (no display name).
    /// </summary>
    /// <param name="email">The email.</param>
    /// <returns><see langword="true"/> when Identity would accept it as the user's email and user name.</returns>
    public static bool IsValidUserEmail(string? email) =>
        email is { Length: > 0 and <= MaxEmailLength }
        && email.All(c => AllowedUserNameCharacters.Contains(c, StringComparison.Ordinal))
        && new EmailAddressAttribute().IsValid(email)
        && MailAddress.TryCreate(email, out var address)
        && string.Equals(address.Address, email, StringComparison.Ordinal)
        && address.DisplayName.Length == 0;

    /// <summary>The Unicode scalar values of <paramref name="value"/>; -1 when it holds a lone surrogate.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The count, or -1.</returns>
    internal static int CountRunes(string value)
    {
        var count = 0;
        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out var consumed) != OperationStatus.Done)
            {
                return -1;
            }

            count++;
            rest = rest[consumed..];
        }

        return count;
    }
}
