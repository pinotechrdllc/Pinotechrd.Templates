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
/// <item>A row whose XML does not parse, or a key without a valid id, is skipped and logged with its id only, so one bad row
/// does not stop the ring. A later row repeating a key id or the exact XML of a row already read (a replayed signed row) is
/// skipped the same way, and so is a row whose XML is longer than <see cref="MaximumXmlLength"/> or whose signature length
/// does not match the signer's key, before anything is parsed or verified.</item>
/// <item>A singleton: it keeps the elements already checked and the highest row id read, and each read fetches and checks
/// only the rows stored since, so junk rows cost once per full read (at start, then every 24 hours), not on every reload
/// of the ring.</item>
/// </list>
/// </summary>
/// <param name="store">The rows of the key ring.</param>
/// <param name="certificates">The certificates that sign and verify the rows.</param>
/// <param name="logger">The logger of skipped rows.</param>
/// <param name="timeProvider">The clock of the periodic full read; the system clock when none is registered.</param>
internal sealed partial class DatabaseXmlRepository(
    DataProtectionKeyStore store,
    DataProtectionComposition.KeyRingCertificates certificates,
    ILogger<DatabaseXmlRepository> logger,
    TimeProvider? timeProvider = null) : IXmlRepository
{
    // Separates this signature from any other made with the same certificate.
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("MyApp.DataProtection.Key.v1");

    private static readonly XName KeyElement = "key";

    // How many ids below the highest one read are read again when not processed yet. Identity ids are taken when a row is
    // inserted, not when it commits, so a row can become visible after a higher id was read; the window picks it up.
    // Limit: a row that commits after more than Window newer rows were inserted is missed until the next full read, at
    // most FullReadInterval later.
    private const int Window = 1000;

    // How often the state is rebuilt from a full read of the table.
    private static readonly TimeSpan FullReadInterval = TimeSpan.FromHours(24);

    // The state of the rows read so far. Rows are only appended, so each read fetches only the rows after _lastId and the
    // rows of the window not yet processed. Every row read is processed once, kept or skipped, so a row is never read or
    // counted twice; ids that fall out of the window are pruned, so the state stays bounded.
    private readonly Lock _gate = new();
    private readonly List<XElement> _elements = [];
    private readonly HashSet<Guid> _keyIds = [];
    private readonly HashSet<int> _processed = [];
    private readonly HashSet<string> _xmlHashes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private int _lastId;
    private DateTimeOffset _fullReadAt;

    /// <summary>The longest XML a row may hold, in characters; a longer row is skipped before it is parsed or verified.</summary>
    internal const int MaximumXmlLength = 64 * 1024;

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastId == 0 || now - _fullReadAt >= FullReadInterval)
            {
                _elements.Clear();
                _keyIds.Clear();
                _processed.Clear();
                _xmlHashes.Clear();
                _lastId = 0;
                _fullReadAt = now;
            }

            foreach (var row in store.ReadUnprocessed(_lastId, Window, _processed))
            {
                Accept(row);
                _processed.Add(row.Id);
                _lastId = Math.Max(_lastId, row.Id);
            }

            _processed.RemoveWhere(id => id <= _lastId - Window);

            return [.. _elements.Select(e => new XElement(e))];
        }
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

    // Checks one new row and keeps its element, under the gate.
    private void Accept(DataProtectionKey row)
    {
        if (row.Xml.Length > MaximumXmlLength)
        {
            LogOversized(logger, row.Id);
            return;
        }

        if (certificates.Current is not null && !IsSigned(row))
        {
            LogUnverified(logger, row.Id);
            return;
        }

        // The signature covers the exact XML, so a replayed row is an exact copy: one already kept is skipped, whatever its
        // kind, so replayed revocations cannot grow the state.
        if (!_xmlHashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Xml)))))
        {
            LogDuplicate(logger, row.Id);
            return;
        }

        XElement element;
        try
        {
            element = XElement.Parse(row.Xml);
        }
        catch (XmlException)
        {
            LogMalformed(logger, row.Id);
            return;
        }

        // The key manager refuses a key id twice, which would stop the ring: keep the first stored row of each key.
        // Revocations with the same id but other XML repeat harmlessly (the key manager collects them in a set).
        if (element.Name == KeyElement)
        {
            if (!Guid.TryParse((string?)element.Attribute("id"), out var keyId))
            {
                LogMalformed(logger, row.Id);
                return;
            }

            if (!_keyIds.Add(keyId))
            {
                LogDuplicate(logger, row.Id);
                return;
            }
        }

        _elements.Add(element);
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
        if (rsa is null || signature.Length != (rsa.KeySize + 7) / 8)
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

    [LoggerMessage(EventId = 3, EventName = "DataProtectionKeyDuplicate", Level = LogLevel.Warning,
        Message = "Data Protection key row {RowId} is skipped: an earlier row holds the same key id or the same XML.")]
    private static partial void LogDuplicate(ILogger logger, int rowId);

    [LoggerMessage(EventId = 4, EventName = "DataProtectionKeyOversized", Level = LogLevel.Warning,
        Message = "Data Protection key row {RowId} is skipped: its XML is longer than the limit.")]
    private static partial void LogOversized(ILogger logger, int rowId);
}
