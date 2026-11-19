using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
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
}
