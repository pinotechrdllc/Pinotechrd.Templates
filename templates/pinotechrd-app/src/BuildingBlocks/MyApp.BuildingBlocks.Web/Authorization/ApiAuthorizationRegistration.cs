using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Web.Telemetry;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// Deny by default for the API, and the hooks the security module plugs into. It uses no security library and ships
/// no way to log in: until a real authentication scheme is registered, every endpoint that is not anonymous answers
/// 401.
/// </summary>
public static class ApiAuthorizationRegistration
{
    /// <summary>
    /// The name of the scheme registered until a real one replaces it: it never authenticates and challenges with 401
    /// and <c>WWW-Authenticate: Bearer</c>.
    /// </summary>
    public const string PlaceholderScheme = "Placeholder";

    /// <summary>
    /// Adds authorization with a fallback policy that requires an authenticated user, so any routed endpoint without its
    /// own authorization metadata is closed (an unknown route still answers 404 and a wrong method 405); the start-time
    /// check of the access rules (see the remarks of <c>EndpointAccess</c>); the
    /// <see cref="PermissionCatalogue"/>; the handler of <see cref="EndpointAccess.RequirePermission{TBuilder}"/>, which
    /// asks the checker of the permission's module (<see cref="PermissionRegistration.AddPermissionChecker{TChecker}"/>;
    /// a module without one is denied, so with none registered every permission is denied); the
    /// scoped <see cref="ICurrentUser"/>; the built-in <see cref="IAuthenticatedRequestStep"/> implementations that set
    /// the user and record the identity in the telemetry; the settings <see cref="ApiIdentityOptions"/>, validated at
    /// start; the start-time check of the access declarations; and the placeholder authentication scheme
    /// <see cref="PlaceholderScheme"/>, the default scheme only while no other default (scheme, authenticate scheme or
    /// challenge scheme) is configured. <see cref="ApiPipeline.UseApiPipeline"/> requires this call.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ApiIdentityOptions>()
            .BindConfiguration(ApiIdentityOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddAuthenticationCore, not AddAuthentication: the latter also adds Data Protection, whose hosted service creates
        // a key ring at start and, on Linux with no key encryption configured, logs a warning. The placeholder needs no
        // Data Protection; the real scheme's AddAuthentication brings it, together with its key storage.
        services.AddAuthenticationCore(options => options.AddScheme<PlaceholderAuthenticationHandler>(PlaceholderScheme, displayName: null));
        services.AddWebEncoders();
        services.TryAddTransient<PlaceholderAuthenticationHandler>();
        services.AddOptions<AuthenticationSchemeOptions>(PlaceholderScheme);
        services.PostConfigure<AuthenticationOptions>(options =>
        {
            if (options.DefaultScheme is null && options.DefaultAuthenticateScheme is null && options.DefaultChallengeScheme is null)
            {
                options.DefaultScheme = PlaceholderScheme;
            }
        });

        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, RoutedOnlyAuthorizationResultHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, PermissionAuthorizationHandler>());
        services.AddPermissions();

        services.TryAddScoped<CurrentUser>();
        services.TryAddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUser>());
        services.AddAuthenticatedRequestStep<CurrentUserStep>();
        services.AddAuthenticatedRequestStep<RequestIdentityTelemetry>();

        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, AccessDeclarationCheck>());
        services.TryAddSingleton<ApiAuthorizationMarker>();
        return services;
    }

    /// <summary>
    /// Sets the tenant of each authenticated request from the tenant claim of its principal
    /// (<see cref="ApiIdentityOptions.TenantIdClaimType"/>, a GUID), never from a header, the query string or the body.
    /// An authenticated principal without one valid tenant claim is answered 401 <c>unauthorized</c> on an endpoint that
    /// is not anonymous. The web building block cannot set the tenant itself (only the host and the infrastructure may),
    /// so the host gives <paramref name="setTenant"/>, which receives the request and the tenant identifier.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="setTenant">Sets the tenant of the request's scope, once.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTenantFromToken(this IServiceCollection services, Action<HttpContext, Guid> setTenant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(setTenant);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthenticatedRequestStep, TenantFromTokenStep>(
            provider => ActivatorUtilities.CreateInstance<TenantFromTokenStep>(provider, setTenant)));
        return services;
    }

    /// <summary>
    /// Sets the tenant of each request to an endpoint marked with <see cref="TenantHeader.TenantFromHeader{TBuilder}"/>
    /// from the <see cref="TenantHeader.Name"/> header, when it names an active tenant of the
    /// <see cref="ITenantCatalog"/> (see the marker for the answers). Other endpoints never read the header. Like
    /// <see cref="AddTenantFromToken"/>, only the host sets the tenant, through <paramref name="setTenant"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="setTenant">Sets the tenant of the request's scope, once.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTenantFromHeader(this IServiceCollection services, Action<HttpContext, Guid> setTenant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(setTenant);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthenticatedRequestStep, TenantFromHeaderStep>(
            _ => new TenantFromHeaderStep(setTenant)));
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TStep"/> as a singleton <see cref="IAuthenticatedRequestStep"/>, run after
    /// authentication and before authorization in the order of its <see cref="IAuthenticatedRequestStep.Order"/>. A
    /// step registered twice runs once.
    /// </summary>
    /// <typeparam name="TStep">The step.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddAuthenticatedRequestStep<TStep>(this IServiceCollection services)
        where TStep : class, IAuthenticatedRequestStep
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthenticatedRequestStep, TStep>());
        return services;
    }

    /// <summary>Whether <see cref="AddApiAuthorization"/> was called.</summary>
    internal static bool IsRegistered(IServiceProvider services) => services.GetService<ApiAuthorizationMarker>() is not null;

    private sealed class ApiAuthorizationMarker;
}
