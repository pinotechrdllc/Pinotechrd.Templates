using MyApp.Api.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Contract;

// The order of the API document follows the order of mapping, so it must not depend on how the code is laid out.
public class EndpointOrderTests
{
    private const string Prefix = "/api/v1/order-fixtures/";

    // Breaks when endpoints are mapped in declaration (compile) order instead of by the full name of their class.
    [Fact]
    public async Task EndpointsAreMappedInOrdinalOrderOfTheirTypeFullNames()
    {
        var types = typeof(OrderFixtureZuluEndpoint).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace == typeof(OrderFixtureZuluEndpoint).Namespace && t.Name.StartsWith("OrderFixture", StringComparison.Ordinal))
            .ToArray();
        Assert.True(types.Length >= 3, "The order fixtures were not found.");
        var expected = types
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => Prefix + t.Name["OrderFixture".Length..^"Endpoint".Length].ToLowerInvariant())
            .ToArray();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiEndpoints(typeof(OrderFixtureZuluEndpoint).Assembly);
        await using var app = builder.Build();
        app.MapApiEndpoints();

        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Where(r => r.StartsWith(Prefix, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(expected, actual);
    }
}
