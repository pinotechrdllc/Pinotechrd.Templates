using Microsoft.AspNetCore.Identity;
using MyApp.Modules.Access.Infrastructure.Persistence;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The password rule of <see cref="AccessPasswordPolicy"/> as an Identity validator, registered by
/// <see cref="AccessPasswordPolicy.AddAccessPasswordPolicy"/> next to Identity's own: it counts Unicode scalar values, not
/// UTF-16 characters, so the API and the migrator's bootstrap command accept the same passwords. Its errors never quote
/// the password.
/// </summary>
/// <param name="describer">Identity's error messages.</param>
public sealed class AccessPasswordValidator(IdentityErrorDescriber describer) : IPasswordValidator<AccessUser>
{
    /// <summary>The code of a password longer than <see cref="AccessPasswordPolicy.MaximumLength"/>.</summary>
    public const string TooLongCode = "PasswordTooLong";

    /// <summary>The code of a password that is not valid UTF-16 (a lone surrogate).</summary>
    public const string InvalidTextCode = "PasswordInvalidText";

    /// <inheritdoc />
    public Task<IdentityResult> ValidateAsync(UserManager<AccessUser> manager, AccessUser user, string? password)
    {
        if (AccessPasswordPolicy.IsValidPassword(password))
        {
            return Task.FromResult(IdentityResult.Success);
        }

        var runes = password is null ? 0 : AccessPasswordPolicy.CountRunes(password);
        var error = runes switch
        {
            < 0 => new IdentityError { Code = InvalidTextCode, Description = "The password holds an invalid character (a lone surrogate)." },
            > AccessPasswordPolicy.MaximumLength => new IdentityError
            {
                Code = TooLongCode,
                Description = $"The password must be at most {AccessPasswordPolicy.MaximumLength} characters.",
            },
            _ => describer.PasswordTooShort(AccessPasswordPolicy.MinimumLength),
        };
        return Task.FromResult(IdentityResult.Failed(error));
    }
}
