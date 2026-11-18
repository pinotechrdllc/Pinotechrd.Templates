using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Host;

public class ApiHostTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    [Fact]
    public async Task UnknownRouteAnswers404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/this-route-does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
