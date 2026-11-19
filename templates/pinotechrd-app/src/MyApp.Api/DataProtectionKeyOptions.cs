namespace MyApp.Api;

/// <summary>
/// Settings of the Data Protection key ring, section <see cref="SectionName"/>: the certificate that encrypts the keys
/// stored in the database, and the previous ones that may still decrypt them after a rotation. Paths only: the files
/// are mounted as secrets, never kept in the repository or in an <c>appsettings</c> file.
/// </summary>
public sealed class DataProtectionKeyOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "DataProtection";

    /// <summary>The configuration key of <see cref="Certificate"/>, the name the start-up errors give.</summary>
    public const string CertificateKey = SectionName + ":" + nameof(Certificate);

    /// <summary>The configuration key of <see cref="PreviousCertificates"/>.</summary>
    public const string PreviousCertificatesKey = SectionName + ":" + nameof(PreviousCertificates);

    /// <summary>The smallest RSA key accepted, in bits.</summary>
    public const int MinimumKeySize = 2048;

    /// <summary>
    /// Gets or sets the certificate that encrypts new keys and decrypts them. Required outside Development; in
    /// Development, without it, keys are stored unencrypted and the platform logs a warning at start.
    /// </summary>
    public DataProtectionCertificateFiles? Certificate { get; set; }

    /// <summary>Gets the certificates used before a rotation: they only decrypt the keys they encrypted.</summary>
    public IList<DataProtectionCertificateFiles> PreviousCertificates { get; } = [];
}

/// <summary>A PEM certificate with an RSA key of at least 2048 bits, and its unencrypted PEM private key.</summary>
public sealed class DataProtectionCertificateFiles
{
    /// <summary>Gets or sets the path of the PEM certificate.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Gets or sets the path of the PEM private key.</summary>
    public string? KeyPath { get; set; }
}
