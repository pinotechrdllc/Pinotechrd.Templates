using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MyApp.Api.Tests.Support;

/// <summary>Configuration keys of the <c>DataProtection</c> section, as documented in the README.</summary>
internal static class DataProtectionSettingKeys
{
    public const string Section = "DataProtection";

    public const string CertificatePath = "DataProtection:Certificate:CertificatePath";

    public const string KeyPath = "DataProtection:Certificate:KeyPath";

    /// <summary>The setting name the start-up failure must give when the certificate is missing or invalid.</summary>
    public const string CertificateSection = "DataProtection:Certificate";

    public static string PreviousCertificatePath(int index) => $"DataProtection:PreviousCertificates:{index}:CertificatePath";

    public static string PreviousKeyPath(int index) => $"DataProtection:PreviousCertificates:{index}:KeyPath";
}

/// <summary>A PEM certificate and its PEM private key, written in a directory of their own that is deleted on dispose.</summary>
public sealed class TestCertificateFiles : IDisposable
{
    private readonly string _directory;

    public TestCertificateFiles(string certificatePem, string keyPem)
    {
        _directory = Path.Combine(Path.GetTempPath(), "pinotechrd-test-cert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        CertificatePath = Path.Combine(_directory, "certificate.pem");
        KeyPath = Path.Combine(_directory, "private-key.pem");
        File.WriteAllText(CertificatePath, certificatePem);
        File.WriteAllText(KeyPath, keyPem);
    }

    public string CertificatePath { get; }

    public string KeyPath { get; }

    /// <summary>The certificate with its private key, to sign as the host does. The caller disposes it.</summary>
    public X509Certificate2 LoadWithPrivateKey() => X509Certificate2.CreateFromPemFile(CertificatePath, KeyPath);

    /// <summary>The certificate alone, as anyone who can read the database or the repository sees it. The caller disposes it.</summary>
    public X509Certificate2 LoadPublic() => X509Certificate2.CreateFromPem(File.ReadAllText(CertificatePath));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory a scanner holds open for a moment: the operating system cleans it later.
        }
    }
}

/// <summary>
/// Certificates made in memory for the tests, never committed: a self-signed certificate with a fresh key each call, as
/// PEM files the way a deployment mounts them.
/// </summary>
internal static class TestCertificates
{
    private static readonly Lazy<TestCertificateFiles> Shared = new(() =>
    {
        var files = CreateRsa(2048);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => files.Dispose();
        return files;
    });

    /// <summary>The certificate every host of a run gets unless the test chooses its own: RSA 2048, valid, shared by the process.</summary>
    public static TestCertificateFiles Default => Shared.Value;

    /// <summary>A new self-signed RSA certificate of <paramref name="keySize"/> bits; the caller disposes it (deletes the files).</summary>
    public static TestCertificateFiles CreateRsa(int keySize = 2048)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest("CN=Pinotechrd test " + Guid.NewGuid().ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return new TestCertificateFiles(certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>A new self-signed elliptic-curve certificate: valid as a certificate, useless to encrypt a key ring.</summary>
    public static TestCertificateFiles CreateEllipticCurve()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Pinotechrd test " + Guid.NewGuid().ToString("N"), ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return new TestCertificateFiles(certificate.ExportCertificatePem(), ecdsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Files that are not PEM at all, in the places a certificate and its key would be.</summary>
    public static TestCertificateFiles CreateGarbage() => new("this is not a certificate", "this is not a key");
}
