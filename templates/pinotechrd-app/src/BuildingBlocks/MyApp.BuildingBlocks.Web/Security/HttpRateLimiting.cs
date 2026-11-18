using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Web.Problems;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>
/// The global rate limit of the API, built on the platform limiter (<see cref="RateLimiterOptions"/>). Every request
/// counts against a fixed window per client, except on endpoints marked with <c>DisableRateLimiting()</c> (the health
/// probes). A rejected request answers 429 <see cref="HttpErrorCodes.RateLimited"/> as a problem details document with
/// a <c>Retry-After</c> header.
/// </summary>
/// <remarks>
/// A stricter limit for some endpoints (for example authentication) is added as a named policy on top of the global
/// one, partitioned the same way, and required by those endpoints; the rejection answer above applies to it too:
/// <code>
/// services.Configure&lt;RateLimiterOptions&gt;(o =&gt; o.AddPolicy("auth", context =&gt;
///     RateLimitPartition.GetFixedWindowLimiter(HttpRateLimiting.ClientPartitionKey(context), _ =&gt; new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) })));
/// routes.MapPost("/login", ...).RequireRateLimiting("auth");
/// </code>
/// </remarks>
public static class HttpRateLimiting
{
    private const string RetryAfterHeader = "Retry-After";

    private const string UnknownClient = "unknown";

    /// <summary>
    /// Returns the partition of a request: the client address after forwarded headers are applied, so behind a
    /// trusted proxy it is the real client. IPv4-mapped IPv6 addresses count as IPv4, and an IPv6 client is
    /// partitioned by its /64 network, the block a single subscriber usually gets, so rotating addresses inside it
    /// does not reset the limit. A request without an address (an in-memory test server) shares one partition.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The partition key.</returns>
    public static string ClientPartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return UnknownClient;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>Adds the limiter with the global fixed-window limit and the problem details rejection.</summary>
    internal static void AddGlobalLimit(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;
        });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<HttpRateLimitOptions>>((options, settings) =>
            {
                var limit = settings.Value;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(ClientPartitionKey(context), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit.PermitLimit,
                        Window = limit.Window,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            });
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        if (!context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfter = httpContext.RequestServices.GetRequiredService<IOptions<HttpRateLimitOptions>>().Value.Window;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers[RetryAfterHeader] = seconds.ToString(CultureInfo.InvariantCulture);
        await PipelineProblems.WriteAsync(httpContext, StatusCodes.Status429TooManyRequests, HttpErrorCodes.RateLimited).ConfigureAwait(false);
    }
}
