using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Security;
using MyApp.Modules.Access.Application.Authentication;

namespace MyApp.Modules.Access.Endpoints.Auth;

/// <summary>
/// Refuses, with 403 <c>csrf_rejected</c>, a browser request to an endpoint that works on the refresh cookie or sets it
/// (login, refresh, logout) when it comes from a page the API does not trust. <c>SameSite=Strict</c> already keeps the
/// cookie off cross-site requests; this also covers a login forced from another site and browsers without SameSite.
/// <list type="bullet">
/// <item><c>Origin</c>, when sent, must be the request's own origin or one of <c>Http:Cors:AllowedOrigins</c>
/// (<c>null</c> included: it is never trusted).</item>
/// <item><c>Sec-Fetch-Site</c>, when sent, must be <c>same-origin</c> or <c>none</c> (typed by the user), unless the
/// <c>Origin</c> is one of <c>Http:Cors:AllowedOrigins</c>, which a browser reports as <c>same-site</c> or
/// <c>cross-site</c>.</item>
/// </list>
/// A request with neither header is not from a browser and passes: it cannot carry a browser's cookie unasked.
/// </summary>
internal sealed class CsrfFilter : IEndpointFilter
{
    private const string FetchSiteHeader = "Sec-Fetch-Site";

    private static readonly Error Rejected =
        Error.Forbidden(AuthErrorCodes.CsrfRejected, "The request comes from a site that is not allowed to call this endpoint.");

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        return IsAllowed(context.HttpContext) ? await next(context).ConfigureAwait(false) : Rejected.ToProblem();
    }

    private static bool IsAllowed(HttpContext context)
    {
        var request = context.Request;
        var origins = request.Headers.Origin;
        var fetchSite = request.Headers[FetchSiteHeader];
        if (origins.Count > 1 || fetchSite.Count > 1)
        {
            return false;
        }

        var listed = false;
        if (origins.Count == 1)
        {
            var origin = origins[0] ?? string.Empty;
            listed = context.RequestServices.GetRequiredService<IOptions<HttpCorsOptions>>().Value.AllowedOrigins
                .Contains(origin, StringComparer.OrdinalIgnoreCase);
            if (!listed && !string.Equals(origin, OwnOrigin(request), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return fetchSite.Count == 0 || listed || fetchSite[0] is "same-origin" or "none";
    }

    // The origin a page served by this host sends: scheme (after forwarded headers) and host as the client wrote it.
    private static string OwnOrigin(HttpRequest request) => request.Scheme + "://" + request.Host.Value;
}
