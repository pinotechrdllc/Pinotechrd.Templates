using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Database;

/// <summary>
/// The connection settings the application refuses, in every project, tenancy or not: each one breaks "a connection's
/// session state (the tenant's <c>search_path</c>) belongs to one physical connection for one open, and is cleared
/// before the next user". Checked on the configured connection string at start (<see cref="DatabaseOptionsValidator"/>)
/// and on any data source a tenant context or the migrator uses (<see cref="Tenancy.TenantDataSource"/>).
/// <para>
/// Considered and allowed: <c>Pooling=false</c> (every open is a new physical connection, closed afterwards),
/// <c>Max Auto Prepare</c> and prepared statements (the reset then runs <c>RESET ALL</c> instead of <c>DISCARD ALL</c>,
/// which still clears <c>search_path</c>, and PostgreSQL re-plans a prepared statement when <c>search_path</c> changes),
/// several hosts, <c>Load Balance Hosts</c> and <c>Target Session Attributes</c> (each open still binds one physical
/// connection to one server), <c>Enlist</c> (the guard refuses to open a tenant connection inside an ambient
/// <c>TransactionScope</c>, the one case where the bind would run inside a transaction that can roll it back),
/// connection lifetimes, keepalives, time zone and encoding. Session defaults the string cannot show (<c>ALTER ROLE</c>
/// or <c>ALTER DATABASE ... SET search_path</c>, <c>PGUSER</c> choosing such a role, <c>postgresql.conf</c>) are checked
/// by the guard on every bind.
/// </para>
/// </summary>
internal static class ConnectionSafety
{
    /// <summary>The keyword of the reset setting, as Npgsql spells it.</summary>
    internal const string NoResetOnClose = "No Reset On Close";

    /// <summary>The environment variable whose startup options Npgsql sends when the connection string has none.</summary>
    internal const string PgOptions = "PGOPTIONS";

    /// <summary>The keyword of the multiplexing setting.</summary>
    internal const string Multiplexing = "Multiplexing";

    /// <summary>
    /// The reason a connection string or a data source is refused, starting with "sets", or <see langword="null"/> when
    /// it is safe. The text names the setting and never quotes a value.
    /// </summary>
    internal static string? Refusal(NpgsqlConnectionStringBuilder settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Multiplexing)
        {
            return $"sets '{Multiplexing}'. It must not be set: with multiplexing, the commands of one connection run on "
                + "whichever physical connection is free, so the tenant's schema set on one is not the schema of the next command.";
        }

        if (settings.NoResetOnClose)
        {
            return $"sets '{NoResetOnClose}'. It must not be set: the tenant's schema is selected per connection, and the "
                + "reset when a connection returns to the pool is what clears it before the next user of that connection.";
        }

        if (!string.IsNullOrEmpty(settings.SearchPath))
        {
            return "sets 'Search Path'. It must not be set: it is the schema every connection starts with and returns to after "
                + "the reset, so connections used without a tenant would see that schema; tenant contexts set their own.";
        }

        if (SetsSearchPath(settings.Options))
        {
            return "sets search_path in 'Options'. It must not be set, for the same reason as 'Search Path'.";
        }

        // Npgsql sends PGOPTIONS when the connection string has no Options.
        if (string.IsNullOrEmpty(settings.Options) && SetsSearchPath(Environment.GetEnvironmentVariable(PgOptions)))
        {
            return $"is used in a process whose environment variable {PgOptions} sets search_path. Remove it from {PgOptions}, "
                + "for the same reason as 'Search Path'.";
        }

        return null;
    }

    /// <summary>
    /// Whether PostgreSQL startup options set <c>search_path</c>, read as the server reads them: arguments are split at
    /// whitespace that is not escaped, a backslash escapes the next character (<c>search\_path</c> is <c>search_path</c>),
    /// a setting is <c>-c name=value</c>, <c>-cname=value</c> or <c>--name=value</c>, and a dash in its name is an
    /// underscore. Only the setting's name counts: <c>-c application_name=search_path_app</c> is allowed.
    /// </summary>
    internal static bool SetsSearchPath(string? options)
    {
        if (string.IsNullOrEmpty(options))
        {
            return false;
        }

        var arguments = SplitArguments(options);
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            string? setting = argument switch
            {
                "-c" when i + 1 < arguments.Count => arguments[++i],
                _ when argument.StartsWith("--", StringComparison.Ordinal) => argument[2..],
                _ when argument.StartsWith("-c", StringComparison.Ordinal) => argument[2..],
                _ => null,
            };
            var name = setting?.Split('=', 2)[0].Replace('-', '_').Trim();
            if (string.Equals(name, "search_path", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The server's own split of startup options: whitespace separates, a backslash makes the next character literal. Quote
    // characters are dropped: the server does not read quotes, so a quoted option is at best refused there, and here it is
    // read as if unquoted, failing closed.
    private static List<string> SplitArguments(string options)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < options.Length; i++)
        {
            var c = options[i];
            if (c == '\\' && i + 1 < options.Length)
            {
                current.Append(options[++i]);
            }
            else if (c is '"' or '\'')
            {
                continue;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
