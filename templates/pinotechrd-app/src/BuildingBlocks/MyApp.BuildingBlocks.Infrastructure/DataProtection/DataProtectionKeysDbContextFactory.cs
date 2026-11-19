using Microsoft.EntityFrameworkCore.Design;
using MyApp.BuildingBlocks.Infrastructure.Migrations;

namespace MyApp.BuildingBlocks.Infrastructure.DataProtection;

/// <summary>
/// The design-time factory of <see cref="DataProtectionKeysDbContext"/> for <c>dotnet ef migrations add</c>: the run-time
/// provider, naming and history table, with no connection.
/// </summary>
public sealed class DataProtectionKeysDbContextFactory : IDesignTimeDbContextFactory<DataProtectionKeysDbContext>
{
    /// <inheritdoc />
    public DataProtectionKeysDbContext CreateDbContext(string[] args) =>
        new(SharedPersistenceRegistration.DesignTimeOptions<DataProtectionKeysDbContext>(DataProtectionKeysDbContext.Schema));
}
