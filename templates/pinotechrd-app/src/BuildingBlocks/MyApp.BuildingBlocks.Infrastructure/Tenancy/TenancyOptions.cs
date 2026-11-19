namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Settings of tenant connections, in the <see cref="SectionName"/> section, read by
/// <see cref="TenantPersistenceRegistration.AddTenantPersistence"/>.
/// </summary>
public sealed class TenancyOptions
{
    /// <summary>The configuration section of these settings.</summary>
    public const string SectionName = "Tenancy";

    /// <summary>The configuration key of <see cref="AllowPrivilegedLogin"/>.</summary>
    public const string AllowPrivilegedLoginKey = SectionName + ":" + nameof(AllowPrivilegedLogin);

    /// <summary>
    /// Gets or sets whether a tenant connection may run on a login that is a superuser or has <c>BYPASSRLS</c>. Such a
    /// login reads every tenant's schema whatever role it assumes, so by default (<see langword="false"/>) the guard
    /// refuses it. Set it to <see langword="true"/> only for development or the single-login setup, where the one login
    /// is typically a superuser (ADR 0012). The tenant migrator's own connections are not checked.
    /// </summary>
    public bool AllowPrivilegedLogin { get; set; }
}
