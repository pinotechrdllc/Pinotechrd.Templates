using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// A test-only route that makes one outgoing <see cref="HttpClient"/> call whose address and headers hold secrets, so a
/// client span exists to be inspected. The connection is refused by the handler itself: nothing leaves the process.
/// </summary>
internal sealed class OutgoingCallEndpoint : IEndpoint
{
    public const string Route = EndpointRegistration.RoutePrefix + "/outgoing-call-fixture";

    public const string TargetHost = "downstream.example.test";

    public const string TargetPath = "/orders/42";

    public const string UserInfoSecret = "s3cr3t-outgoing-userinfo";

    public const string QuerySecret = "s3cr3t-outgoing-query";

    public const string HeaderSecret = "s3cr3t-outgoing-header";

    public static string[] Secrets => [UserInfoSecret, QuerySecret, HeaderSecret];

    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapGet("/outgoing-call-fixture", async () =>
        {
            using var handler = new SocketsHttpHandler { ConnectCallback = (_, _) => throw new IOException("Connection blocked by the test.") };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://client:{UserInfoSecret}@{TargetHost}{TargetPath}?token={QuerySecret}");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + HeaderSecret);
            try
            {
                using var response = await client.SendAsync(request);
            }
            catch (HttpRequestException)
            {
                // Expected: the handler refuses to connect.
            }

            return Results.Ok();
        });
}
