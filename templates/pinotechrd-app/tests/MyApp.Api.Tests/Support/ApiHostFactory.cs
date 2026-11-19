using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Infrastructure.Database;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The real API host with the test-only endpoints of <see cref="ProblemFixtureEndpoint"/> added through the same
/// <see cref="IEndpoint"/> mechanism the application uses, so requests go through the production pipeline (the API is
/// deny by default: the fixture endpoints declare their access and their permission is registered here). The
/// environment is Development unless the factory is created with <see cref="ForEnvironment"/>. Configuration values
/// are supplied with <see cref="WithSetting"/> before the first request, and a test sets the remote address of a
/// request (the in-memory server gives none) with the <see cref="RemoteAddressHeader"/> request header.
/// <para>
/// Database: a host that does not exercise the database gets <see cref="TestDatabase.PlaceholderConnectionString"/>
/// (valid, never opened) and a healthy stand-in for the readiness check named <see cref="DatabaseHealthCheck.Name"/>,
/// so the readiness tests of the rest of the application keep meaning "ready with its dependencies up" without a
/// container. The real check is kept, with whatever connection string the test chose, as soon as the test calls
/// <see cref="WithDatabase"/>, <see cref="WithoutDatabaseConnectionString"/> or sets the connection string itself.
/// </para>
/// </summary>
public sealed class ApiHostFactory : WebApplicationFactory<Program>
{
    /// <summary>Request header whose value becomes <c>Connection.RemoteIpAddress</c>, before the production pipeline runs.</summary>
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    private readonly string _environment;
    // The Access module needs its JWT settings to start: every host gets the fake ones of AccessJwt, which a test replaces
    // (or empties, to prove the start fails) with WithSetting.
    private readonly Dictionary<string, string?> _settings = new(AccessJwt.Settings)
    {
        [DatabaseRegistration.ConnectionStringKey] = TestDatabase.PlaceholderConnectionString,
    };

    private bool _realDatabaseCheck;
    private readonly Dictionary<string, string> _earlySettings = [];
    private readonly List<Action<IServiceCollection>> _services = [];

    public ApiHostFactory()
        : this(Environments.Development)
    {
    }

    private ApiHostFactory(string environment) => _environment = environment;

    /// <summary>Creates a host that runs in the given environment, for example <see cref="Environments.Production"/>.</summary>
    public static ApiHostFactory ForEnvironment(string environment) => new(environment);

    /// <summary>Sets a configuration value (for example <c>Http:RateLimiting:PermitLimit</c>) for this host only.</summary>
    public ApiHostFactory WithSetting(string key, string value)
    {
        _settings[key] = value;
        _realDatabaseCheck |= string.Equals(key, DatabaseRegistration.ConnectionStringKey, StringComparison.Ordinal);
        return this;
    }

    /// <summary>Uses <paramref name="connectionString"/> as the database and keeps the real readiness check.</summary>
    public ApiHostFactory WithDatabase(string connectionString) => WithSetting(DatabaseRegistration.ConnectionStringKey, connectionString);

    /// <summary>
    /// Leaves the connection string unset (even if the machine's environment sets one) and keeps the real readiness
    /// check, for the tests of the start-up failure.
    /// </summary>
    public ApiHostFactory WithoutDatabaseConnectionString()
    {
        _settings[DatabaseRegistration.ConnectionStringKey] = null;
        _realDatabaseCheck = true;
        return this;
    }

    /// <summary>
    /// Sets a configuration value that <c>Program.cs</c> can already read while it builds the host, before any service
    /// is registered (for example <c>Logging:LogLevel:Default</c> or <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>). A value from
    /// <see cref="WithSetting"/> is only added when the host is built, so code that reads the configuration eagerly does
    /// not see it. This one does not touch the process environment, so parallel hosts do not share it.
    /// </summary>
    public ApiHostFactory WithEarlySetting(string key, string value)
    {
        _earlySettings[key] = value;
        return this;
    }

    /// <summary>Adds services to this host only, for example an authentication scheme.</summary>
    public ApiHostFactory WithServices(Action<IServiceCollection> configure)
    {
        _services.Add(configure);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        foreach (var (key, value) in _earlySettings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            // The validation source generator only sees the endpoints of the project that calls AddValidation, so
            // the test project registers its own endpoint types the same way the API does for its own.
            services.AddTestAssemblyValidation();
            services.AddSingleton<IEndpoint, ProblemFixtureEndpoint>();
            // The permission of the fixture endpoint that requires one; a host that starts must know every permission its
            // endpoints require.
            services.AddPermissions(ProblemFixtureEndpoint.AdminPermission);
            if (!_realDatabaseCheck)
            {
                services.Configure<HealthCheckServiceOptions>(options =>
                {
                    foreach (var registration in options.Registrations.Where(r => r.Name == DatabaseHealthCheck.Name).ToList())
                    {
                        options.Registrations.Remove(registration);
                    }

                    options.Registrations.Add(new HealthCheckRegistration(
                        DatabaseHealthCheck.Name, _ => new HealthyStandIn(), failureStatus: null, tags: [HealthRegistration.ReadyTag]));
                });
            }

            // First, so it also runs ahead of the platform's own forwarded headers middleware.
            services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter, RemoteAddressStartupFilter>());
            foreach (var configure in _services)
            {
                configure(services);
            }
        });
    }

    // The database being up, for the hosts that do not test the database.
    private sealed class HealthyStandIn : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Healthy());
    }

    // Runs ahead of everything the API adds, like the address a real server would have set.
    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var value) && IPAddress.TryParse(value.ToString(), out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    return nextMiddleware(context);
                });
                next(app);
            };
    }
}
