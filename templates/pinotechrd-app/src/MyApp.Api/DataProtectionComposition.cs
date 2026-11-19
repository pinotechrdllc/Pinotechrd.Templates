using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Infrastructure.DataProtection;
using MyApp.BuildingBlocks.Web.OpenApi;

namespace MyApp.Api;

/// <summary>
/// Joins the Data Protection key ring of the platform (which the security library's authentication brings) to the shared
/// database of the infrastructure building block, and encrypts it with a certificate. It lives here because only the API
/// knows both ASP.NET Core and the infrastructure, which must not reference ASP.NET Core.
/// <list type="bullet">
/// <item>Storage: the table <c>data_protection.keys</c>, through <see cref="DatabaseXmlRepository"/>, so every instance of
/// the API uses the same keys. The migrator creates the table and grants the application's login read and insert only.</item>
/// <item>Encryption at rest: every new key is encrypted with <see cref="DataProtectionKeyOptions.Certificate"/>; keys
/// encrypted before a rotation are decrypted with <see cref="DataProtectionKeyOptions.PreviousCertificates"/>. The
/// encryption is the platform's (<see cref="CertificateXmlEncryptor"/>); the decryption looks for the certificate among
/// those settings only, never in a machine's certificate store, through <see cref="CertificateKeyDecryptor"/>, whose type
/// name each encrypted key records: keep that type where it is.</item>
/// <item>Integrity: with a certificate, every row is signed with its private key and only rows signed by a configured
/// certificate are read (see <see cref="DatabaseXmlRepository"/>), so a row inserted without the private key is ignored.</item>
/// <item>Outside Development the certificate is required, with an RSA key of at least
/// <see cref="DataProtectionKeyOptions.MinimumKeySize"/> bits, and checked at start: without a valid one the API does not
/// start, and the error names the setting, never a path. In Development it is optional: without it keys are stored
/// unencrypted and the platform logs a warning at start.</item>
/// <item>The application name, <see cref="ApplicationName"/>, isolates this application's keys: instances that share the
/// table protect and unprotect for each other. Changing it makes every protected payload unreadable.</item>
/// </list>
/// The build-time OpenAPI generation (<see cref="ApiDocumentGeneration.IsRunning"/>) runs with no configuration: there
/// nothing is checked and the key ring keeps the platform's default storage.
/// </summary>
public static class DataProtectionComposition
{
    /// <summary>The Data Protection application name shared by every instance of the API.</summary>
    public const string ApplicationName = "MyApp";

    /// <summary>Registers the key ring in the database, its encryption and the start-time check of its settings.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDataProtectionKeys(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddDataProtectionKeyStore();
        services.TryAddSingleton<DatabaseXmlRepository>();

        var options = services.AddOptions<DataProtectionKeyOptions>().BindConfiguration(DataProtectionKeyOptions.SectionName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DataProtectionKeyOptions>, KeyOptionsValidator>());
        if (!ApiDocumentGeneration.IsRunning)
        {
            options.ValidateOnStart();
        }

        services.TryAddSingleton<KeyRingCertificates>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<KeyManagementOptions>, ConfigureKeyManagement>());
        return services;
    }

    // A PEM certificate with its unencrypted PEM key. On Windows a key read from PEM is ephemeral, which the XML decryption
    // cannot use: a PKCS#12 round trip gives it a key it can.
    private static X509Certificate2 Load(DataProtectionCertificateFiles files)
    {
        var certificate = X509Certificate2.CreateFromPemFile(files.CertificatePath!, files.KeyPath);
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        using (certificate)
        {
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
        }
    }

    private static bool IsSet(DataProtectionCertificateFiles? files) =>
        files is not null && (!string.IsNullOrWhiteSpace(files.CertificatePath) || !string.IsNullOrWhiteSpace(files.KeyPath));

    // The messages name the setting and the rule, never a path or a file's content.
    private sealed class KeyOptionsValidator(IHostEnvironment environment) : IValidateOptions<DataProtectionKeyOptions>
    {
        public ValidateOptionsResult Validate(string? name, DataProtectionKeyOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (ApiDocumentGeneration.IsRunning)
            {
                return ValidateOptionsResult.Skip;
            }

            var failures = new List<string>();
            if (IsSet(options.Certificate))
            {
                Check(options.Certificate!, DataProtectionKeyOptions.CertificateKey, failures);
            }
            else if (!environment.IsDevelopment())
            {
                failures.Add(
                    $"{DataProtectionKeyOptions.CertificateKey} is required outside Development: set "
                    + $"{DataProtectionKeyOptions.CertificateKey}:{nameof(DataProtectionCertificateFiles.CertificatePath)} and "
                    + $"{DataProtectionKeyOptions.CertificateKey}:{nameof(DataProtectionCertificateFiles.KeyPath)} to a PEM certificate "
                    + $"with an RSA key of at least {DataProtectionKeyOptions.MinimumKeySize} bits and its PEM private key, mounted as secrets.");
            }

            for (var i = 0; i < options.PreviousCertificates.Count; i++)
            {
                Check(options.PreviousCertificates[i], $"{DataProtectionKeyOptions.PreviousCertificatesKey}:{i}", failures);
            }

            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }

        private static void Check(DataProtectionCertificateFiles files, string key, List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(files.CertificatePath) || string.IsNullOrWhiteSpace(files.KeyPath))
            {
                failures.Add(
                    $"{key} needs both {nameof(DataProtectionCertificateFiles.CertificatePath)} and "
                    + $"{nameof(DataProtectionCertificateFiles.KeyPath)}.");
                return;
            }

            X509Certificate2 certificate;
            try
            {
                certificate = Load(files);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                failures.Add($"{key} could not be read: it must name a PEM certificate and its matching unencrypted PEM private key.");
                return;
            }

            using (certificate)
            {
                using var rsa = certificate.GetRSAPrivateKey();
                if (rsa is null || rsa.KeySize < DataProtectionKeyOptions.MinimumKeySize)
                {
                    failures.Add($"{key} must hold an RSA key of at least {DataProtectionKeyOptions.MinimumKeySize} bits.");
                }
            }
        }
    }

    // The certificates of the settings, loaded once (after the settings pass their check) and kept for the process.
    internal sealed class KeyRingCertificates : IDisposable
    {
        public KeyRingCertificates(IOptions<DataProtectionKeyOptions> options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var settings = options.Value;
            Current = IsSet(settings.Certificate) ? Load(settings.Certificate!) : null;
            Previous = [.. settings.PreviousCertificates.Select(Load)];
        }

        public X509Certificate2? Current { get; }

        public IReadOnlyList<X509Certificate2> Previous { get; }

        public IReadOnlyList<X509Certificate2> All => [.. Previous.Prepend(Current).OfType<X509Certificate2>()];

        public void Dispose()
        {
            Current?.Dispose();
            foreach (var certificate in Previous)
            {
                certificate.Dispose();
            }
        }
    }

    private sealed class ConfigureKeyManagement(DatabaseXmlRepository repository, KeyRingCertificates certificates, ILoggerFactory loggerFactory)
        : IConfigureOptions<KeyManagementOptions>
    {
        public void Configure(KeyManagementOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (ApiDocumentGeneration.IsRunning)
            {
                return;
            }

            options.XmlRepository = repository;
            if (certificates.Current is { } certificate)
            {
                options.XmlEncryptor = new CertificateKeyEncryptor(new CertificateXmlEncryptor(certificate, loggerFactory));
            }
        }
    }

    // The platform's certificate encryption, recorded with the decryptor below instead of the platform's, which only finds
    // a certificate in the machine's store or in settings this project cannot reach.
    private sealed class CertificateKeyEncryptor(CertificateXmlEncryptor inner) : IXmlEncryptor
    {
        public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
            new(inner.Encrypt(plaintextElement).EncryptedElement, typeof(CertificateKeyDecryptor));
    }

    /// <summary>
    /// Decrypts a key encrypted with the current certificate or one of the previous ones of
    /// <see cref="DataProtectionKeyOptions"/>, matched by the thumbprint the encrypted key records. Created by the key
    /// manager from the type name stored with each key.
    /// </summary>
    internal sealed class CertificateKeyDecryptor : IXmlDecryptor
    {
        private readonly KeyRingCertificates _certificates;

        /// <summary>Creates the decryptor with the certificates of the application's services.</summary>
        /// <param name="services">The application's services.</param>
        public CertificateKeyDecryptor(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _certificates = services.GetRequiredService<KeyRingCertificates>();
        }

        /// <inheritdoc />
        public XElement Decrypt(XElement encryptedElement)
        {
            ArgumentNullException.ThrowIfNull(encryptedElement);

            // EncryptedXml works on a document: the element goes under a root, is decrypted in place and read back.
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = new XElement("root", encryptedElement).CreateReader())
            {
                document.Load(reader);
            }

            new CertificateEncryptedXml(document, _certificates.All).DecryptDocument();
            using var decrypted = document.DocumentElement!.FirstChild!.CreateNavigator()!.ReadSubtree();
            return XElement.Load(decrypted);
        }
    }

    // Finds the key's certificate among the configured ones only; a key no configured certificate encrypted fails.
    private sealed class CertificateEncryptedXml(XmlDocument document, IReadOnlyList<X509Certificate2> certificates) : EncryptedXml(document)
    {
        public override byte[]? DecryptEncryptedKey(EncryptedKey encryptedKey)
        {
            ArgumentNullException.ThrowIfNull(encryptedKey);

            var thumbprints = encryptedKey.KeyInfo.OfType<KeyInfoX509Data>()
                .SelectMany(data => data.Certificates?.OfType<X509Certificate2>() ?? [])
                .Select(c => c.Thumbprint);
            foreach (var thumbprint in thumbprints)
            {
                var certificate = certificates.FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
                using var rsa = certificate?.GetRSAPrivateKey();
                if (rsa is not null && encryptedKey.CipherData.CipherValue is { } cipher)
                {
                    var oaep = string.Equals(encryptedKey.EncryptionMethod?.KeyAlgorithm, XmlEncRSAOAEPUrl, StringComparison.Ordinal);
                    return DecryptKey(cipher, rsa, oaep);
                }
            }

            return null;
        }
    }
}
