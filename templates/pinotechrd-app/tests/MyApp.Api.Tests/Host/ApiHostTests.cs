using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MyApp.Api.Tests.Host;

public class ApiHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task UnknownRouteAnswers404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/this-route-does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
