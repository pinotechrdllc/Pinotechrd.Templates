using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web.OpenApi;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// Stops the host before it serves anything when the access rules are broken, listing every offender:
/// <list type="bullet">
/// <item><description>A routed endpoint, other than the <see cref="ExemptRoutes"/>, that declares none or more than one
/// of <c>RequirePermission</c>, <c>AuthenticatedOnly</c> and <c>AllowAnonymous</c>; whose permission metadata has no
/// matching permission requirement in its authorization data; that requires a permission missing from the
/// <see cref="PermissionCatalogue"/>; or that is short-circuited (<c>ShortCircuit()</c>, which skips authorization)
/// without being anonymous, or while carrying authorization, CORS or antiforgery metadata (routing would answer
/// 500); or whose authorization names its own <c>AuthenticationSchemes</c> (endpoints use the default scheme only); or
/// that is marked <c>TenantFromHeader()</c> without being anonymous.</description></item>
/// <item><description>Two registered permissions that differ only by case; two permission checkers for one module, or a
/// checker registered other than with <see cref="PermissionRegistration.AddPermissionChecker{TChecker}"/>.</description></item>
/// <item><description>An authentication setup where a challenge or a forbid has no scheme (it would answer 500), or
/// where another scheme is registered while the placeholder is still the default scheme to authenticate or challenge
/// (the real scheme would never run).</description></item>
/// </list>
/// It runs once the request pipeline is built and every endpoint is mapped, before the server listens. The build-time
/// contract generation builds the pipeline too, so a broken rule also stops the generation: the contract never
/// describes an endpoint that could not start.
/// </summary>
internal sealed class AccessDeclarationCheck : IStartupFilter
{
    /// <summary>
    /// The only routed endpoints that need no declaration: the health probes (anonymous by construction, see
    /// <see cref="HealthRegistration.MapHealth"/>) and the OpenAPI document (anonymous, see
    /// <see cref="OpenApiRegistration.MapOpenApiDocument"/>). Compared with the route template, leading slash included.
    /// </summary>
    internal static readonly IReadOnlyList<string> ExemptRoutes =
        [HealthRegistration.LivePath, HealthRegistration.ReadyPath, "/openapi/{documentName}.json"];

    private const string ShortCircuitMetadataType = "Microsoft.AspNetCore.Routing.ShortCircuit.ShortCircuitMetadata";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            next(app);
            Verify(app.ApplicationServices);
        };

    private static void Verify(IServiceProvider services)
    {
        var offenders = PermissionOffenders(services)
            .Concat(EndpointOffenders(services.GetRequiredService<EndpointDataSource>(), services.GetRequiredService<PermissionCatalogue>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value))
            .Concat(SchemeOffenders(services.GetRequiredService<IAuthenticationSchemeProvider>()))
            .ToList();
        if (offenders.Count == 0)
        {
            return;
        }

        var message = new StringBuilder()
            .Append("The API does not start: every routed endpoint except the health probes and the OpenAPI document declares exactly one of ")
            .Append("RequirePermission(\"Module.Resource.Action\"), AuthenticatedOnly() or AllowAnonymous() on itself, ")
            .Append("every permission it requires is registered once with AddPermissions, and authentication has a scheme for every outcome.");
        foreach (var offender in offenders)
        {
            message.AppendLine().Append("- ").Append(offender);
        }

        throw new InvalidOperationException(message.ToString());
    }

    private static IEnumerable<string> PermissionOffenders(IServiceProvider services)
    {
        var catalogue = services.GetRequiredService<PermissionCatalogue>();
        foreach (var group in catalogue.Permissions.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            yield return $"The permissions {string.Join(" and ", group.Select(p => $"'{p}'"))} differ only by case; register one of them.";
        }

        foreach (var problem in services.GetRequiredService<PermissionCheckerMap>().Problems)
        {
            yield return problem;
        }
    }

    private static IEnumerable<string> EndpointOffenders(EndpointDataSource endpoints, PermissionCatalogue catalogue, AuthorizationOptions authorization)
    {
        if (authorization.FallbackPolicy?.AuthenticationSchemes.Count > 0)
        {
            yield return "The fallback authorization policy names its own AuthenticationSchemes; endpoints use the default scheme only.";
        }

        foreach (var endpoint in endpoints.Endpoints.OfType<RouteEndpoint>().Where(e => !IsExempt(e)))
        {
            var name = NameOf(endpoint);
            var metadata = endpoint.Metadata;
            var anonymous = metadata.GetMetadata<IAllowAnonymous>() is not null;
            if (IsShortCircuited(endpoint) && !anonymous)
            {
                yield return $"{name} is short-circuited (ShortCircuit()), which skips authorization; only an AllowAnonymous() endpoint may be.";
            }
            else if (IsShortCircuited(endpoint) && CarriesMiddlewareMetadata(endpoint))
            {
                yield return $"{name} is short-circuited (ShortCircuit()) but carries authorization, CORS or antiforgery metadata (as every endpoint of the API group does), which routing refuses with a 500 on the first request; map it outside the API group.";
            }

            if (TenantHeader.IsMarked(endpoint) && !anonymous)
            {
                yield return $"{name} takes its tenant from the {TenantHeader.Name} header (TenantFromHeader()) without AllowAnonymous(); only an anonymous endpoint, used before login, chooses its tenant, every other one takes it from the token.";
            }

            if (NamesSchemes(endpoint, authorization))
            {
                yield return $"{name} names its own AuthenticationSchemes in its authorization; authorization would authenticate again with them and replace the user the identity steps read from the default scheme, so endpoints use the default scheme only.";
            }

            var declarations = DeclarationsOf(endpoint);
            if (declarations.Count == 0)
            {
                yield return $"{name} declares no access.";
                continue;
            }

            if (declarations.Count > 1)
            {
                yield return $"{name} declares {declarations.Count} accesses ({string.Join(", ", declarations)}); keep one.";
                continue;
            }

            if (metadata.GetMetadata<PermissionMetadata>() is not { } permission)
            {
                continue;
            }

            if (!IsEnforced(endpoint, permission.Permission))
            {
                yield return $"{name} carries the permission '{permission.Permission}' without its authorization requirement; declare it with RequirePermission.";
            }
            else if (!catalogue.Contains(permission.Permission))
            {
                yield return $"{name} requires the permission '{permission.Permission}', which is not registered with AddPermissions.";
            }
        }
    }

    private static IEnumerable<string> SchemeOffenders(IAuthenticationSchemeProvider schemes)
    {
        // The platform's provider answers from its options without waiting.
        var challenge = schemes.GetDefaultChallengeSchemeAsync().GetAwaiter().GetResult();
        var forbid = schemes.GetDefaultForbidSchemeAsync().GetAwaiter().GetResult();
        var authenticate = schemes.GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult();
        var others = schemes.GetAllSchemesAsync().GetAwaiter().GetResult()
            .Where(s => s.Name != ApiAuthorizationRegistration.PlaceholderScheme)
            .Select(s => s.Name)
            .ToList();

        if (challenge is null || forbid is null)
        {
            yield return "Authentication has no default challenge or forbid scheme, so a 401 or 403 would fail with 500: set DefaultScheme, or DefaultChallengeScheme (and DefaultForbidScheme if it differs), with AddAuthentication(options => ...).";
        }

        var placeholderDefault = authenticate?.Name == ApiAuthorizationRegistration.PlaceholderScheme
            || challenge?.Name == ApiAuthorizationRegistration.PlaceholderScheme;
        if (others.Count > 0 && placeholderDefault)
        {
            yield return $"The scheme(s) {string.Join(", ", others.Select(o => $"'{o}'"))} are registered, but the placeholder '{ApiAuthorizationRegistration.PlaceholderScheme}' is still the default to authenticate or challenge, so they never run: register the real scheme as the default, AddAuthentication(\"{others[0]}\"), or set DefaultAuthenticateScheme and DefaultChallengeScheme.";
        }
    }

    // RequirePermission adds the metadata and, in the same call, a policy with the requirement; metadata added by hand
    // would show in the contract and the check without anything enforcing it.
    private static bool IsEnforced(Endpoint endpoint, string permission) =>
        endpoint.Metadata.OfType<AuthorizationPolicy>()
            .SelectMany(p => p.Requirements)
            .OfType<PermissionRequirement>()
            .Any(r => string.Equals(r.Permission, permission, StringComparison.Ordinal));

    // Authorization authenticates again with the schemes an endpoint names and replaces the principal, after the
    // identity steps set the user and the tenant from the default scheme: the two could then differ.
    // A named policy (RequireAuthorization("Name")) carries its schemes in the options, not in the metadata, and an
    // IAuthorizeData without a policy name uses the default policy, so both are resolved before counting schemes.
    private static bool NamesSchemes(Endpoint endpoint, AuthorizationOptions options) =>
        endpoint.Metadata.Any(m => m switch
        {
            AuthorizationPolicy policy => policy.AuthenticationSchemes.Count > 0,
            IAuthorizeData data => !string.IsNullOrWhiteSpace(data.AuthenticationSchemes)
                || (string.IsNullOrEmpty(data.Policy) ? options.DefaultPolicy : options.GetPolicy(data.Policy))?.AuthenticationSchemes.Count > 0,
            _ => false,
        });

    // ShortCircuit() adds a metadata type the platform keeps internal, so it is recognized by its full name.
    private static bool IsShortCircuited(Endpoint endpoint) =>
        endpoint.Metadata.Any(m => m.GetType().FullName == ShortCircuitMetadataType);

    // Routing throws on a short-circuited endpoint that carries metadata only a skipped middleware would honour.
    private static bool CarriesMiddlewareMetadata(Endpoint endpoint) =>
        endpoint.Metadata.Any(m => m is IAuthorizeData or IAuthorizationRequirementData or AuthorizationPolicy
            or Microsoft.AspNetCore.Cors.Infrastructure.ICorsMetadata or Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata);

    private static bool IsExempt(RouteEndpoint endpoint) =>
        ExemptRoutes.Contains(RouteOf(endpoint), StringComparer.OrdinalIgnoreCase);

    // Routing ignores the leading slash ("api/v1/x" serves /api/v1/x), so the comparison does too.
    private static string RouteOf(RouteEndpoint endpoint) => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');

    private static List<string> DeclarationsOf(Endpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        var declarations = new List<string>();
        declarations.AddRange(metadata.OfType<PermissionMetadata>().Select(p => $"RequirePermission(\"{p.Permission}\")"));
        declarations.AddRange(metadata.OfType<AuthenticatedOnlyMetadata>().Select(_ => "AuthenticatedOnly()"));
        declarations.AddRange(metadata.OfType<IAllowAnonymous>().Select(_ => "AllowAnonymous()"));
        return declarations;
    }

    // The methods and the route template, plus the endpoint name when it has one: enough to find it in the code.
    private static string NameOf(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } list
            ? string.Join(",", list)
            : "ANY";
        var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        return $"{methods} {RouteOf(endpoint)}" + (name is null ? string.Empty : $" ({name})");
    }
}
