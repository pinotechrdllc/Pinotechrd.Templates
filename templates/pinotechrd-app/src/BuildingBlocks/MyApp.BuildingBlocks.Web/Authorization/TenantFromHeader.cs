using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Tenancy;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// Endpoint metadata of an anonymous endpoint that takes its tenant from the <see cref="TenantHeader.Name"/> header.
/// Only <see cref="TenantHeader.TenantFromHeader{TBuilder}"/> creates it.
/// </summary>
public sealed class TenantFromHeaderMetadata
{
    /// <summary>The one instance; the marker carries no data.</summary>
    internal static TenantFromHeaderMetadata Instance { get; } = new();

    private TenantFromHeaderMetadata()
    {
    }
}

/// <summary>
/// The tenant of a request made before login (the login, refresh and logout endpoints), chosen by the client with the
/// <see cref="Name"/> header, which holds the tenant's slug. Only endpoints marked with
/// <see cref="TenantFromHeader{TBuilder}"/> read it; everywhere else the header is ignored and the tenant comes from the
/// token alone.
/// </summary>
public static class TenantHeader
{
    /// <summary>The request header that holds the tenant's slug.</summary>
    public const string Name = "X-Tenant";

    /// <summary>
    /// Marks the endpoint as one whose tenant comes from the <see cref="Name"/> header: the header is required (missing
    /// or sent more than once answers 400 <c>bad_request</c>), and the tenant it names is set for the request only when
    /// it is an active tenant. A value that is not a valid slug, an unknown slug and a tenant that is not active all
    /// leave the request without a tenant, the same way, so the endpoint answers with its usual failure and nobody learns
    /// which slugs exist. The endpoint must also be <c>AllowAnonymous()</c>, or the host does not start. A token sent
    /// with the header must be for the same tenant, or the request answers 401.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the endpoint convention builder.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="builder"/> is a route group.</exception>
    public static TBuilder TenantFromHeader<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder is RouteGroupBuilder)
        {
            throw new ArgumentException(
                "TenantFromHeader() is declared on each endpoint, not on a route group: an endpoint added to the group later would inherit it unseen.",
                nameof(builder));
        }

        builder.WithMetadata(TenantFromHeaderMetadata.Instance);
        return builder;
    }

    /// <summary>Whether the endpoint is marked with <see cref="TenantFromHeader{TBuilder}"/>.</summary>
    internal static bool IsMarked(Endpoint? endpoint) => endpoint?.Metadata.GetMetadata<TenantFromHeaderMetadata>() is not null;
}

/// <summary>
/// Sets the tenant of a request to an endpoint marked with <see cref="TenantHeader.TenantFromHeader{TBuilder}"/> from
/// the <see cref="TenantHeader.Name"/> header, through the setter the host gives. It runs at
/// <see cref="AuthenticatedRequestStepOrder.TenantSelection"/>, before the tenant from the token, which then accepts
/// only the same tenant. The slug is looked up with <see cref="ITenantCatalog"/>; without one registered the request
/// fails (500): the step never guesses a tenant.
/// </summary>
internal sealed class TenantFromHeaderStep(Action<HttpContext, Guid> setTenant) : IAuthenticatedRequestStep
{
    public int Order => AuthenticatedRequestStepOrder.TenantSelection;

    public async ValueTask<IResult?> InvokeAsync(HttpContext context)
    {
        if (!TenantHeader.IsMarked(context.GetEndpoint()))
        {
            return null;
        }

        var values = context.Request.Headers[TenantHeader.Name];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return TypedResults.BadRequest();
        }

        var catalog = context.RequestServices.GetService<ITenantCatalog>()
            ?? throw new InvalidOperationException(
                $"An endpoint reads its tenant from the {TenantHeader.Name} header, but no {nameof(ITenantCatalog)} is registered to look it up.");
        if (await catalog.FindActiveTenantIdAsync(values[0]!, context.RequestAborted).ConfigureAwait(false) is { } tenantId)
        {
            setTenant(context, tenantId);
            return null;
        }

        // A token sent with a slug that names no active tenant gets the same 401 as a token for another active tenant
        // (refused by the tenant from the token): otherwise the answer would tell a token holder which slugs are active.
        return context.User.Identity?.IsAuthenticated == true ? TypedResults.Challenge() : null;
    }
}
