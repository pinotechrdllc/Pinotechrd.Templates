using System.Net;
using MyApp.Api.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Host;

public class HealthEndpointTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private static readonly string[] ReadyTags = [HealthRegistration.ReadyTag];

    private static readonly string[] OtherTags = ["other"];

    [Fact]
    public async Task LiveAnswers200WhenNoReadinessCheckIsRegistered()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, HealthRegistration.LivePath));
    }

    [Fact]
    public async Task LiveAnswers200EvenWhenAReadyCheckIsUnhealthy()
    {
        using var client = ClientWith(("db", HealthStatus.Unhealthy, ReadyTags));

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, HealthRegistration.LivePath));
    }

    [Fact]
    public async Task ReadyAnswers503WhenAReadyCheckIsUnhealthy()
    {
        using var client = ClientWith(("db", HealthStatus.Unhealthy, ReadyTags));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusOf(client, HealthRegistration.ReadyPath));
    }

    [Fact]
    public async Task ReadyAnswers200WhenAllReadyChecksAreHealthy()
    {
        using var client = ClientWith(("db", HealthStatus.Healthy, ReadyTags), ("cache", HealthStatus.Healthy, ReadyTags));

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, HealthRegistration.ReadyPath));
    }

    [Fact]
    public async Task ReadyAnswers503WhenOnlyOneOfSeveralReadyChecksIsUnhealthy()
    {
        using var client = ClientWith(("db", HealthStatus.Healthy, ReadyTags), ("cache", HealthStatus.Unhealthy, ReadyTags));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusOf(client, HealthRegistration.ReadyPath));
    }

    [Fact]
    public async Task ReadyIgnoresAnUnhealthyCheckWithoutTheReadyTag()
    {
        using var client = ClientWith(("db", HealthStatus.Healthy, ReadyTags), ("noise", HealthStatus.Unhealthy, OtherTags));

        Assert.Equal(HttpStatusCode.OK, await StatusOf(client, HealthRegistration.ReadyPath));
    }

    [Fact]
    public async Task HealthEndpointsAreNotMappedUnderTheVersionedPrefix()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, EndpointRegistration.RoutePrefix + HealthRegistration.LivePath));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(client, EndpointRegistration.RoutePrefix + HealthRegistration.ReadyPath));
    }

    [Fact]
    public void HealthEndpointsAreExcludedFromApiDescriptionAndTheVersionEndpointIsNot()
    {
        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        foreach (var path in new[] { HealthRegistration.LivePath, HealthRegistration.ReadyPath })
        {
            var health = Assert.Single(endpoints, e => e.RoutePattern.RawText == path);
            Assert.Contains(health.Metadata.GetOrderedMetadata<IExcludeFromDescriptionMetadata>(), m => m.ExcludeFromDescription);
        }

        var version = Assert.Single(endpoints, e => e.RoutePattern.RawText == EndpointRegistration.RoutePrefix + "/version");
        Assert.DoesNotContain(version.Metadata.GetOrderedMetadata<IExcludeFromDescriptionMetadata>(), m => m.ExcludeFromDescription);
        Assert.False(string.IsNullOrWhiteSpace(version.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName));
    }

    private static async Task<HttpStatusCode> StatusOf(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private HttpClient ClientWith(params (string Name, HealthStatus Status, string[] Tags)[] checks) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var health = services.AddHealthChecks();
            foreach (var (name, status, tags) in checks)
            {
                health.AddCheck(name, () => new HealthCheckResult(status), tags);
            }
        })).CreateClient();
}
