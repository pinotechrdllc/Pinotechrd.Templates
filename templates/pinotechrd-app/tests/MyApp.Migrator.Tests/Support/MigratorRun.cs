namespace MyApp.Migrator.Tests.Support;

/// <summary>What one run of the migrator command gave: its exit code and everything it reported.</summary>
internal sealed record MigratorRun(int ExitCode, string Report)
{
    public const string ConnectionKey = "ConnectionStrings:Database";

    public const string ParallelismKey = "Migrator:MaxDegreeOfParallelism";

    /// <summary>Runs the command in this process the way the program does, with the connection string on its command line.</summary>
    public static Task<MigratorRun> ExecuteAsync(string connectionString, params string[] extraArguments) =>
        ExecuteRawAsync([$"--{ConnectionKey}={connectionString}", .. extraArguments]);

    /// <summary>Runs the command with exactly these arguments.</summary>
    public static async Task<MigratorRun> ExecuteRawAsync(params string[] arguments)
    {
        using var report = new StringWriter();
        var exit = await MigratorHost.RunAsync(arguments, report, TestContext.Current.CancellationToken);
        return new MigratorRun(exit, report.ToString());
    }
}
