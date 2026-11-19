using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using MyApp.BuildingBlocks.Infrastructure.DataProtection;

namespace MyApp.Api;

/// <summary>
/// The Data Protection key repository of the API: the key ring in the shared database, through
/// <see cref="DataProtectionKeyStore"/>, so every instance of the API uses the same keys. It only reads and appends; the
/// key manager encrypts each key before it gets here (see <see cref="DataProtectionComposition"/>).
/// <list type="bullet">
/// <item>With a certificate configured, every row is signed on store with the current certificate's private key (RSA-PSS,
/// SHA-256) over its friendly name and XML, and the signer's SHA-256 thumbprint is kept with it. On read only rows whose
/// signature verifies against the current or a previous certificate are kept: a row the application's login could insert
/// without the private key (unsigned, unknown signer, bad signature) is skipped and logged with its id only.</item>
/// <item>Without a certificate (Development only) rows are stored unsigned and all are read.</item>
/// <item>A row whose XML does not parse is skipped and logged with its id only, so one bad row does not stop the ring.</item>
/// </list>
/// </summary>
/// <param name="store">The rows of the key ring.</param>
/// <param name="certificates">The certificates that sign and verify the rows.</param>
/// <param name="logger">The logger of skipped rows.</param>
internal sealed partial class DatabaseXmlRepository(
    DataProtectionKeyStore store,
    DataProtectionComposition.KeyRingCertificates certificates,
    ILogger<DatabaseXmlRepository> logger) : IXmlRepository
{
    // Separates this signature from any other made with the same certificate.
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("MyApp.DataProtection.Key.v1");

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();
        foreach (var row in store.ReadAll())
        {
            if (certificates.Current is not null && !IsSigned(row))
            {
                LogUnverified(logger, row.Id);
                continue;
            }

            try
            {
                elements.Add(XElement.Parse(row.Xml));
            }
            catch (XmlException)
            {
                LogMalformed(logger, row.Id);
            }
        }

        return elements;
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        var xml = element.ToString(SaveOptions.DisableFormatting);
        if (certificates.Current is not { } certificate)
        {
            store.Append(friendlyName, xml, signature: null, signer: null);
            return;
        }

        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The Data Protection certificate has no RSA private key.");
        var signature = rsa.SignData(Signed(friendlyName, xml), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        store.Append(friendlyName, xml, signature, Thumbprint(certificate));
    }

    private static string Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    // The signed bytes: the domain, then the friendly name (a presence byte, then its length and UTF-8 bytes) and the XML
    // (its length and UTF-8 bytes), each length a 4-byte big-endian integer, so no two rows frame to the same bytes.
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

    private bool IsSigned(DataProtectionKey row)
    {
        if (row.Signature is not { Length: > 0 } signature || string.IsNullOrEmpty(row.Signer))
        {
            return false;
        }

        var signer = certificates.All.FirstOrDefault(c => string.Equals(Thumbprint(c), row.Signer, StringComparison.OrdinalIgnoreCase));
        using var rsa = signer?.GetRSAPublicKey();
        if (rsa is null)
        {
            return false;
        }

        try
        {
            return rsa.VerifyData(Signed(row.FriendlyName, row.Xml), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 1, EventName = "DataProtectionKeyUnverified", Level = LogLevel.Warning,
        Message = "Data Protection key row {RowId} is skipped: it is not signed by a configured certificate.")]
    private static partial void LogUnverified(ILogger logger, int rowId);

    [LoggerMessage(EventId = 2, EventName = "DataProtectionKeyMalformed", Level = LogLevel.Warning,
        Message = "Data Protection key row {RowId} is skipped: its XML does not parse.")]
    private static partial void LogMalformed(ILogger logger, int rowId);
}
