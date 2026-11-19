using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using MyApp.Modules.Access.Application.Tenants;
using MyApp.Modules.Access.Infrastructure.Security;

namespace MyApp.Migrator;

/// <summary>
/// The settings of the <c>bootstrap</c> command, in the <see cref="MigratorComposition.BootstrapSectionName"/> section,
/// validated at start (exit 2): the operator tenant's slug and name (the rules of <see cref="ProvisionTenantRequest"/>),
/// its first administrator's email and password. The password is read only from the environment
/// (<c>Bootstrap__AdminPassword</c>) or user secrets (<see cref="PasswordSourceFailure"/>); when given it must be
/// <see cref="AccessPasswordPolicy.MinimumLength"/> to <see cref="AccessPasswordPolicy.MaximumLength"/> Unicode scalar
/// values, and it is required only when the administrator must be created. No message quotes a value, and
/// <see cref="ToString"/> is the type's name.
/// </summary>
public sealed class BootstrapOptions
{
    /// <summary>The key of the administrator's password.</summary>
    public const string AdminPasswordKey = MigratorComposition.BootstrapSectionName + ":" + nameof(AdminPassword);

    /// <summary>The longest email, the length of the users table's column.</summary>
    public const int MaxEmailLength = AccessPasswordPolicy.MaxEmailLength;

    /// <summary>Gets or sets the operator tenant's slug.</summary>
    public string? Slug { get; set; }

    /// <summary>Gets or sets the operator tenant's display name, also the customer name of its subscription.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the first administrator's email, also its user name.</summary>
    public string? AdminEmail { get; set; }

    /// <summary>Gets or sets the first administrator's password: from the environment or user secrets only.</summary>
    public string? AdminPassword { get; set; }

    /// <summary>
    /// Why the administrator's password is refused for its source, or <see langword="null"/> when it comes only from
    /// allowed sources (or is not set). Refused: the command line, and any file whose name starts with
    /// <c>appsettings</c>, which are committed or visible to other processes. The message names the setting and the
    /// source, never the value.
    /// </summary>
    /// <param name="configuration">The configuration of the host, with every source added.</param>
    /// <returns>The failure, or <see langword="null"/>.</returns>
    public static string? PasswordSourceFailure(IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var provider in Flatten(configuration.Providers))
        {
            if (!provider.TryGet(AdminPasswordKey, out _))
            {
                continue;
            }

            if (provider is CommandLineConfigurationProvider)
            {
                return $"{AdminPasswordKey} must not be given on the command line; set it in the environment (Bootstrap__AdminPassword) or in user secrets.";
            }

            if (provider is FileConfigurationProvider { Source.Path: { } path }
                && Path.GetFileName(path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                return $"{AdminPasswordKey} must not be in an appsettings file; set it in the environment (Bootstrap__AdminPassword) or in user secrets.";
            }
        }

        return null;
    }

    /// <inheritdoc />
    public override string ToString() => nameof(BootstrapOptions);

    /// <summary>Whether <see cref="AdminEmail"/> is an email the Access module accepts as a user's email and user name (<see cref="AccessPasswordPolicy.IsValidUserEmail"/>).</summary>
    internal bool HasValidEmail => AccessPasswordPolicy.IsValidUserEmail(AdminEmail);

    /// <summary>Whether <see cref="AdminPassword"/> is absent or accepted by the Access password policy (<see cref="AccessPasswordPolicy.IsValidPassword"/>).</summary>
    internal bool HasValidPassword => AdminPassword is null || AccessPasswordPolicy.IsValidPassword(AdminPassword);

    // The providers, with those of a chained configuration listed in its place.
    private static IEnumerable<IConfigurationProvider> Flatten(IEnumerable<IConfigurationProvider> providers)
    {
        foreach (var provider in providers)
        {
            if (provider is ChainedConfigurationProvider { Configuration: IConfigurationRoot chained })
            {
                foreach (var inner in Flatten(chained.Providers))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return provider;
            }
        }
    }
}
