using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using Npgsql;
using Serilog.Events;

namespace MyApp.Api.Tests.Security;

// The key ring of the real host on real PostgreSQL: keys are kept in the shared data_protection schema (the migrator creates it
// and grants the application login), encrypted with the certificate, shared by every instance, and still readable after a
// certificate rotation. The migrator runs as its own process, with a separate application login, as in production.
[Collection(PostgresContainer.Name)]
public class DataProtectionKeyRingTests(PostgresFixture postgres)
{
    private const string Purpose = "Pinotechrd.Tests.KeyRing";

    private const string Payload = "payload-protected-by-one-host";

    private const string KeysTable = "data_protection.keys";

    // Breaks when the keys stay in memory or in a file of the host (a second host with the same database and certificate cannot
    // read what the first protected, as with several instances behind a load balancer), or when the second host creates a key
    // of its own instead of reading the first one.
    [Fact]
    public async Task TwoHostsOnTheSameDatabaseAndCertificateReadEachOthersProtectedData()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var certificate = TestCertificates.CreateRsa();

        string protectedByFirst;
        using (var first = database.Host(certificate))
        {
            protectedByFirst = ProtectorOf(first).Protect(Payload);
        }

        using var second = database.Host(certificate);
        var unprotected = ProtectorOf(second).Unprotect(protectedByFirst);

        Assert.Equal(Payload, unprotected);
        Assert.Equal(1L, await database.KeyCountAsync());
    }

    // Breaks when the host does not persist its keys as the application login (the grant of SELECT and INSERT on the keys is
    // missing, or the repository writes elsewhere): the first Protect fails with a database error or writes no row.
    [Fact]
    public async Task ProtectingDataStoresAKeyRowAsTheApplicationLogin()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var certificate = TestCertificates.CreateRsa();
        using var host = database.Host(certificate);
        Assert.Equal(0L, await database.KeyCountAsync());

        _ = ProtectorOf(host).Protect(Payload);

        Assert.Equal(1L, await database.KeyCountAsync());
    }

    // Breaks when the stored key is plain XML (the master key readable by anyone with the database), or when the host logs the
    // data protection warning that no XML encryptor is configured.
    [Fact]
    public async Task StoredKeysAreEncryptedWithTheCertificateAndTheHostLogsNoDataProtectionWarning()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var certificate = TestCertificates.CreateRsa();
        using var host = database.Host(certificate);
        var sink = host.CaptureLogs();

        _ = ProtectorOf(host).Protect(Payload);

        var xml = Assert.Single(await database.KeyXmlAsync());
        Assert.Contains("EncryptedData", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("masterKey", xml, StringComparison.Ordinal);
        var warnings = sink.Events
            .Where(e => e.Level >= LogEventLevel.Warning && (e.CategoryOf() ?? string.Empty).StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal))
            .Select(e => e.RenderMessage(CultureInfo.InvariantCulture))
            .ToList();
        Assert.Empty(warnings);
    }

    // Breaks when a certificate that is no longer the current one cannot decrypt the keys it encrypted (the ring would be lost at
    // the first rotation), or when the previous certificate is not read from DataProtection:PreviousCertificates.
    [Fact]
    public async Task AfterARotationHostsStillReadDataProtectedBeforeWhenThePreviousCertificateIsConfigured()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var oldCertificate = TestCertificates.CreateRsa();
        using var newCertificate = TestCertificates.CreateRsa();
        string protectedBeforeRotation;
        using (var before = database.Host(oldCertificate))
        {
            protectedBeforeRotation = ProtectorOf(before).Protect(Payload);
        }

        using var after = database.Host(newCertificate, previous: oldCertificate);

        Assert.Equal(Payload, ProtectorOf(after).Unprotect(protectedBeforeRotation));
    }

    // Breaks when the new certificate alone decrypts the old keys (the control of the rotation test: it proves that the previous
    // certificate is what makes the data readable, and that the keys really are encrypted with a certificate).
    [Fact]
    public async Task AfterARotationWithoutThePreviousCertificateDataProtectedBeforeCannotBeRead()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var oldCertificate = TestCertificates.CreateRsa();
        using var newCertificate = TestCertificates.CreateRsa();
        string protectedBeforeRotation;
        using (var before = database.Host(oldCertificate))
        {
            protectedBeforeRotation = ProtectorOf(before).Protect(Payload);
        }

        using var after = database.Host(newCertificate);

        Assert.ThrowsAny<CryptographicException>(() => ProtectorOf(after).Unprotect(protectedBeforeRotation));
    }

    private static IDataProtector ProtectorOf(ApiHostFactory host) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    // A database as production has it: a migrator login that owns it and a separate application login. The migrator is run as
    // its own process (a second composition root, not a reference of the host's tests).
    private sealed class KeyRingDatabase : IAsyncDisposable
    {
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
}
