using System.Diagnostics;

namespace MyApp.Api.Tests.Support;

/// <summary>One kind of request that carries secrets, and the statuses it may be answered with.</summary>
internal sealed record Scenario(string Name, int[] Statuses, Func<RequestSecrets, HttpRequestMessage> Build, int Repeat = 1)
{
    public bool IsForwarded => Name.StartsWith("forwarded", StringComparison.Ordinal);

    public bool IsUnhandledException => Name.StartsWith("500", StringComparison.Ordinal);
}

/// <summary>The outcome of sending scenarios: the secrets they carried, how many requests, and the trace id of each.</summary>
internal sealed record ScenarioRun(RequestSecrets Secrets, int Requests, IReadOnlyList<string> TraceIds);

/// <summary>
/// The requests that the log and span tests send: a success, an unmatched route, a request the binder rejects, a
/// malformed body, a validation failure, 401, 403, 429, an unhandled exception, requests refused at the start of the
/// pipeline (CORS preflight, forwarded headers from a sender that is not trusted or that sends garbage). Each one
/// carries an <c>Authorization</c> header, a <c>Cookie</c> header, a custom header, a query string with a token and a
/// JSON body, and a trace id of its own so its telemetry can be told from that of other tests.
/// </summary>
internal static class SecretScenarios
{
    public const string Listed = "https://app.example.com";

    public const string Unlisted = "https://evil.example.com";

    public const string Proxy = "10.9.9.9";

    private const string Base = ProblemFixtureEndpoint.Base;

    /// <summary>
    /// A host with authentication (so there is a 401 and a 403), a listed CORS origin (so a preflight can be refused at
    /// the start of the pipeline), a trusted proxy (so forwarded headers are processed) and a limit of two requests
    /// (so a third is answered 429). A null <paramref name="level"/> leaves the default level of the environment.
    /// </summary>
    public static ApiHostFactory CreateFactory(string environment, string? level)
    {
        var factory = ApiHostFactory.ForEnvironment(environment)
            .WithServices(TestAuthenticationHandler.Register)
            .WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed)
            .WithSetting(HttpSettingKeys.KnownProxy(0), Proxy)
            .WithLowLimit();
        return level is null ? factory : factory.WithEarlySetting("Logging:LogLevel:Default", level);
    }

    /// <summary>Every remote address that the scenarios set on their requests (the client address).</summary>
    public static IEnumerable<string> RemoteAddresses => Enumerable.Range(1, 14).Select(i => $"10.20.0.{i}").Append(Proxy);

    public static Scenario[] All() =>
    [
        new("200 version", [200], s => s.Request(HttpMethod.Get, SecurityRequests.Version, "10.20.0.1")),
        new("404 unknown route", [404], s => s.Request(HttpMethod.Get, SecurityRequests.Unknown, "10.20.0.2")),
        new("400 non-integer query value", [400], s => s.Request(HttpMethod.Get, Base + "/count", "10.20.0.3", queryPrefix: $"count={s.QueryToken}&")),
        new("400 malformed JSON", [400], s => s.Request(HttpMethod.Post, Base + "/items", "10.20.0.4", body: $$"""{"name": "{{s.BodyValue}}", """)),
        new("400 validation failure", [400, 422], s => s.Request(HttpMethod.Post, Base + "/items", "10.20.0.5", body: $$"""{"name":"{{s.BodyValue}}","quantity":9999}""")),
        new("401 anonymous", [401], s => s.Request(HttpMethod.Get, ProblemFixtureEndpoint.Secured, "10.20.0.6")),
        new("403 without the permission", [403], s => s.Request(HttpMethod.Get, ProblemFixtureEndpoint.AdminOnly, "10.20.0.7", user: s.CustomHeader)),
        new("200 authenticated with personal claims", [200], s => s.Request(HttpMethod.Get, ProblemFixtureEndpoint.Secured, "10.20.0.13", user: s.NameClaim, claims: s.PersonalClaims())),
        new("403 authenticated with personal claims", [403], s => s.Request(HttpMethod.Get, ProblemFixtureEndpoint.AdminOnly, "10.20.0.14", user: s.NameClaim, claims: s.PersonalClaims())),
        new("429 over the limit", [200, 200, 429], s => s.Request(HttpMethod.Get, SecurityRequests.Version, "10.20.0.8"), Repeat: 3),
        new("500 unhandled exception", [500], s => s.Request(HttpMethod.Get, SecurityRequests.Throwing, "10.20.0.9")),
        new("preflight from an unlisted origin", [200, 204, 400, 403], s => Preflight(s, Unlisted, "10.20.0.10")),
        new("preflight from a listed origin", [200, 204], s => Preflight(s, Listed, "10.20.0.11")),
        new("forwarded headers from an untrusted sender", [200], s => Forwarded(s, "10.20.0.12", s.CustomHeader)),
        new("forwarded headers from the proxy that are not an address", [200], s => Forwarded(s, Proxy, s.CustomHeader)),
    ];

    public static Scenario Named(string name)
    {
        var scenario = Array.Find(All(), s => s.Name == name);
        Assert.NotNull(scenario);
        return scenario;
    }

    /// <summary>Sends one attempt of a scenario with a trace id of its own, checks its status and returns the trace id.</summary>
    public static async Task<string> SendAsync(HttpClient client, Scenario scenario, RequestSecrets secrets, int attempt)
    {
        using var request = scenario.Build(secrets);
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var status = (int)response.StatusCode;
        var allowed = scenario.Repeat == 1 ? scenario.Statuses : [scenario.Statuses[attempt]];
        Assert.True(Array.Exists(allowed, s => s == status), $"Scenario '{scenario.Name}' answered {status}, expected one of {string.Join(", ", allowed)}.");
        return traceId;
    }

    /// <summary>Sends the scenarios that pass <paramref name="include"/> (all by default) with the same secrets.</summary>
    public static async Task<ScenarioRun> RunAsync(HttpClient client, Func<Scenario, bool>? include = null)
    {
        var secrets = RequestSecrets.Create();
        var traceIds = new List<string>();
        foreach (var scenario in All().Where(include ?? (_ => true)))
        {
            for (var i = 0; i < scenario.Repeat; i++)
            {
                traceIds.Add(await SendAsync(client, scenario, secrets, i));
            }
        }

        return new ScenarioRun(secrets, traceIds.Count, traceIds);
    }

    private static HttpRequestMessage Preflight(RequestSecrets secrets, string origin, string remote)
    {
        var request = secrets.Request(HttpMethod.Options, SecurityRequests.Version, remote);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "Authorization, " + RequestSecrets.CustomHeaderName);
        return request;
    }

    private static HttpRequestMessage Forwarded(RequestSecrets secrets, string remote, string value)
    {
        var request = secrets.Request(HttpMethod.Get, SecurityRequests.Version, remote);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", value);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", value);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", value);
        return request;
    }
}
