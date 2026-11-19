using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using Serilog.Events;

namespace MyApp.Api.Tests.Security;

// The key ring lives in a table the application's login can INSERT into. Anyone who steals that login could add a key whose
// master key they chose and make the application accept what they protected. With a certificate, the ring only uses rows signed
// with the certificate's private key (which the login never has); these tests insert the forged rows as the application login
// on real PostgreSQL, before a fresh host loads the ring (the ring is cached once loaded), and prove the host ignores them while
// the legitimate data still unprotects.
[Collection(PostgresContainer.Name)]
public class DataProtectionKeyIntegrityTests(PostgresFixture postgres)
{
    private const string Purpose = "Pinotechrd.Tests.KeyIntegrity";

    private const string Payload = "legitimate-payload";

    private const string AttackerPayload = "payload-protected-by-the-attacker";

    private const string Unterminated = "<key><unterminated";

    private const string NotXml = "not xml at all";

    private static IDataProtector ProtectorOf(ApiHostFactory host) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    // Breaks when a row is stored without a signature or signer although a certificate is configured, or when the signature is
    // not made with that certificate's private key (it does not verify with the public key).
    [Fact]
    public async Task EveryStoredKeyRowCarriesASignatureMadeWithTheCertificatesPrivateKey()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        using var certificate = files.LoadWithPrivateKey();
        using (var host = database.Host(files))
        {
            _ = ProtectorOf(host).Protect(Payload);
        }

        Assert.Equal(await database.KeyCountAsync(), await database.SignedRowCountAsync());
        var signature = await database.FirstSignatureAsync();
        Assert.NotNull(signature);
        Assert.Equal(KeyForgery.Thumbprint(certificate), await database.FirstSignerAsync(), ignoreCase: true);
        Assert.True(KeyForgery.Verifies(certificate, await database.FirstFriendlyNameAsync(), await database.FirstKeyXmlAsync(), signature));
    }

    // Breaks when the ring uses a row with an unencrypted key element inserted by the application login: the attacker's master key
    // would be accepted and what they protected would unprotect.
    [Fact]
    public async Task AnUnencryptedKeyInsertedByTheApplicationLoginIsIgnored()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        var legitimate = await ProtectWithLegitimateHostAsync(database, files);
        var forged = KeyForgery.Unencrypted(Purpose, AttackerPayload);
        _ = await database.InsertAsApplicationAsync("key-forged", forged.Xml, signature: null, signer: null);

        using var host = database.Host(files);

        AssertAttackFailsAndLegitimateDataStillReads(host, legitimate, forged);
    }

    // Breaks when a row encrypted with the certificate's public key (which anyone can copy from an existing row) is used although
    // it carries no signature: the attacker needs no private key to make such a row.
    [Fact]
    public async Task AKeyEncryptedWithThePublicCertificateButUnsignedIsIgnored()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        var legitimate = await ProtectWithLegitimateHostAsync(database, files);
        var forged = ForgeEncryptedFor(files, await database.FirstKeyXmlAsync());
        _ = await database.InsertAsApplicationAsync("key-forged", forged.Xml, signature: null, signer: null);

        using var host = database.Host(files);

        AssertAttackFailsAndLegitimateDataStillReads(host, legitimate, forged);
    }

    // Breaks when the ring trusts a signer name without verifying the signature: a row that names the right certificate with bytes
    // that are not its signature is used.
    [Fact]
    public async Task AKeyEncryptedWithThePublicCertificateWithAWrongSignatureIsIgnored()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        using var certificate = files.LoadPublic();
        var legitimate = await ProtectWithLegitimateHostAsync(database, files);
        var forged = ForgeEncryptedFor(files, await database.FirstKeyXmlAsync());
        var wrongSignature = RandomNumberGenerator.GetBytes(256);
        _ = await database.InsertAsApplicationAsync("key-forged", forged.Xml, wrongSignature, KeyForgery.Thumbprint(certificate));

        using var host = database.Host(files);

        AssertAttackFailsAndLegitimateDataStillReads(host, legitimate, forged);
    }

    // Breaks when a valid signature of the legitimate row is accepted for another row (a signature copied from an existing row and
    // attached to the forged key).
    [Fact]
    public async Task AKeyWithTheSignatureCopiedFromALegitimateRowIsIgnored()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        using var certificate = files.LoadPublic();
        var legitimate = await ProtectWithLegitimateHostAsync(database, files);
        var copiedSignature = (await database.FirstSignatureAsync())!;
        var forged = ForgeEncryptedFor(files, await database.FirstKeyXmlAsync());
        _ = await database.InsertAsApplicationAsync("key-forged", forged.Xml, copiedSignature, KeyForgery.Thumbprint(certificate));

        using var host = database.Host(files);

        AssertAttackFailsAndLegitimateDataStillReads(host, legitimate, forged);
    }

    // Breaks when a signature from any certificate is enough: the attacker signs with a certificate of their own, which the host
    // does not list as current or previous.
    [Fact]
    public async Task AKeySignedByAnotherCertificateIsIgnored()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        using var attacker = TestCertificates.CreateRsa();
        using var attackerCertificate = attacker.LoadWithPrivateKey();
        var legitimate = await ProtectWithLegitimateHostAsync(database, files);
        var forged = ForgeEncryptedFor(files, await database.FirstKeyXmlAsync());
        _ = await database.InsertAsApplicationAsync(
            "key-forged",
            forged.Xml,
            KeyForgery.Sign(attackerCertificate, "key-forged", forged.Xml),
            KeyForgery.Thumbprint(attackerCertificate));

        using var host = database.Host(files);

        AssertAttackFailsAndLegitimateDataStillReads(host, legitimate, forged);
    }

    // Breaks when the forgery of these tests is not a key the host would use (the control of the attack tests: the same row, signed
    // with the current certificate's private key, is accepted, so the attacks fail only because the signature is not valid).
    [Fact]
    public async Task TheSameForgedKeySignedWithTheCurrentCertificateIsUsed()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        using var certificate = files.LoadWithPrivateKey();
        _ = await ProtectWithLegitimateHostAsync(database, files);
        var forged = ForgeEncryptedFor(files, await database.FirstKeyXmlAsync());
        _ = await database.InsertAsApplicationAsync(
            "key-forged",
            forged.Xml,
            KeyForgery.Sign(certificate, "key-forged", forged.Xml),
            KeyForgery.Thumbprint(certificate));

        using var host = database.Host(files);

        Assert.Equal(AttackerPayload, ProtectorOf(host).Unprotect(forged.ProtectedPayload));
    }

    // Breaks when rows signed by a certificate of DataProtection:PreviousCertificates are no longer used after a rotation (the
    // ring would lose every key made before it), here the first key and a second row signed by the previous certificate.
    [Fact]
    public async Task AfterARotationRowsSignedByAPreviousCertificateAreStillUsed()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var oldFiles = TestCertificates.CreateRsa();
        using var newFiles = TestCertificates.CreateRsa();
        using var oldCertificate = oldFiles.LoadWithPrivateKey();
        var legitimate = await ProtectWithLegitimateHostAsync(database, oldFiles);
        var signedByPrevious = ForgeEncryptedFor(newFiles, await database.FirstKeyXmlAsync());
        _ = await database.InsertAsApplicationAsync(
            "key-previous",
            signedByPrevious.Xml,
            KeyForgery.Sign(oldCertificate, "key-previous", signedByPrevious.Xml),
            KeyForgery.Thumbprint(oldCertificate));

        using var host = database.Host(newFiles, previous: oldFiles);
        var protector = ProtectorOf(host);

        Assert.Equal(Payload, protector.Unprotect(legitimate));
        Assert.Equal(AttackerPayload, protector.Unprotect(signedByPrevious.ProtectedPayload));
    }

    // Breaks when a retired certificate (no longer listed anywhere) still validates rows: after the rotation without the previous
    // certificate, a row it signed is ignored.
    [Fact]
    public async Task ARowSignedByACertificateThatIsNoLongerListedIsIgnored()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var retiredFiles = TestCertificates.CreateRsa();
        using var currentFiles = TestCertificates.CreateRsa();
        using var retired = retiredFiles.LoadWithPrivateKey();
        _ = await ProtectWithLegitimateHostAsync(database, retiredFiles);
        var forged = ForgeEncryptedFor(currentFiles, await database.FirstKeyXmlAsync());
        _ = await database.InsertAsApplicationAsync(
            "key-retired",
            forged.Xml,
            KeyForgery.Sign(retired, "key-retired", forged.Xml),
            KeyForgery.Thumbprint(retired));

        using var host = database.Host(currentFiles);

        Assert.ThrowsAny<CryptographicException>(() => ProtectorOf(host).Unprotect(forged.ProtectedPayload));
    }

    // Breaks when Development without a certificate no longer works with unsigned rows (keys stored unsigned are not read back, so
    // every restart loses the ring), or when the host writes a signature without having a certificate to sign with.
    [Fact]
    public async Task WithoutACertificateUnsignedRowsStillWork()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        string protectedByFirst;
        using (var first = database.HostWithoutCertificate())
        {
            protectedByFirst = ProtectorOf(first).Protect(Payload);
        }

        using var second = database.HostWithoutCertificate();

        Assert.Equal(Payload, ProtectorOf(second).Unprotect(protectedByFirst));
        Assert.Equal(1L, await database.KeyCountAsync());
        Assert.Equal(0L, await database.SignedRowCountAsync());
        Assert.Null(await database.FirstSignatureAsync());
    }

    // Breaks when a row whose XML does not parse stops the ring (Protect or Unprotect throw), or when the warning carries the XML
    // of the row (an attacker could use the log to plant text) instead of the row id alone. The host has no certificate.
    [Fact]
    public async Task MalformedRowsDoNotBreakTheRingWithoutACertificateAndTheLogCarriesOnlyTheirIds()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        string legitimate;
        using (var first = database.HostWithoutCertificate())
        {
            legitimate = ProtectorOf(first).Protect(Payload);
        }

        var ids = new[]
        {
            await database.InsertAsApplicationAsync("key-bad-1", Unterminated, signature: null, signer: null),
            await database.InsertAsApplicationAsync("key-bad-2", NotXml, signature: null, signer: null),
        };

        using var host = database.HostWithoutCertificate();
        AssertRingSurvives(host, legitimate, ids);
    }

    // Breaks when a malformed row that carries no signature (an attacker's) makes the ring fail or is logged with its content.
    [Fact]
    public async Task UnsignedMalformedRowsDoNotBreakTheRingWithACertificateAndTheLogCarriesOnlyTheirIds()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        var legitimate = await ProtectWithLegitimateHostAsync(database, files);
        var ids = new[]
        {
            await database.InsertAsApplicationAsync("key-bad-1", Unterminated, signature: null, signer: null),
            await database.InsertAsApplicationAsync("key-bad-2", NotXml, signature: null, signer: null),
        };

        using var host = database.Host(files);
        AssertRingSurvives(host, legitimate, ids);
    }

    // Breaks when a malformed row that does carry a valid signature (it passed the integrity check, then fails to parse) makes the
    // ring fail or is logged with its content: parsing must be guarded after the signature is verified too.
    [Fact]
    public async Task SignedMalformedRowsDoNotBreakTheRingAndTheLogCarriesOnlyTheirIds()
    {
        await using var database = await KeyRingDatabase.CreateAsync(postgres);
        using var files = TestCertificates.CreateRsa();
        using var certificate = files.LoadWithPrivateKey();
        var legitimate = await ProtectWithLegitimateHostAsync(database, files);
        var ids = new[]
        {
            await database.InsertAsApplicationAsync("key-bad-1", Unterminated, KeyForgery.Sign(certificate, "key-bad-1", Unterminated), KeyForgery.Thumbprint(certificate)),
            await database.InsertAsApplicationAsync("key-bad-2", NotXml, KeyForgery.Sign(certificate, "key-bad-2", NotXml), KeyForgery.Thumbprint(certificate)),
        };

        using var host = database.Host(files);
        AssertRingSurvives(host, legitimate, ids);
    }

    private static void AssertRingSurvives(ApiHostFactory host, string legitimate, int[] malformedIds)
    {
        var sink = host.CaptureLogs();
        var protector = ProtectorOf(host);

        Assert.Equal(Payload, protector.Unprotect(legitimate));
        Assert.Equal("another", protector.Unprotect(protector.Protect("another")));

        var events = sink.Events;
        foreach (var text in events.SelectMany(e => e.TextsOf()))
        {
            Assert.DoesNotContain("unterminated", text, StringComparison.Ordinal);
            Assert.DoesNotContain(NotXml, text, StringComparison.Ordinal);
        }

        var warnings = events
            .Where(e => e.Level >= LogEventLevel.Warning)
            .Select(e => e.RenderMessage(CultureInfo.InvariantCulture))
            .ToList();
        foreach (var id in malformedIds)
        {
            var asWord = new Regex($@"\b{id.ToString(CultureInfo.InvariantCulture)}\b", RegexOptions.None, TimeSpan.FromSeconds(1));
            Assert.Contains(warnings, message => asWord.IsMatch(message));
        }
    }

    private static ForgedKey ForgeEncryptedFor(TestCertificateFiles files, string existingRowXml)
    {
        using var publicCertificate = files.LoadPublic();
        return KeyForgery.EncryptedWithPublicCertificate(Purpose, AttackerPayload, publicCertificate, existingRowXml);
    }

    // The legitimate host creates the first key of the ring (signed with the certificate) before anything is forged.
    private static async Task<string> ProtectWithLegitimateHostAsync(KeyRingDatabase database, TestCertificateFiles files)
    {
        using var host = database.Host(files);
        var protectedPayload = ProtectorOf(host).Protect(Payload);
        Assert.Equal(1L, await database.KeyCountAsync());
        return protectedPayload;
    }

    private static void AssertAttackFailsAndLegitimateDataStillReads(ApiHostFactory host, string legitimate, ForgedKey forged)
    {
        var protector = ProtectorOf(host);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(forged.ProtectedPayload));
        Assert.Equal(Payload, protector.Unprotect(legitimate));
    }
}
