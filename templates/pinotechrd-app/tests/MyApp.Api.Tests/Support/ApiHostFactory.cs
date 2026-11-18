using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The real API host with the test-only endpoints of <see cref="ProblemFixtureEndpoint"/> added through the same
/// <see cref="IEndpoint"/> mechanism the application uses, so requests go through the production pipeline. The
/// environment is Development unless the factory is created with <see cref="ForEnvironment"/>. Configuration values
/// are supplied with <see cref="WithSetting"/> before the first request, and a test sets the remote address of a
/// request (the in-memory server gives none) with the <see cref="RemoteAddressHeader"/> request header.
/// </summary>
public sealed class ApiHostFactory : WebApplicationFactory<Program>
{
    /// <summary>Request header whose value becomes <c>Connection.RemoteIpAddress</c>, before the production pipeline runs.</summary>
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings = [];
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
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            // The validation source generator only sees the endpoints of the project that calls AddValidation, so
            // the test project registers its own endpoint types the same way the API does for its own.
            services.AddValidation();
            services.AddSingleton<IEndpoint, ProblemFixtureEndpoint>();
            // First, so it also runs ahead of the platform's own forwarded headers middleware.
            services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter, RemoteAddressStartupFilter>());
            foreach (var configure in _services)
            {
                configure(services);
            }
        });
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
