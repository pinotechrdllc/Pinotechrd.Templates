namespace MyApp.Api.Tests.Support;

/// <summary>Configuration keys of the <c>Http</c> section, as documented in the README.</summary>
internal static class HttpSettingKeys
{
    public const string PermitLimit = "Http:RateLimiting:PermitLimit";

    public const string Window = "Http:RateLimiting:Window";

    public const string KnownProxiesName = "Http:ForwardedHeaders:KnownProxies";

    public const string KnownNetworksName = "Http:ForwardedHeaders:KnownNetworks";

    public const string AllowedOriginsName = "Http:Cors:AllowedOrigins";

    public static string AllowedOrigin(int index) => $"{AllowedOriginsName}:{index}";

    public static string KnownProxy(int index) => $"{KnownProxiesName}:{index}";

    public static string KnownNetwork(int index) => $"{KnownNetworksName}:{index}";

    /// <summary>Host with a limit of two requests per hour, so the third request of one client is rejected and no window expires during a test.</summary>
    public static ApiHostFactory WithLowLimit(this ApiHostFactory factory) =>
        factory.WithSetting(PermitLimit, "2").WithSetting(Window, "01:00:00");
}
