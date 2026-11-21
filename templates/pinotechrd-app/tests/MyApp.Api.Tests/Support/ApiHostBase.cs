using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The defaults every test host built on the real <c>Program</c> needs: the fake JWT settings of <see cref="AccessJwt"/>
/// (the Access module does not start without them) and the refresh token purge turned off. A derived host sets its
/// environment and its own settings in <see cref="ConfigureHost"/> and by changing <see cref="Settings"/>.
/// </summary>
public abstract class ApiHostBase : WebApplicationFactory<Program>
{
    /// <summary>The setting that turns the refresh token purge service on or off; false in every test host unless a test sets it.</summary>
    public const string PurgeEnabledKey = "Access:RefreshTokens:Purge:Enabled";

    /// <summary>
    /// The configuration values of this host, added to its configuration when it is built; change them before the first request.
    /// A null value leaves the key unset even if the machine's environment sets it.
    /// </summary>
    protected Dictionary<string, string?> Settings { get; } = new(AccessJwt.Settings)
    {
        // The purge service runs in every API host, its first round after a random wait of up to a minute: off in test hosts,
        // so no test meets a purge it did not start; the purge service tests turn it on with a setting.
        [PurgeEnabledKey] = "false",
    };

    /// <summary>Sets the environment and whatever else the derived host needs; runs before <see cref="Settings"/> is applied.</summary>
    protected abstract void ConfigureHost(IWebHostBuilder builder);

    protected sealed override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ConfigureHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
    }
}
