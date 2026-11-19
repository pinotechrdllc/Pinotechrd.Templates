namespace MyApp.Migrator;

/// <summary>The migrator's settings, in the <see cref="MigratorComposition.SectionName"/> section.</summary>
public sealed class MigratorOptions
{
    /// <summary>The default of <see cref="MaxDegreeOfParallelism"/> (ADR 0001).</summary>
    public const int DefaultMaxDegreeOfParallelism = 4;

    /// <summary>
    /// Gets or sets how many tenants are migrated at once, at least 1. The tenant migrator also caps it by the pool:
    /// each tenant in progress holds two connections.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = DefaultMaxDegreeOfParallelism;

    /// <summary>
    /// Gets or sets the login the API connects with, when it is not the migrator's own: it becomes a member of every
    /// tenant's database role (ADR 0012). Empty when the API and the migrator share one login.
    /// </summary>
    public string? ApplicationRole { get; set; }
}
