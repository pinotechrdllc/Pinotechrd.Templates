using System.Net;
using System.Reflection;
using System.Text.Json;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Host;

public class VersionEndpointTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    [Fact]
    public async Task VersionAnswers200WithTheInformationalVersionOfTheApiAssembly()
    {
        var expected = ApiAssembly.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/version", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("json", response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var version = json.RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(expected, version);
    }

    [Fact]
    public async Task VersionIsNotReachableOutsideTheVersionedPrefix()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/version", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
