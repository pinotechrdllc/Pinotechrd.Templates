using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.Modules.Access.Application.Tenants;
using MyApp.Modules.Access.Infrastructure.Persistence;

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
        AddLayers(builder);
        AddModules(builder);

        services.AddDatabaseMigrator();
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
