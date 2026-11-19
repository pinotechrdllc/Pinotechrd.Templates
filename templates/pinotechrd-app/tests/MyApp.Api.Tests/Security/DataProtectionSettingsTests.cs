using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// The certificate that encrypts the key ring: outside Development the host refuses to start without a valid one (an RSA
// certificate of at least 2048 bits, with its private key), with a message that names the setting and never quotes a path;
// Development starts without one.
public class DataProtectionSettingsTests
{
    private const string MissingDirectory = "no-such-directory-7c2f1e";

    public static TheoryData<string> EnvironmentsOutsideDevelopment() => [Environments.Production, Environments.Staging];

    // Breaks when a host outside Development starts without a certificate (keys would be stored unencrypted).
    [Theory]
    [MemberData(nameof(EnvironmentsOutsideDevelopment))]
    public void HostOutsideDevelopmentWithoutACertificateRefusesToStart(string environment)
    {
        using var factory = ApiHostFactory.ForEnvironment(environment).WithoutDataProtectionCertificate();

        StartupFailure.AssertOptionsInvalid(factory, DataProtectionSettingKeys.CertificateSection);
    }

    // Breaks when the certificate is accepted without its private key.
    [Fact]
    public void HostOutsideDevelopmentWithOnlyTheCertificatePathRefusesToStart()
    {
        using var certificate = TestCertificates.CreateRsa();
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production)
            .WithoutDataProtectionCertificate()
            .WithSetting(DataProtectionSettingKeys.CertificatePath, certificate.CertificatePath);

        StartupFailure.AssertOptionsInvalid(factory, DataProtectionSettingKeys.CertificateSection);
    }

    // Breaks when a private key path alone is accepted.
    [Fact]
    public void HostOutsideDevelopmentWithOnlyTheKeyPathRefusesToStart()
    {
        using var certificate = TestCertificates.CreateRsa();
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production)
            .WithoutDataProtectionCertificate()
            .WithSetting(DataProtectionSettingKeys.KeyPath, certificate.KeyPath);

        StartupFailure.AssertOptionsInvalid(factory, DataProtectionSettingKeys.CertificateSection);
    }

    // Breaks when a certificate file that does not exist is ignored, or when the failure quotes the path (a path is deployment
    // detail the message must not leak): the setting is named, the path is not.
    [Fact]
    public void HostOutsideDevelopmentWithACertificateFileThatDoesNotExistRefusesToStartWithoutQuotingThePath()
    {
        var missing = Path.Combine(Path.GetTempPath(), MissingDirectory, "certificate.pem");
        using var certificate = TestCertificates.CreateRsa();
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production)
            .WithSetting(DataProtectionSettingKeys.CertificatePath, missing)
            .WithSetting(DataProtectionSettingKeys.KeyPath, certificate.KeyPath);

        StartupFailure.AssertOptionsInvalidWithout(factory, DataProtectionSettingKeys.CertificateSection, MissingDirectory);
    }

    // Breaks when files that are not PEM are accepted, or when the failure quotes their content or path.
    [Fact]
    public void HostOutsideDevelopmentWithCertificateFilesThatAreNotPemRefusesToStartWithoutQuotingThem()
    {
        using var garbage = TestCertificates.CreateGarbage();
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production).WithDataProtectionCertificate(garbage);

        StartupFailure.AssertOptionsInvalidWithout(
            factory, DataProtectionSettingKeys.CertificateSection, "this is not a certificate", "this is not a key", Path.GetDirectoryName(garbage.CertificatePath)!);
    }

    // Breaks when the minimum RSA size is not enforced (a key of 1024 or 1536 bits would protect the key ring).
    [Theory]
    [InlineData(1024)]
    [InlineData(1536)]
    public void HostOutsideDevelopmentWithAnRsaKeyUnder2048BitsRefusesToStart(int bits)
    {
        using var certificate = TestCertificates.CreateRsa(bits);
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production).WithDataProtectionCertificate(certificate);

        StartupFailure.AssertOptionsInvalid(factory, DataProtectionSettingKeys.CertificateSection);
    }

    // Breaks when a certificate whose key is not RSA is accepted (it cannot encrypt the key ring).
    [Fact]
    public void HostOutsideDevelopmentWithACertificateThatIsNotRsaRefusesToStart()
    {
        using var certificate = TestCertificates.CreateEllipticCurve();
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production).WithDataProtectionCertificate(certificate);

        StartupFailure.AssertOptionsInvalid(factory, DataProtectionSettingKeys.CertificateSection);
    }

    // Breaks when a previous certificate (decrypt only) is not validated like the current one: a rotation would start and then
    // fail at the first read of an old key.
    [Fact]
    public void HostOutsideDevelopmentWithAPreviousCertificateUnder2048BitsRefusesToStart()
    {
        using var weak = TestCertificates.CreateRsa(1024);
        using var factory = ApiHostFactory.ForEnvironment(Environments.Production).WithPreviousDataProtectionCertificate(0, weak);

        StartupFailure.AssertOptionsInvalid(factory, DataProtectionSettingKeys.Section);
    }

    // Breaks when the boundary moves up: an RSA certificate of exactly 2048 bits is valid, and the host protects and unprotects.
    [Theory]
    [MemberData(nameof(EnvironmentsOutsideDevelopment))]
    public void HostOutsideDevelopmentWithAnRsa2048CertificateStarts(string environment)
    {
        using var certificate = TestCertificates.CreateRsa(2048);
        using var factory = ApiHostFactory.ForEnvironment(environment).WithDataProtectionCertificate(certificate);

        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Pinotechrd.Tests.Settings");

        Assert.Equal("hello", protector.Unprotect(protector.Protect("hello")));
    }

    // Breaks when Development requires a certificate (a developer machine and the tests of every module would need one).
    [Fact]
    public void DevelopmentHostWithoutACertificateStarts()
    {
        using var factory = new ApiHostFactory().WithoutDataProtectionCertificate();

        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Pinotechrd.Tests.Settings");

        Assert.Equal("hello", protector.Unprotect(protector.Protect("hello")));
    }
}
