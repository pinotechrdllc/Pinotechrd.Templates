using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Web.Authorization;
using Serilog;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// Adds the identity of an authenticated request to its completion event (<see cref="RequestLogging.UserIdProperty"/>,
/// <see cref="RequestLogging.TenantIdProperty"/>) and to its server span
/// (<see cref="TelemetryRegistration.UserIdTag"/>, <see cref="TelemetryRegistration.TenantIdTag"/>): the opaque identifiers of <see cref="ICurrentUser"/> and <see cref="ICurrentTenant"/>
/// as GUIDs, each only when it is known. An anonymous request gets neither. Never a name, an email, a token or a claim
/// other than these identifiers. It runs after the steps that set the user and the tenant.
/// </summary>
internal sealed class RequestIdentityTelemetry : IAuthenticatedRequestStep
{
    public int Order => AuthenticatedRequestStepOrder.Telemetry;

    public ValueTask<IResult?> InvokeAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var user = services.GetService<ICurrentUser>();
        var tenant = services.GetService<ICurrentTenant>();
        var userId = user is { IsAuthenticated: true } ? user.UserId.ToString("D") : null;
        var tenantId = tenant is { IsSet: true } ? tenant.TenantId.ToString("D") : null;
        if (userId is null && tenantId is null)
        {
            return ValueTask.FromResult<IResult?>(null);
        }

        var diagnostics = services.GetService<IDiagnosticContext>();
        var span = context.Features.Get<IHttpActivityFeature>()?.Activity;
        Record(diagnostics, span, RequestLogging.UserIdProperty, TelemetryRegistration.UserIdTag, userId);
        Record(diagnostics, span, RequestLogging.TenantIdProperty, TelemetryRegistration.TenantIdTag, tenantId);
        return ValueTask.FromResult<IResult?>(null);
    }

    private static void Record(IDiagnosticContext? diagnostics, Activity? span, string property, string tag, string? value)
    {
        if (value is null)
        {
            return;
        }

        diagnostics?.Set(property, value);
        span?.SetTag(tag, value);
    }
}
