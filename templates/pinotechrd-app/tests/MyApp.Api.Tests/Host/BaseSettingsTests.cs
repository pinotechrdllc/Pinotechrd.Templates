using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.Api.Tests.Host;

// Chosen approach: load the appsettings.json found in the host content root through the configuration
// system and inspect the keys and values it yields. This checks the file the host really loads, without
// the environment overlays (appsettings.Development.json, environment variables) that would blur the result.
public class BaseSettingsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] SensitiveKeyFragments =
        ["connectionstring", "password", "pwd", "secret", "apikey", "privatekey", "accesskey", "token", "credential"];

    private static readonly string[] SensitiveValueFragments =
        ["password=", "pwd=", "user id=", "uid=", "host=", "server=", "-----begin"];

    private static List<string> Findings(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var findings = new List<string>();
        foreach (var (key, value) in settings)
        {
            var lowerKey = key.ToLowerInvariant();
            var segments = lowerKey.Split(':');
            if (SensitiveKeyFragments.Any(lowerKey.Contains) || segments.Contains("key") || segments.Contains("keys"))
            {
                findings.Add($"Key '{key}' looks like a secret or connection string and must not be in appsettings.json.");
            }
            else if (value is not null && SensitiveValueFragments.Any(f => value.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add($"Value of '{key}' looks like a connection string or credential.");
            }
        }

        return findings;
    }

    [Fact]
    public void BaseAppsettingsContainsNoConnectionStringsPasswordsKeysOrSecrets()
    {
        var contentRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        var path = Path.Combine(contentRoot, "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json not found in the host content root '{contentRoot}'.");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var findings = Findings(configuration.AsEnumerable());

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void SecretDetectorFlagsConnectionStringsPasswordsAndKeysButNotOrdinarySettings()
    {
        var findings = Findings(
        [
            new("ConnectionStrings:Default", "Host=db;Database=x"),
            new("Db:Password", "x"),
            new("Jwt:Key", "abc"),
            new("Other:Setting", "Server=a;User Id=b"),
            new("Logging:LogLevel:Default", "Information"),
            new("AllowedHosts", "*"),
        ]);

        Assert.Equal(4, findings.Count);
    }
}
