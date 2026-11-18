using System.Net;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Host;

// The probes only read: GET and HEAD answer, every other method answers the same 405 problem as any other route.
public class HealthProbeMethodTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    public static TheoryData<string, string> ReadMethods => Combine("GET", "HEAD");

    public static TheoryData<string, string> WriteMethods => Combine("POST", "PUT", "DELETE", "PATCH");

    // Breaks when restricting the methods also removes GET or HEAD from a probe.
    [Theory]
    [MemberData(nameof(ReadMethods))]
    public async Task GetAndHeadAnswer200(string path, string method)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when a probe answers a write method with anything but the 405 problem: 200, an empty 405 or another code.
    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task WriteMethodsAnswer405MethodNotAllowedProblem(string path, string method)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 405, "method_not_allowed");
    }

    // Breaks when the 405 does not tell the client which methods the probe accepts.
    [Theory]
    [MemberData(nameof(WriteMethods))]
    public async Task WriteMethodsAnswerAnAllowHeaderListingGetAndHead(string path, string method)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var allow = response.Content.Headers.Allow.ToList();
        if (response.Headers.TryGetValues("Allow", out var values))
        {
            allow.AddRange(values.SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries)));
        }

        Assert.Contains("GET", allow);
        Assert.Contains("HEAD", allow);
        Assert.DoesNotContain(method, allow);
    }

    // Breaks when a probe appears in the served API document, as the restriction on methods could make it do.
    [Fact]
    public async Task ProbesStayOutOfTheServedOpenApiDocument()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(HealthRegistration.LivePath, text, StringComparison.Ordinal);
        Assert.DoesNotContain(HealthRegistration.ReadyPath, text, StringComparison.Ordinal);
    }

    // Breaks when the probes fall under the rate limit once their methods are restricted: with one permit, the first
    // requests would pass and the later ones answer 429.
    [Theory]
    [MemberData(nameof(ReadMethods))]
    public async Task ProbesAreNotRateLimitedWithATinyPermitLimit(string path, string method)
    {
        using var limited = new ApiHostFactory()
            .WithSetting(HttpSettingKeys.PermitLimit, "1")
            .WithSetting(HttpSettingKeys.Window, "01:00:00");
        using var client = limited.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add(ApiHostFactory.RemoteAddressHeader, "10.9.0.1");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static TheoryData<string, string> Combine(params string[] methods)
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { HealthRegistration.LivePath, HealthRegistration.ReadyPath })
        {
            foreach (var method in methods)
            {
                data.Add(path, method);
            }
        }

        return data;
    }
}
