using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The real API host with the test-only endpoints of <see cref="ProblemFixtureEndpoint"/> added through the same
/// <see cref="IEndpoint"/> mechanism the application uses, so requests go through the production pipeline. The
/// environment is Development unless the factory is created with <see cref="ForEnvironment"/>.
/// </summary>
public sealed class ApiHostFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;

    public ApiHostFactory()
        : this(Environments.Development)
    {
    }

    private ApiHostFactory(string environment) => _environment = environment;

    /// <summary>Creates a host that runs in the given environment, for example <see cref="Environments.Production"/>.</summary>
    public static ApiHostFactory ForEnvironment(string environment) => new(environment);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureTestServices(services =>
        {
            // The validation source generator only sees the endpoints of the project that calls AddValidation, so
            // the test project registers its own endpoint types the same way the API does for its own.
            services.AddValidation();
            services.AddSingleton<IEndpoint, ProblemFixtureEndpoint>();
        });
    }
}
