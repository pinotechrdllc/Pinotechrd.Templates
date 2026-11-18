using System.Net;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>Recognizes host names that always mean this machine.</summary>
internal static class LocalHost
{
    /// <summary>
    /// Whether <paramref name="host"/> is <c>localhost</c>, a <c>*.localhost</c> name or a loopback address (with or
    /// without IPv6 brackets). An empty host counts as local.
    /// </summary>
    public static bool Is(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }
}
