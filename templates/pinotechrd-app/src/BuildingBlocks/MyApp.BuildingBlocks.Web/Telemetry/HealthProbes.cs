using Microsoft.AspNetCore.Http;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>Recognizes the requests of the health probes, which are neither logged at Information nor traced.</summary>
internal static class HealthProbes
{
    /// <summary>
    /// Whether <paramref name="path"/> is <see cref="HealthRegistration.LivePath"/> or
    /// <see cref="HealthRegistration.ReadyPath"/>, with or without a trailing slash and in any case, as routing matches
    /// them. It is decided by path because a span starts before routing has chosen an endpoint.
    /// </summary>
    public static bool IsProbe(PathString path) => Is(path, HealthRegistration.LivePath) || Is(path, HealthRegistration.ReadyPath);

    private static bool Is(PathString path, string probe) =>
        path.StartsWithSegments(probe, StringComparison.OrdinalIgnoreCase, out var rest) && (!rest.HasValue || rest.Value == "/");
}
