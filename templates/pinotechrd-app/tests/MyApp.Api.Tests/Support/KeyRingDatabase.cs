using System.Diagnostics;
using System.Globalization;
using MyApp.Api.Tests.Database;
using Npgsql;

namespace MyApp.Api.Tests.Support;

// A database as production has it: a migrator login that owns it and a separate application login. The migrator is run as
// its own process (a second composition root, not a reference of the host's tests).
internal sealed class KeyRingDatabase : IAsyncDisposable
{
    private const string KeysTable = "data_protection.keys";

    private readonly PostgresFixture _postgres;
    private readonly string _name;
    private readonly string _migrator;
    private readonly string _application;
    private readonly string _asMigrator;
    private readonly string _asApplication;

    private KeyRingDatabase(PostgresFixture postgres, string name, string migrator, string application, string asMigrator, string asApplication)
    {
        _postgres = postgres;
        _name = name;
        _migrator = migrator;
        _application = application;
        _asMigrator = asMigrator;
        _asApplication = asApplication;
    }

    public static async Task<KeyRingDatabase> CreateAsync(PostgresFixture postgres)
    {
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var (migrator, application, secret) = ("m_" + suffix, "a_" + suffix, "Pw-" + Guid.NewGuid().ToString("N"));
        var superuser = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var name = superuser.Database!;
        var database = new KeyRingDatabase(
            postgres,
            name,
            migrator,
            application,
            new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Username = migrator, Password = secret, Pooling = false }.ConnectionString,
            new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Username = application, Password = secret, Pooling = false }.ConnectionString);
        await AdminAsync(postgres, $"CREATE ROLE {migrator} LOGIN CREATEROLE PASSWORD '{secret}'; CREATE ROLE {application} LOGIN PASSWORD '{secret}'");
        await AdminAsync(postgres, $"ALTER DATABASE \"{name}\" OWNER TO {migrator}");
        var migrated = await RunMigratorAsync([$"--ConnectionStrings:Database={database._asMigrator}", $"--Migrator:ApplicationRole={application}"]);
        Assert.True(migrated.Exit == 0, migrated.Output);
        return database;
    }

    /// <summary>A host on the application login with the real key ring and the given certificates.</summary>
    public ApiHostFactory Host(TestCertificateFiles certificate, TestCertificateFiles? previous = null)
    {
        var factory = new ApiHostFactory().WithDatabase(_asApplication).WithRealKeyRing().WithDataProtectionCertificate(certificate);
        return previous is null ? factory : factory.WithPreviousDataProtectionCertificate(0, previous);
    }

    public async Task<long> KeyCountAsync() =>
        long.Parse((await ColumnAsync($"SELECT count(*) FROM {KeysTable}"))[0], CultureInfo.InvariantCulture);

    public Task<List<string>> KeyXmlAsync() => ColumnAsync($"SELECT xml FROM {KeysTable}");

    /// <summary>A host on the application login with the real key ring and no certificate (Development only).</summary>
    public ApiHostFactory HostWithoutCertificate() =>
        new ApiHostFactory().WithDatabase(_asApplication).WithRealKeyRing().WithoutDataProtectionCertificate();

    /// <summary>The XML of the first row of the ring, which the key manager wrote.</summary>
    public async Task<string> FirstKeyXmlAsync() => (await ColumnAsync($"SELECT xml FROM {KeysTable} ORDER BY id LIMIT 1"))[0];

    /// <summary>The friendly name of the first row of the ring.</summary>
    public async Task<string> FirstFriendlyNameAsync() => (await ColumnAsync($"SELECT friendly_name FROM {KeysTable} ORDER BY id LIMIT 1"))[0];

    /// <summary>The number of rows with a signature and a signer.</summary>
    public async Task<long> SignedRowCountAsync() =>
        long.Parse((await ColumnAsync($"SELECT count(*) FROM {KeysTable} WHERE signature IS NOT NULL AND signer IS NOT NULL"))[0], CultureInfo.InvariantCulture);

    /// <summary>The signature column of the first row, or null.</summary>
    public async Task<byte[]?> FirstSignatureAsync()
    {
        await using var connection = new NpgsqlConnection(_asMigrator);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT signature FROM {KeysTable} ORDER BY id LIMIT 1", connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as byte[];
    }

    /// <summary>The signer column of the first row, or null.</summary>
    public async Task<string?> FirstSignerAsync()
    {
        await using var connection = new NpgsqlConnection(_asMigrator);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT signer FROM {KeysTable} ORDER BY id LIMIT 1", connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string;
    }

    /// <summary>
    /// Inserts a row as the application login, which is all an attacker who stole that login can do, and returns its id. The
    /// row is written before the hosts under test start, so their key ring (cached once loaded) includes it.
    /// </summary>
    public async Task<int> InsertAsApplicationAsync(string? friendlyName, string xml, byte[]? signature, string? signer)
    {
        await using var connection = new NpgsqlConnection(_asApplication);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {KeysTable} (friendly_name, xml, signature, signer) VALUES (@name, @xml, @signature, @signer) RETURNING id",
            connection);
        command.Parameters.AddWithValue("name", (object?)friendlyName ?? DBNull.Value);
        command.Parameters.AddWithValue("xml", xml);
        command.Parameters.Add(new NpgsqlParameter("signature", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = (object?)signature ?? DBNull.Value });
        command.Parameters.AddWithValue("signer", (object?)signer ?? DBNull.Value);
        return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        await AdminAsync(_postgres, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
        await AdminAsync(_postgres, $"DROP OWNED BY {_migrator}; DROP OWNED BY {_application}");
        await AdminAsync(_postgres, $"DROP ROLE IF EXISTS {_application}; DROP ROLE IF EXISTS {_migrator}");
    }

    private async Task<List<string>> ColumnAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_asMigrator);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // The SQL is written by this test; the names in it are constants.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return rows;
    }

    private static async Task AdminAsync(PostgresFixture postgres, string sql)
    {
        var admin = postgres.Connection("postgres");
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // Generated names only.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(int Exit, string Output)> RunMigratorAsync(string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(SolutionLocator.SolutionRoot(), "src", "MyApp.Migrator", "bin", configuration, "net10.0", "MyApp.Migrator.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }
}
