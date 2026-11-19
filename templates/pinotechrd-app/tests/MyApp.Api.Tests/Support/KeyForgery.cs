using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyApp.Api.Tests.Support;

/// <summary>A key the attacker made, with a payload protected by it and the row XML as it would be inserted.</summary>
internal sealed record ForgedKey(string Xml, string ProtectedPayload);

/// <summary>
/// What someone holding only the application's database login can make: a key ring of their own (their master key), built by a
/// separate in-process Data Protection provider with the application's name, and the XML of its key as a row. Also the row
/// signature as the key ring makes it (RSA-PSS with SHA-256 over the domain, the name and the XML, signer = the
/// SHA-256 thumbprint of the certificate), for the tests that need a correctly signed row.
/// </summary>
internal static class KeyForgery
{
    private const string ApplicationName = "MyApp";

    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("MyApp.DataProtection.Key.v1");

    /// <summary>A key whose master key the attacker chose, stored as the key manager writes it when no encryptor is set.</summary>
    public static ForgedKey Unencrypted(string purpose, string payload) => Create(purpose, payload, encryptWith: null, decryptorTypeOf: null);

    /// <summary>
    /// A key whose master key the attacker chose, encrypted with <paramref name="publicCertificate"/> (public part only, as it is
    /// in every stored row) and recording the decryptor of <paramref name="existingRowXml"/>, so the host would decrypt it.
    /// </summary>
    public static ForgedKey EncryptedWithPublicCertificate(string purpose, string payload, X509Certificate2 publicCertificate, string existingRowXml) =>
        Create(purpose, payload, publicCertificate, existingRowXml);

    /// <summary>The SHA-256 thumbprint that identifies a signer.</summary>
    public static string Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>The signature of a row made with the private key of <paramref name="certificate"/>.</summary>
    public static byte[] Sign(X509Certificate2 certificate, string? friendlyName, string xml)
    {
        using var rsa = certificate.GetRSAPrivateKey()!;
        return rsa.SignData(Signed(friendlyName, xml), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    /// <summary>Whether <paramref name="signature"/> is a valid signature of the row by <paramref name="certificate"/>.</summary>
    public static bool Verifies(X509Certificate2 certificate, string? friendlyName, string xml, byte[] signature)
    {
        using var rsa = certificate.GetRSAPublicKey()!;
        return rsa.VerifyData(Signed(friendlyName, xml), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    private static ForgedKey Create(string purpose, string payload, X509Certificate2? encryptWith, string? decryptorTypeOf)
    {
        var repository = new InMemoryXmlRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = repository;
            if (encryptWith is not null)
            {
                options.XmlEncryptor = new CertificateXmlEncryptor(encryptWith, NullLoggerFactory.Instance);
            }
        });
        using var provider = services.BuildServiceProvider();
        var protectedPayload = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose).Protect(payload);
        var element = repository.GetAllElements().Single();

        if (decryptorTypeOf is not null)
        {
            var existing = XElement.Parse(decryptorTypeOf).DescendantsAndSelf().Select(e => e.Attribute("decryptorType")).First(a => a is not null)!.Value;
            foreach (var attribute in element.DescendantsAndSelf().Select(e => e.Attribute("decryptorType")).Where(a => a is not null))
            {
                attribute!.Value = existing;
            }
        }

        return new ForgedKey(element.ToString(SaveOptions.DisableFormatting), protectedPayload);
    }

    private static byte[] Signed(string? friendlyName, string xml)
    {
        var name = friendlyName is null ? [] : Encoding.UTF8.GetBytes(friendlyName);
        var text = Encoding.UTF8.GetBytes(xml);
        var data = new byte[Domain.Length + 1 + 4 + name.Length + 4 + text.Length];
        var span = data.AsSpan();
        Domain.CopyTo(span);
        span = span[Domain.Length..];
        span[0] = friendlyName is null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32BigEndian(span[1..], name.Length);
        name.CopyTo(span[5..]);
        span = span[(5 + name.Length)..];
        BinaryPrimitives.WriteInt32BigEndian(span, text.Length);
        text.CopyTo(span[4..]);
        return data;
    }
}
