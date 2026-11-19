namespace MyApp.BuildingBlocks.Infrastructure.DataProtection;

/// <summary>
/// One element of the Data Protection key ring, as the key manager wrote it: a key (encrypted with the certificate
/// outside Development) or a revocation. Rows are only added, never changed or removed.
/// </summary>
public sealed class DataProtectionKey
{
    /// <summary>Gets or sets the identity filled by the database, which gives the order the elements were stored in.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the name the key manager gave the element, such as <c>key-&lt;id&gt;</c>.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>Gets or sets the element's XML.</summary>
    public string Xml { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the signature of the friendly name and the XML by the private key of the certificate that stored the
    /// row; <see langword="null"/> when no certificate was configured (Development only).
    /// </summary>
#pragma warning disable CA1819 // An EF-mapped bytea column.
    public byte[]? Signature { get; set; }
#pragma warning restore CA1819

    /// <summary>Gets or sets the SHA-256 thumbprint of the certificate that signed the row, or <see langword="null"/>.</summary>
    public string? Signer { get; set; }
}
