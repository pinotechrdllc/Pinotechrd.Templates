using MyApp.Modules.Access.Application.Tenants;

namespace MyApp.Migrator;

/// <summary>
/// The settings of the <c>provision</c> command, in the <see cref="MigratorComposition.ProvisionSectionName"/> section,
/// validated at start (exit 2) with the rules of <see cref="ProvisionTenantRequest"/>. No message quotes a value.
/// </summary>
public sealed class ProvisionOptions
{
    /// <summary>Gets or sets the tenant's slug, its identifier (<see cref="ProvisionTenantRequest.SlugPattern"/>).</summary>
    public string? Slug { get; set; }

    /// <summary>Gets or sets the tenant's display name.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the id of the subscription the tenant belongs to. A string, so a value that is not a GUID fails the
    /// validation, whose message names the setting, instead of the binder, whose message could quote the value.
    /// </summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Whether <see cref="SubscriptionId"/> is a GUID other than the empty one.</summary>
    internal bool HasValidSubscriptionId => Guid.TryParse(SubscriptionId, out var id) && id != Guid.Empty;

    /// <summary>The request these settings describe, once they are valid.</summary>
    /// <returns>The request.</returns>
    internal ProvisionTenantRequest ToRequest() => new(Slug!, Name!, Guid.Parse(SubscriptionId!));
}
