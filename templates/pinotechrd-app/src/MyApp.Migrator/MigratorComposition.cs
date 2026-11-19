using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.Modules.Access.Application.Security;
using MyApp.Modules.Access.Application.Tenants;
using MyApp.Modules.Access.Infrastructure.Persistence;
using MyApp.Modules.Access.Infrastructure.Security;
using MyApp.Modules.Access.Infrastructure.Seeding;

namespace MyApp.Migrator;

/// <summary>
/// What the migrator migrates: the shared data source, the Access module's catalog and tenant contexts and, through the
/// hooks, the persistence of the other parts of the application. Shared by both architectures, like the API's
/// <c>BusinessComposition</c>: the parts that differ are partial methods declared here and implemented, when the
/// architecture has something to add, in a file of its own. A partial method with no implementation is not dead code:
/// the compiler removes the call.
/// </summary>
public static partial class MigratorComposition
{
    /// <summary>The configuration section of the migrator's settings.</summary>
    public const string SectionName = "Migrator";

    /// <summary>The configuration section of the <c>provision</c> command's settings.</summary>
    public const string ProvisionSectionName = "Provision";

    /// <summary>The configuration section of the <c>bootstrap</c> command's settings.</summary>
    public const string BootstrapSectionName = "Bootstrap";

    /// <summary>
    /// Registers the database (validated at start), the migrator's settings (validated at start), the Access module's
    /// persistence, the layers or modules of the application, and the <see cref="IDatabaseMigrator"/>.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHostApplicationBuilder AddMigrations(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        services.AddDatabase();
        services.AddOptions<MigratorOptions>()
            .Bind(builder.Configuration.GetSection(SectionName))
            .Validate(o => o.MaxDegreeOfParallelism >= 1, $"{SectionName}:{nameof(MigratorOptions.MaxDegreeOfParallelism)} must be at least 1.")
            .ValidateOnStart();

        // The catalog first: shared contexts are migrated in registration order, before any tenant.
        services.AddAccessCatalog();
        services.AddAccessPersistence();
        // What migrate seeds (ADR 0013): the plans in the catalog, each tenant's modules and permissions, from the
        // permissions the modules declare, the same registrations the API makes.
        services.AddAccessPermissions();
        services.AddAccessSeeding();
        AddLayers(builder);
        AddModules(builder);

        // The API's login, when it is not the migrator's: it becomes a member of every tenant's role (ADR 0012).
        var section = builder.Configuration.GetSection(SectionName);
        services.AddDatabaseMigrator(o => o.ApplicationRole =
            section[nameof(MigratorOptions.ApplicationRole)] is { Length: > 0 } role ? role : null);
        return builder;
    }

    /// <summary>
    /// Registers the settings of the <c>provision</c> command, validated at start with the rules of
    /// <see cref="ProvisionTenantRequest"/>; the provisioner itself comes with the catalog. Called only for that command,
    /// so the plain migration needs no such settings.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHostApplicationBuilder AddProvisioning(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The messages name the setting and the rule, never the value.
        builder.Services.AddOptions<ProvisionOptions>()
            .Bind(builder.Configuration.GetSection(ProvisionSectionName))
            .Validate(
                o => ProvisionTenantRequest.IsValidSlug(o.Slug),
                $"{ProvisionSectionName}:{nameof(ProvisionOptions.Slug)} must be 1 to 63 lower-case letters, digits or single hyphens, starting and ending with a letter or digit.")
            .Validate(
                o => ProvisionTenantRequest.IsValidName(o.Name),
                $"{ProvisionSectionName}:{nameof(ProvisionOptions.Name)} must be 1 to {ProvisionTenantRequest.MaxNameLength} characters, not blank, without control, invisible format or separator characters.")
            .Validate(
                o => o.HasValidSubscriptionId,
                $"{ProvisionSectionName}:{nameof(ProvisionOptions.SubscriptionId)} must be a GUID other than the empty one.")
            .ValidateOnStart();
        return builder;
    }

    /// <summary>
    /// Registers the settings of the <c>bootstrap</c> command (<see cref="BootstrapOptions"/>, validated at start), the
    /// Access module's bootstrap (the target, Identity's user manager with the Access password policy and the
    /// administrator's seeder) and silences Identity's log, whose messages can quote a user name. Called only for that
    /// command, after <see cref="AddMigrations"/>.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHostApplicationBuilder AddBootstrap(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The messages name the setting and the rule, never the value.
        builder.Services.AddOptions<BootstrapOptions>()
            .Bind(builder.Configuration.GetSection(BootstrapSectionName))
            .Validate(
                o => ProvisionTenantRequest.IsValidSlug(o.Slug),
                $"{BootstrapSectionName}:{nameof(BootstrapOptions.Slug)} must be 1 to 63 lower-case letters, digits or single hyphens, starting and ending with a letter or digit.")
            .Validate(
                o => ProvisionTenantRequest.IsValidName(o.Name),
                $"{BootstrapSectionName}:{nameof(BootstrapOptions.Name)} must be 1 to {ProvisionTenantRequest.MaxNameLength} characters, not blank, without control, invisible format or separator characters.")
            .Validate(
                o => o.HasValidEmail,
                $"{BootstrapSectionName}:{nameof(BootstrapOptions.AdminEmail)} must be one email address of at most {BootstrapOptions.MaxEmailLength} characters, only letters a-z or A-Z, digits and - . _ @ +.")
            .Validate(
                o => o.HasValidPassword,
                $"{BootstrapOptions.AdminPasswordKey} must be {AccessPasswordPolicy.MinimumLength} to {AccessPasswordPolicy.MaximumLength} characters (Unicode scalar values), not only white space.")
            .ValidateOnStart();
        builder.Services.AddAccessBootstrap();
        builder.Logging.AddFilter("Microsoft.AspNetCore.Identity", LogLevel.None);
        return builder;
    }

    /// <summary>
    /// Hook for the persistence of a clean application's infrastructure layer: implemented in a file only that
    /// architecture keeps, once the layer has a context. Without it the call is removed at compile time.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    static partial void AddLayers(IHostApplicationBuilder builder);

    /// <summary>
    /// Hook for the persistence of the modules: implemented in a file only a modular application keeps, once a module
    /// has a context. Without it the call is removed at compile time.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    static partial void AddModules(IHostApplicationBuilder builder);
}
