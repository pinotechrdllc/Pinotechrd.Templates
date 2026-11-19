using System.Security.Cryptography;
using MyApp.Modules.Access.Application.Security;

namespace MyApp.Migrator.Tests.Support;

/// <summary>What one run of the bootstrap command gave: its exit code, its report and what it wrote to standard error.</summary>
internal sealed record BootstrapRun(int ExitCode, string Report, string Error)
{
    public const string Verb = "bootstrap";

    public const string PasswordVariable = "Bootstrap__AdminPassword";

    public const string DefaultSlug = "operator-ops";

    public const string DefaultName = "Operator Ops";

    public const string DefaultEmail = "root.admin@example.test";

    public const string GoodPassword = "correct-horse-battery-staple-1";

    // The environment and standard error belong to the process: runs that set them take turns.
    private static readonly SemaphoreSlim ProcessState = new(1, 1);

    /// <summary>The settings of the command that name the operator tenant and its administrator.</summary>
    public static string[] Settings(string? slug = DefaultSlug, string? name = DefaultName, string? email = DefaultEmail)
    {
        var settings = new List<string>();
        if (slug is not null)
        {
            settings.Add($"--Bootstrap:Slug={slug}");
        }

        if (name is not null)
        {
            settings.Add($"--Bootstrap:Name={name}");
        }

        if (email is not null)
        {
            settings.Add($"--Bootstrap:AdminEmail={email}");
        }

        return [.. settings];
    }

    /// <summary>Runs <c>bootstrap</c> with the default tenant and administrator and the password in the environment.</summary>
    public static async Task<BootstrapRun> ExecuteAsync(string database, string? password, params string[] extraArguments) =>
        (await ExecuteAllAsync(password, [[Verb, $"--{MigratorRun.ConnectionKey}={database}", .. Settings(), .. extraArguments]]))[0];

    /// <summary>
    /// Runs every argument list at the same time, each on its own thread, with the password (or none) in the environment
    /// for all of them. Standard error is collected for all of them together.
    /// </summary>
    public static async Task<BootstrapRun[]> ExecuteAllAsync(string? password, params string[][] argumentLists)
    {
        await ProcessState.WaitAsync(TestContext.Current.CancellationToken);
        var previousVariable = Environment.GetEnvironmentVariable(PasswordVariable);
        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(PasswordVariable, password);
            Console.SetError(error);
            using var start = new ManualResetEventSlim(false);
            var runs = argumentLists.Select(arguments => Task.Factory.StartNew(
                async () =>
                {
                    start.Wait();
                    using var report = new StringWriter();
                    var exit = await MigratorHost.RunAsync(arguments, report, TestContext.Current.CancellationToken);
                    return (exit, report: report.ToString());
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap()).ToArray();
            start.Set();
            var results = await Task.WhenAll(runs);
            await Task.Delay(200, TestContext.Current.CancellationToken); // the console logger writes from its own thread
            var captured = error.ToString();
            return [.. results.Select(r => new BootstrapRun(r.exit, r.report, captured))];
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(PasswordVariable, previousVariable);
            ProcessState.Release();
        }
    }

    /// <summary>The full text a person could read from the run: the report and standard error.</summary>
    public string Everything => Report + "\n" + Error;

    /// <summary>A password of <paramref name="runes"/> Unicode scalar values (an emoji is one, and two UTF-16 characters).</summary>
    public static string PasswordOfRunes(int runes, bool emoji) =>
        emoji ? string.Concat(Enumerable.Repeat("\U0001F600", runes)) : new string('ω', runes);

    /// <summary>The subset of an Identity V3 hash header: its format, PRF, iteration count and salt size.</summary>
    internal sealed record HashHeader(byte Format, uint Prf, uint Iterations, int SaltLength, byte[] Salt, byte[] Subkey);

    /// <summary>Decodes an ASP.NET Core Identity V3 password hash (base64 of 0x01, PRF, iterations, salt length, salt, subkey).</summary>
    public static HashHeader Decode(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        static uint Read(byte[] b, int offset) => (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
        var saltLength = (int)Read(bytes, 9);
        return new HashHeader(bytes[0], Read(bytes, 1), Read(bytes, 5), saltLength, bytes[13..(13 + saltLength)], bytes[(13 + saltLength)..]);
    }

    /// <summary>Whether the hash holds the password, derived again from its own salt with PBKDF2 and HMAC-SHA-512.</summary>
    public static bool Verifies(string hash, string password)
    {
        var header = Decode(hash);
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, header.Salt, (int)header.Iterations, HashAlgorithmName.SHA512, header.Subkey.Length);
        return CryptographicOperations.FixedTimeEquals(derived, header.Subkey);
    }

    /// <summary>Every permission of the Access module, in the upper case the tenant stores.</summary>
    public static string[] ExpectedPermissionCodes { get; } =
        [.. AccessPermissions.All.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal)];
}
