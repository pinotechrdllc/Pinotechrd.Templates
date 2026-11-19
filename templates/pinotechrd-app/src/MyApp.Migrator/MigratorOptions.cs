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
}
