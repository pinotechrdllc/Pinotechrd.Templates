using System.Runtime.CompilerServices;
using MyApp.BuildingBlocks.Infrastructure.Database;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// The shared <see cref="NpgsqlDataSource"/>, checked for what tenant persistence relies on: the connection-string
/// settings <see cref="ConnectionSafety"/> refuses (<c>Multiplexing</c>, <c>No Reset On Close</c>, a search path). A
/// singleton; tenant contexts registered with
/// <see cref="TenantPersistenceRegistration.AddTenantDbContext{TContext}"/> and the migrator take the data source from it,
/// and <c>UseTenantPersistence</c> (<see cref="TenantDbContextOptionsExtensions"/>)
/// runs the same check (once per data source).
/// <para>
/// Not detectable here, and so not to be done: a physical connection initializer on the data source builder that sets
/// <c>search_path</c>, and PgBouncer in transaction pooling mode in front of the server.
/// </para>
/// </summary>
internal sealed class TenantDataSource
{
    private static readonly ConditionalWeakTable<NpgsqlDataSource, object> Checked = [];

    public TenantDataSource(NpgsqlDataSource dataSource) => Value = Check(dataSource);

    /// <summary>Gets the checked data source.</summary>
    public NpgsqlDataSource Value { get; }

    /// <summary>Returns <paramref name="dataSource"/> when its settings are safe for tenant connections, and throws otherwise.</summary>
    /// <exception cref="InvalidOperationException">The data source sets a refused setting; the message names it.</exception>
    internal static NpgsqlDataSource Check(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (Checked.TryGetValue(dataSource, out _))
        {
            return dataSource;
        }

        // The connection string of the data source never holds the password (Persist Security Info is off), and the
        // message does not quote it anyway.
        if (ConnectionSafety.Refusal(new NpgsqlConnectionStringBuilder(dataSource.ConnectionString)) is { } refusal)
        {
            throw new InvalidOperationException($"The data source {refusal}");
        }

        Checked.TryAdd(dataSource, Checked);
        return dataSource;
    }
}
