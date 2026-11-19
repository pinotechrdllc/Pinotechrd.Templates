using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

/// <summary>The guard's check of command text. SessionStatements is internal, so it is called through reflection.</summary>
internal static class SessionStatementsProbe
{
    private static readonly MethodInfo RefusalMethod =
        typeof(DatabaseRegistration).Assembly.GetType(typeof(TenantSchemaName).Namespace + ".SessionStatements", throwOnError: true)!
            .GetMethod("Refusal", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    /// <summary>Why the text is refused, or null when the guard lets it through.</summary>
    public static string? Refusal(string? commandText) => (string?)RefusalMethod.Invoke(null, [commandText]);

    /// <summary>Statements that change the session's path or role, in the forms the guard must refuse.</summary>
    public static readonly string[] Blocked =
    [
        "DISCARD ALL",
        "DISCARD",
        "DISCARD UNKNOWN_KIND",
        "RESET ALL",
        "RESET search_path",
        "RESET ROLE",
        "RESET SESSION AUTHORIZATION",
        "SET search_path TO x_schema",
        "SET search_path = x_schema",
        "SET SESSION search_path TO x_schema",
        "SET LOCAL search_path TO x_schema",
        "SET SCHEMA 'x_schema'",
        "SET ROLE x_role",
        "SET LOCAL ROLE x_role",
        "SET SESSION AUTHORIZATION x_role",
        "SELECT set_config('search_path','x_schema',false)",
        "SELECT set_config('search_path','x_schema',true)",
        "SELECT pg_catalog.set_config('search_path','x_schema',false)",
        "SELECT set_config(c.name, 'x', false) FROM (SELECT 'search_path' AS name) c",
        "SELECT set_config(current_setting('app.setting'), 'x', false)",
        "ALTER ROLE x_role SET work_mem='1MB'",
        "ALTER USER x_role SET work_mem='1MB'",
        "ALTER DATABASE x_db SET work_mem='1MB'",
        "ALTER SYSTEM SET work_mem='1MB'",
        "SET \"search_path\" TO x_schema",
        "SET SESSION \"search_path\" TO x_schema",
        "RESET \"search_path\"",
        "SET \"role\" TO x_role",
        "SET U&\"search_path\" TO x_schema",
        "SELECT U&\"a\" FROM t",
        "SELECT \"set_config\"('search_path','x_schema',false)",
        "SELECT pg_catalog.\"set_config\"('search_path','x_schema',false)",
        "SELECT set_config('search'||'_path','x_schema',false)",
        "SELECT set_config('search'\n'_path','x_schema',false)",
        "SELECT set_config('sea' 'rch_path','x_schema',false)",
        "SELECT set_config(E'search_path','x_schema',false)",
        "SELECT set_config($$search_path$$,'x_schema',false)",
        "SELECT set_config($1,'x_schema',false)",
        "SELECT set_config(c.name,'x_schema',false) FROM c",
        "SELECT set_config('search_path'::text,'x_schema',false)",
        "SELECT set_config(('search_path'),'x_schema',false)",
        "SELECT set_config('session_authorization','x_role',false)",
        "SELECT set_config('role','x_role',false)",
        "SELECT set_config('standard_conforming_strings','off',false)",
        "SET standard_conforming_strings TO off",
        "SET LOCAL standard_conforming_strings = off",
        "SET session_authorization TO x_role",
        "PREPARE p AS SELECT set_config('search_path','x_schema',false)",
        "SELECT 1 AS x$a$; RESET ALL; SELECT 2 AS y$a$",
        "UPDATE pg_settings SET setting = 'x_schema' WHERE name = 'search_path'",
        "UPDATE pg_catalog.pg_settings SET setting = 'x_schema' WHERE name = 'search_path'",
        "UPDATE \"pg_catalog\".\"pg_settings\" SET setting = 'x_schema' WHERE name = 'search_path'",
        "UPDATE ONLY pg_settings AS s SET setting = 'x_schema' WHERE s.name = 'search_path'",
        "INSERT INTO pg_settings (name, setting) VALUES ('search_path', 'x_schema')",
        "DELETE FROM pg_settings WHERE name = 'search_path'",
        "MERGE INTO pg_settings USING (SELECT 1) AS s ON true WHEN MATCHED THEN UPDATE SET setting = 'x_schema'",
        "WITH u AS (UPDATE pg_settings SET setting = 'x_schema' WHERE name = 'search_path' RETURNING name) SELECT * FROM u",
        "PREPARE p AS UPDATE pg_settings SET setting = 'x_schema' WHERE name = 'search_path'",
    ];

    /// <summary>Statements that look like the above and must pass.</summary>
    public static readonly string[] Allowed =
    [
        "SELECT 'RESET ALL; SET search_path TO x'",
        "SELECT 1 AS \"reset\"",
        "SELECT 1 AS reset_token",
        "SET statement_timeout = 1000",
        "RESET statement_timeout",
        "SELECT set_config('app.user_id','1',false)",
        "SELECT $$RESET ALL$$",
        "SELECT $tag$ a; RESET ALL; SET search_path TO x $tag$",
        "CREATE FUNCTION f() RETURNS int LANGUAGE sql SET search_path = pg_catalog AS $$ SELECT 1 $$",
        "SELECT E'it\\'s; RESET ALL'",
        "SELECT E'a''b; SET search_path TO x'",
        "SELECT 1 -- RESET ALL",
        "/* RESET ALL; SET ROLE x */ SELECT 1",
        "SELECT \"a;b\" FROM t",
        "ALTER TABLE t ADD COLUMN reset int",
        "SELECT current_setting('search_path')",
        "SELECT i.id, i.name FROM access_items AS i WHERE i.name = 'discard all'",
        "DISCARD PLANS",
        "DISCARD TEMP",
        "DISCARD TEMPORARY",
        "DISCARD SEQUENCES",
        "ALTER USER MAPPING FOR x SERVER s OPTIONS (SET user 'u')",
        "SELECT set_config('application_name','search_path_app',false)",
        "SELECT 1 AS x$a$",
        "SELECT 1 AS \"search_path\", 2 AS \"role\", 3 AS \"set_config\"",
        "UPDATE reserved_words SET role = 'x', search_path = 'y', \"schema\" = 'z'",
        "INSERT INTO reserved_words (id, role) VALUES ('1', 'a') ON CONFLICT (id) DO UPDATE SET role = EXCLUDED.role",
        "INSERT INTO t (v) VALUES ('x; RESET ALL; --')",
        "SELECT name, setting FROM pg_settings WHERE name = 'search_path'",
        "SELECT * FROM pg_settings_log",
        "UPDATE pg_settings_log SET setting = 'x' WHERE name = 'search_path'",
        "DELETE FROM pg_settings_log WHERE name = 'x'",
        "UPDATE t SET pg_settings = 1",
        "INSERT INTO t (pg_settings) VALUES (1)",
        "UPDATE t SET x = s.setting FROM pg_settings s WHERE s.name = 'application_name'",
        "SELECT 'UPDATE pg_settings SET setting = 1 WHERE true'",
    ];
}

// The lexical check, without a server: each blocked statement in every form a caller could write it, and the look-alikes
// that must pass.
public class SessionStatementLexerTests
{
    private static IEnumerable<string> Decorated(string statement)
    {
        yield return statement;
        yield return "/* leading */ " + statement;
        yield return "-- leading\n" + statement;
        yield return "  \n\t  " + statement;
        yield return statement.ToUpperInvariant();
        yield return statement.ToLowerInvariant();
        yield return string.Concat(statement.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)));
        yield return statement.Replace(" ", "  \n  ", StringComparison.Ordinal);
        yield return statement.Replace(" ", "/**/", StringComparison.Ordinal);
        yield return "SELECT 1; " + statement;
        yield return "SELECT 1;\n-- c\n" + statement + " ;";
        yield return statement + "; SELECT 1";
        yield return "SELECT 'it''s'; " + statement;
    }

    // Breaks when a statement that changes the session's path or role gets through in any of these forms: leading comments,
    // any case, extra whitespace or comments between words, as the second statement of a batch.
    [Fact]
    public void EveryBlockedStatementIsRefusedInEveryForm()
    {
        var missed = SessionStatementsProbe.Blocked
            .SelectMany(Decorated, (statement, form) => (statement, form))
            .Where(x => SessionStatementsProbe.Refusal(x.form) is null)
            .Select(x => x.form)
            .ToList();

        Assert.True(missed.Count == 0, "Not refused:" + Environment.NewLine + string.Join(Environment.NewLine, missed));
    }

    // Breaks when the refusal is too wide: look-alikes (a literal, a quoted alias, a column named reset_token, a dollar-quoted
    // string, a comment, SET of a harmless setting, a function-level SET) must pass.
    [Fact]
    public void LookAlikesPass()
    {
        var refused = SessionStatementsProbe.Allowed.Where(s => SessionStatementsProbe.Refusal(s) is not null).ToList();

        Assert.True(refused.Count == 0, "Refused:" + Environment.NewLine + string.Join(Environment.NewLine, refused));
    }

    // Breaks when the text of the very long command is not scanned to its end (a statement after a megabyte of text), or when
    // the scan is slow enough to matter.
    [Fact]
    public void AStatementAfterAVeryLongTextIsStillRefused()
    {
        var text = "SELECT '" + new string('x', 2_000_000) + "'; RESET ALL";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var refusal = SessionStatementsProbe.Refusal(text);
        watch.Stop();

        Assert.NotNull(refusal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Scanning took {watch.Elapsed}.");
    }

    [Fact]
    public void AnEmptyOrMissingTextIsNotRefused()
    {
        Assert.Null(SessionStatementsProbe.Refusal(null));
        Assert.Null(SessionStatementsProbe.Refusal(string.Empty));
        Assert.Null(SessionStatementsProbe.Refusal("   "));
    }
}

// Tries to defeat the lexer against a real server: each candidate really changes the session on the server (the probe
// checks it), so the guard must refuse it. A candidate that gets through is a bypass. Needs Docker.
[Collection(PostgresContainer.Name)]
public class SessionStatementBypassTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Candidates =
    [
        "SET \"search_path\" TO x_schema",
        "SET SESSION \"search_path\" TO x_schema",
        "RESET \"search_path\"",
        "SET \"role\" TO {role}",
        "SET U&\"sea\\0072ch_path\" TO x_schema",
        "SELECT \"set_config\"('search_path','x_schema',false)",
        "SELECT pg_catalog.\"set_config\"('search_path','x_schema',false)",
        "SELECT 1 AS x$a$; RESET ALL; SELECT 2 AS y$a$",
        "SELECT 1 AS x$a$; SET search_path TO x_schema; SELECT 2 AS y$a$",
        "/* /* */ RESET ALL */ RESET ALL",
        "SET/**/search_path TO x_schema",
        "SELECT E'\\\\'; SET search_path TO x_schema",
        "SELECT E'\\''; SET search_path TO x_schema",
        "SELECT 'it''s'; SET search_path TO x_schema",
        "SELECT $a$ ; $a$; SET search_path TO x_schema",
        "SELECT $a$ $b$ $a$; SET search_path TO x_schema",
        "SELECT $$ $a$ $$; SET search_path TO x_schema",
        "SELECT $é$ ; $é$; RESET ALL",
        "PREPARE p AS SELECT set_config('search_path','x_schema',false); EXECUTE p",
        "PREPARE p(text) AS SELECT set_config($1,'x_schema',false); EXECUTE p('search_path')",
        "SELECT set_config(U&'search_path', 'x_schema', false)",
        "SELECT set_config('app.x', 'v', false), set_config('search_path', 'x_schema', false)",
        "select SET_CONFIG ( 'SEARCH_PATH', 'x_schema', false )",
        "SELECT 1 -- '\n; SET search_path TO x_schema",
        "SELECT 1 /* ' */; SET search_path TO x_schema",
        "SELECT 1 AS \"a'b\"; SET search_path TO x_schema",
        "SELECT * FROM (SELECT set_config('search_path','x_schema',false)) AS t",
        "SET SESSION AUTHORIZATION {role}",
        "SET ROLE {role}",
        "BEGIN; SET search_path TO x_schema; COMMIT",
        "SELECT 1\n;\n--c\nRESET ALL",
        "SET standard_conforming_strings TO off",
        "SET session_authorization TO {role}",
        "SELECT set_config('search'||'_path','x_schema',false)",
        "SELECT set_config('search'\n'_path','x_schema',false)",
        "SELECT set_config(E'search_path','x_schema',false)",
        "SELECT set_config($$search_path$$,'x_schema',false)",
        "SELECT set_config(c, 'x_schema', false) FROM (SELECT 'search_path' AS c) t",
        "SELECT set_config('search_path'::text,'x_schema',false)",
        "SELECT set_config(('search_path'),'x_schema',false)",
        "SELECT set_config('SEARCH_PATH','x_schema',false)",
        "SELECT set_config('session_authorization','{role}',false)",
        "SELECT set_config('role','{role}',false)",
        "SELECT set_config('standard_conforming_strings','off',false)",
        "PREPARE q(text,text) AS SELECT set_config($1,$2,false); EXECUTE q('search_path','x_schema')",
        "SELECT 1 AS \"x\"; SET \"search_path\" TO x_schema",
        "UPDATE pg_settings SET setting = 'x_schema' WHERE name = 'search_path'",
        "UPDATE pg_catalog.pg_settings SET setting = 'x_schema' WHERE name = 'search_path'",
    ];

    public static TheoryData<string> Inputs() => [.. Candidates];

    // The session of the probe: a path set before the candidate runs, so a change shows. Compared as the path, the current
    // role and the session role.
    private static async Task<string> FingerprintAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT current_setting('search_path') || '|' || current_user || '|' || session_user || '|' || current_setting('standard_conforming_strings')", connection);
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<bool> ChangesTheSessionAsync(TenantRig rig, string sql)
    {
        await using var connection = await rig.OpenAdminAsync();
        await using (var preset = new NpgsqlCommand("SET search_path TO pre_schema", connection))
        {
            await preset.ExecuteNonQueryAsync(Token);
        }

        var before = await FingerprintAsync(connection);
        try
        {
#pragma warning disable CA2100 // The candidates are written by the test.
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(Token);
        }
        catch (PostgresException)
        {
            return false;
        }

        return before != await FingerprintAsync(connection);
    }

    // Breaks when a statement that changes the session's path or role on the server is let through by the lexer. Each
    // candidate is first run on a raw connection to prove it has an effect; the failure message lists the exact inputs the
    // guard does not refuse.
    [Fact]
    public async Task EverySessionChangingCandidateIsRefusedByTheGuard()
    {
        await using var login = await DedicatedLogin.CreateAsync(postgres);
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var effective = new List<string>();
        var inert = new List<string>();
        var passed = new List<string>();
        foreach (var candidate in Candidates)
        {
            var sql = candidate.Replace("{role}", login.Name, StringComparison.Ordinal);
            if (await ChangesTheSessionAsync(rig, sql))
            {
                effective.Add(candidate);
                if (SessionStatementsProbe.Refusal(sql) is null)
                {
                    passed.Add(candidate);
                }
            }
            else
            {
                inert.Add(candidate);
            }
        }

        Assert.True(inert.Count == 0, "Candidates with no effect on the server (fix the probe):" + Environment.NewLine + string.Join(Environment.NewLine, inert));
        Assert.True(passed.Count == 0, "Effective on the server and not refused by the guard:" + Environment.NewLine + string.Join(Environment.NewLine, passed));
    }

    // What the guard cannot see, and the README says so: SQL built inside the server. A DO block and a function that calls
    // set_config change the session and are allowed through. Breaks (the day it is fixed) if the guard starts refusing them,
    // when this test and the README line should be updated together.
    [Theory]
    [InlineData("DO $$ BEGIN PERFORM set_config('search_path','x_schema',false); END $$")]
    [InlineData("CREATE FUNCTION pg_temp.hidden_path_change() RETURNS text LANGUAGE sql AS $$ SELECT set_config('search_path','x_schema',false) $$; SELECT pg_temp.hidden_path_change()")]
    [InlineData("DO $$ BEGIN EXECUTE 'SET search_path TO x_schema'; END $$")]
    public async Task SqlBuiltInsideTheServerIsOutOfTheGuardsReachAsDocumented(string sql)
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);

        Assert.True(await ChangesTheSessionAsync(rig, sql));
        Assert.Null(SessionStatementsProbe.Refusal(sql));
    }
}

// The guard's check through the EF Core entry points on an open tenant connection. Needs Docker.
[Collection(PostgresContainer.Name)]
public class SessionStatementEfTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool IsQuery(string statement) =>
        statement.StartsWith("SELECT", StringComparison.Ordinal) || statement.StartsWith("WITH", StringComparison.Ordinal);

    private static void SyncRefusals(AccessContext context, TenantRig rig, string statement)
    {
        var before = rig.CommandsSent;
        Assert.Throws<InvalidOperationException>(() => context.Database.ExecuteSqlRaw(statement));
        if (IsQuery(statement))
        {
            // SqlQueryRaw wraps the text in a subquery, so only statements that are queries can be sent through it.
            Assert.Throws<InvalidOperationException>(() => context.Database.SqlQueryRaw<int>(statement).ToList());
        }

        Assert.Equal(before, rig.CommandsSent);
    }

    // Breaks when a statement that changes the session is sent to the server through ExecuteSqlRaw(Async) or SqlQueryRaw (or
    // FromSqlRaw for a set_config call), or when a refusal leaves the connection unusable: each refused call sends no command
    // and the next query reads the tenant's schema.
    [Fact]
    public async Task StatementsThatChangeTheSessionAreRefusedBeforeTheServerAndTheNextQueryReadsTheTenant()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await context.Database.OpenTenantConnectionAsync(Token);
        var before = rig.CommandsSent;

        foreach (var statement in SessionStatementsProbe.Blocked)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.ExecuteSqlRawAsync(statement, Token));
            if (IsQuery(statement))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<int>(statement).ToListAsync(Token));
            }

            SyncRefusals(context, rig, statement);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Items.FromSqlRaw("SELECT * FROM access_items WHERE (SELECT set_config('search_path','x_schema',false)) IS NOT NULL").ToListAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Items.FromSqlRaw("WITH u AS (UPDATE pg_settings SET setting = 'x_schema' WHERE name = 'search_path' RETURNING name) SELECT * FROM access_items").ToListAsync(Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.ExecuteSqlRawAsync("/* c */ select 1; reset all", Token));
        Assert.Equal(before, rig.CommandsSent);
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // Breaks when look-alikes are refused or break the binding: they run, and the session stays on the tenant.
    [Fact]
    public async Task LookAlikeStatementsRunAndTheSessionStaysOnTheTenant()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        await context.Database.OpenTenantConnectionAsync(Token);

        Assert.Equal("RESET ALL; SET search_path TO x", await context.Database.SqlQueryRaw<string>("SELECT 'RESET ALL; SET search_path TO x' AS \"Value\"").SingleAsync(Token));
        await context.Database.ExecuteSqlRawAsync("SELECT 1 AS reset_token", Token);
        await context.Database.ExecuteSqlRawAsync("SELECT 1 AS \"reset\"", Token);
        Assert.Equal("RESET ALL", await context.Database.SqlQueryRaw<string>("SELECT $$RESET ALL$$ AS \"Value\"").SingleAsync(Token));
        Assert.Equal("it's; RESET ALL", await context.Database.SqlQueryRaw<string>("SELECT E'it\\'s; RESET ALL' AS \"Value\"").SingleAsync(Token));
        await context.Database.ExecuteSqlRawAsync("SELECT 1 AS x$a$", Token);
        Assert.Equal("search_path_app", await context.Database.SqlQueryRaw<string>("SELECT set_config('application_name','search_path_app',false) AS \"Value\"").SingleAsync(Token));
        Assert.Contains(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>("SELECT setting AS \"Value\" FROM pg_settings WHERE name = 'search_path'").SingleAsync(Token), StringComparison.Ordinal);
        await context.Database.ExecuteSqlRawAsync("DISCARD PLANS", Token);
        await context.Database.ExecuteSqlRawAsync("DISCARD TEMP", Token);
        await context.Database.ExecuteSqlRawAsync("SET statement_timeout = 1000", Token);
        Assert.Equal("1s", await context.Database.SqlQueryRaw<string>("SELECT current_setting('statement_timeout') AS \"Value\"").SingleAsync(Token));
        Assert.Equal("1", await context.Database.SqlQueryRaw<string>("SELECT set_config('app.user_id','1',false) AS \"Value\"").SingleAsync(Token));
        await context.Database.ExecuteSqlRawAsync("CREATE FUNCTION pg_temp.scoped_fn() RETURNS int LANGUAGE sql SET search_path = pg_catalog AS $$ SELECT 1 $$", Token);
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // Reports what the guard sees. Commands EF runs are seen (above). A raw DbCommand made from Database.GetDbConnection()
    // on the open tenant connection is NOT: it is outside EF Core, so a path change through it succeeds and the guard still
    // believes the connection is bound (the README says the code that uses the connection directly follows the rule
    // unchecked). Closing the connection and letting EF open it again binds it anew. Breaks (the day the guard reads raw
    // commands) when this behaviour is fixed; update this test and the README together.
    [Fact]
    public async Task ARawCommandOnTheTenantConnectionIsOutsideTheGuardsReachAndAReopenBindsAgain()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        var connection = await context.Database.OpenTenantConnectionAsync(Token);

        await using (var raw = new NpgsqlCommand("SET search_path TO raw_command_schema", connection))
        {
            await raw.ExecuteNonQueryAsync(Token);
        }

        Assert.Equal("raw_command_schema", await ExecuteScalarAsync(connection, "SELECT current_setting('search_path')"));

        await context.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // Breaks when pg_temp is searched before the tenant's schema: a temporary table named like a tenant table would then answer
    // the tenant's own queries in place of the real table (pg_temp is last in every tenant path), and a later scope on the same
    // physical connection (pool of one: same backend) must still see the real table.
    [Fact]
    public async Task ATemporaryTableNamedLikeATenantTableNeverShadowsItAndIsGoneWhenTheConnectionIsReturned()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 1);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await rig.ExecuteAsync($"INSERT INTO \"{TenantSchemaName.For(tenant)}\".access_items (id, name) VALUES (gen_random_uuid(), 'real-row')");
        long first;
        await using (var scope = rig.ScopeFor(tenant))
        {
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
            await context.Database.OpenTenantConnectionAsync(Token);
            await context.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE access_items (id uuid, name text, created_by text)", Token);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO pg_temp.access_items (id, name) VALUES (gen_random_uuid(), 'temp-row')", Token);

            Assert.Equal(["real-row"], await context.Items.Select(i => i.Name).ToListAsync(Token));
            first = await context.Database.SqlQueryRaw<long>("SELECT pg_backend_pid()::bigint AS \"Value\"").SingleAsync(Token);
        }

        await using var later = rig.ScopeFor(tenant);
        var again = later.ServiceProvider.GetRequiredService<AccessContext>();
        Assert.Equal(first, await again.Database.SqlQueryRaw<long>("SELECT pg_backend_pid()::bigint AS \"Value\"").SingleAsync(Token));
        Assert.Equal(["real-row"], await again.Items.Select(i => i.Name).ToListAsync(Token));
    }

    private static async Task<object?> ExecuteScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Token);
    }
}
