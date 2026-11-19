using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using MyApp.Api.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.Api.Tests.Host;

// Chosen approach: load the appsettings.json found in the host content root through the configuration
// system and inspect the keys and values it yields. This checks the file the host really loads, without
// the environment overlays (appsettings.Development.json, environment variables) that would blur the result.
public partial class BaseSettingsTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private static readonly string[] SensitiveKeyFragments =
        ["connectionstring", "password", "pwd", "secret", "apikey", "privatekey", "accesskey", "token", "credential"];

    private static readonly string[] SensitiveValueFragments =
        ["password=", "pwd=", "user id=", "uid=", "host=", "server=", "data source=", "accountkey=", "-----begin"];

    [GeneratedRegex(@"://[^/\s@]*@")]
    private static partial Regex UrlCredentials();

    // Lifetimes of refresh tokens, whose names contain "token" but which hold no secret: allowed only as the section
    // itself or with a value that is a number or a duration, so a secret put under one of these names is still found.
    private static readonly string[] TokenLifetimeKeys =
        ["access:refreshtokens", "access:refreshtokens:absolutelifetime", "access:jwt:refreshtokenexpirydays"];

    private static bool IsTokenLifetime(string lowerKey, string? value) =>
        TokenLifetimeKeys.Contains(lowerKey, StringComparer.Ordinal)
        && (value is null || int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _)
            || TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _));

    private static List<string> Findings(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var findings = new List<string>();
        foreach (var (key, value) in settings)
        {
            var lowerKey = key.ToLowerInvariant();
            if (IsTokenLifetime(lowerKey, value))
            {
                continue;
            }

            var segments = lowerKey.Split(':');
            if (SensitiveKeyFragments.Any(lowerKey.Contains) || segments.Any(x => x.EndsWith("key", StringComparison.Ordinal) || x.EndsWith("keys", StringComparison.Ordinal)))
            {
                findings.Add($"Key '{key}' looks like a secret or connection string and must not be in appsettings.json.");
            }
            else if (value is not null && (SensitiveValueFragments.Any(f => value.Contains(f, StringComparison.OrdinalIgnoreCase)) || UrlCredentials().IsMatch(value)))
            {
                findings.Add($"Value of '{key}' looks like a connection string or credential.");
            }
        }

        return findings;
    }

    [Fact]
    public void ShippedAppsettingsFilesContainNoConnectionStringsPasswordsKeysOrSecrets()
    {
        var contentRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        var files = Directory.GetFiles(contentRoot, "appsettings*.json")
            .Where(f => !f.EndsWith(".local.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(files.Count > 0, $"No appsettings*.json found in the host content root '{contentRoot}'.");

        var findings = new List<string>();
        foreach (var file in files)
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(file, optional: false).Build();
            findings.AddRange(Findings(configuration.AsEnumerable()).Select(f => $"{Path.GetFileName(file)}: {f}"));
        }

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Theory]
    [InlineData("ConnectionStrings:Default", "x")]
    [InlineData("Db:Password", "x")]
    [InlineData("Jwt:Key", "abc")]
    [InlineData("Jwt:SigningKey", "abc")]
    [InlineData("Storage:AccountKey", "abc")]
    [InlineData("EncryptionKey", "abc")]
    [InlineData("Client:ClientKey", "abc")]
    [InlineData("Other:Setting", "Server=a;User Id=b")]
    [InlineData("Other:Setting", "Host=db;Database=x")]
    [InlineData("Other:Setting", "Data Source=db.sqlite")]
    [InlineData("Other:Setting", "AccountKey=abc")]
    [InlineData("Other:Setting", "postgres://user:pass@host/db")]
    public void SecretDetectorFlagsSecretLookingSettings(string key, string value)
    {
        Assert.Single(Findings([new(key, value)]));
    }

    [Theory]
    [InlineData("Logging:LogLevel:Default", "Information")]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore", "Warning")]
    [InlineData("AllowedHosts", "*")]
    [InlineData("Service:BaseUrl", "https://example.com/api")]
    public void SecretDetectorIgnoresOrdinarySettings(string key, string value)
    {
        Assert.Empty(Findings([new(key, value)]));
    }
}
