using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Pinotechrd.Security.Extensions;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Options;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>Q5: what AddPinotechrdSecurity validates, when, and whether a host with no configuration can start.</summary>
public static class Q5Startup
{
    public static async Task<Outcome> RunAsync()
    {
        var q = new Outcome("Q5", required: true);
        var empty = new ConfigurationBuilder().Build();

        q.Fact($"no configure delegate -> {Register(null)}");
        q.Fact($"secret from an empty configuration (null) -> {Register(o => o.Jwt.Secret = empty["Pinotechrd:Security:Jwt:Secret"]!)}");
        q.Fact($"secret of 18 characters -> {Register(o => o.Jwt.Secret = "too-short-fake-key")}");
        q.Fact($"secret of 32 spaces -> {Register(o => o.Jwt.Secret = new string(' ', 32))}");

        // The minimum is an option the caller can lower; the key size is then only checked when a token is signed.
        var weak = new ServiceCollection().AddLogging().AddDataProtection().Services;
        weak.AddDbContext<PinotechrdTenantSecurityDbContext>();
        weak.AddPinotechrdSecurity<PinotechrdTenantSecurityDbContext, AppUser, AppRole>(o =>
        {
            o.Jwt.MinimumSecretLength = 8;
            o.Jwt.Secret = "fake-key";
        });
        string signing;
        try
        {
            using var sp = weak.BuildServiceProvider();
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITokenProvider>().GenerateAccessToken([new Claim("id", "x")]);
            signing = "signed";
        }
        catch (Exception ex)
        {
            signing = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        }

        q.Fact($"MinimumSecretLength=8 and an 8-character secret: registration accepted; signing a token -> {signing}");

        // No connection string: the library takes none. A context with no provider fails only when first used.
        var noDb = new ServiceCollection().AddLogging().AddDataProtection().Services;
        noDb.AddDbContext<PinotechrdTenantSecurityDbContext>();
        noDb.AddPinotechrdSecurity<PinotechrdTenantSecurityDbContext, AppUser, AppRole>(o => o.Jwt.Secret = SpikeRegistration.FakeSigningKey);
        string firstUse;
        using (var sp = noDb.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false }))
        {
            using var scope = sp.CreateScope();
            try
            {
                _ = scope.ServiceProvider.GetRequiredService<PinotechrdTenantSecurityDbContext>().Model;
                firstUse = "model built";
            }
            catch (Exception ex)
            {
                firstUse = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
            }
        }

        q.Fact($"no database configured: AddPinotechrdSecurity and BuildServiceProvider succeed; first use of the context -> {firstUse}");

        // Build-time OpenAPI generation, as the template does it: the host starts with no configuration.
        var probe = FindProbe();
        var plain = await BuildProbeAsync(probe, guarded: false);
        var guarded = await BuildProbeAsync(probe, guarded: true);
        var document = Path.Combine(probe, "obj", "openapi", "Pinotechrd.Spikes.OpenApiProbe.json");
        q.Fact($"build-time OpenAPI, secret read from configuration: exit {plain.ExitCode}; {plain.Evidence}");
        q.Fact($"same with a random throw-away key only when the entry assembly is GetDocument.Insider: exit {guarded.ExitCode}; document written = {File.Exists(document)}");
        q.Check(plain.ExitCode != 0, "with no configuration the eager check stops the build-time document generation");
        q.Check(guarded.ExitCode == 0 && File.Exists(document), "the guarded registration lets the document be generated");

        q.Summary = q.Passed
            ? "AddPinotechrdSecurity throws InvalidOperationException inside the call (before Build) for an empty, whitespace or short secret; " +
              "it validates no connection string. With no configuration the build-time OpenAPI generation fails; registering a random key only " +
              "under GetDocument.Insider works around it."
            : "start-up behaviour differs from the expectation";
        return q;
    }

    private static string Register(Action<PinotechrdSecurityOptions>? configure)
    {
        var services = new ServiceCollection();
        try
        {
            services.AddPinotechrdSecurity<PinotechrdTenantSecurityDbContext, AppUser, AppRole>(configure);
            return "accepted";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name} thrown by the Add call: \"{ex.Message}\"";
        }
    }

    private static string FindProbe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "OpenApiProbe")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("OpenApiProbe"), "OpenApiProbe");
    }

    private static async Task<(int ExitCode, string Evidence)> BuildProbeAsync(string probe, bool guarded)
    {
        var document = Path.Combine(probe, "obj", "openapi");
        if (Directory.Exists(document))
        {
            Directory.Delete(document, recursive: true);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = probe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "build", "--nologo", "-v:q", "-p:OpenApiGenerateDocuments=true", $"-p:PinotechrdGuard={guarded.ToString().ToLowerInvariant()}" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout + await stderr;
        var line = output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("InvalidOperationException", StringComparison.Ordinal) || l.Contains("JWT secret", StringComparison.Ordinal))
            ?? output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
            ?? "no error line";
        return (process.ExitCode, line.Length > 240 ? line[..240] : line);
    }
}
