using System.Collections.Concurrent;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Finds, in the text of a command a tenant context runs through EF Core, a statement that would change the session's
/// <c>search_path</c> or role after the connection was bound, so the guard can refuse it before it reaches the server.
/// <para>
/// It is a guard against mistakes in application SQL, not a security boundary. It cannot see SQL built or run inside
/// the server (functions and procedures, <c>DO</c> blocks, dynamic <c>EXECUTE</c>), nor commands run directly on the
/// connection returned by <c>OpenTenantConnectionAsync</c>.
/// </para>
/// <para>
/// The text is read with PostgreSQL's lexical rules: comments (nested block comments too), string constants (standard,
/// <c>E''</c>, <c>U&amp;''</c>, bit and national, dollar-quoted, where a dollar quote starts only where a token can start),
/// quoted identifiers (read as their name, so <c>"search_path"</c> is <c>search_path</c>), and statements split at
/// semicolons. Refused, matching names case-insensitively:
/// </para>
/// <list type="bullet">
/// <item>a statement starting with <c>DISCARD ALL</c> (other <c>DISCARD</c> forms leave the path alone), <c>RESET ALL</c>,
/// <c>RESET</c> or <c>SET [SESSION|LOCAL]</c> of <c>search_path</c>, <c>schema</c>, <c>role</c>,
/// <c>session_authorization</c>, <c>SET SESSION AUTHORIZATION</c>, <c>SET standard_conforming_strings</c> (it changes how
/// the following text is read), or <c>ALTER ROLE|USER|DATABASE|SYSTEM</c> (except <c>ALTER USER MAPPING</c>);</item>
/// <item>any <c>set_config</c> call (schema-qualified or quoted too) unless its first argument is one plain string
/// constant followed by a comma, naming a setting other than those above;</item>
/// <item>any write to <c>pg_settings</c> (<c>UPDATE [ONLY]</c>, <c>INSERT INTO</c>, <c>DELETE FROM</c>, <c>MERGE INTO</c>
/// it, plain, <c>pg_catalog.</c>-qualified or quoted, anywhere in the text, data-modifying CTEs and <c>PREPARE</c> included):
/// an <c>UPDATE</c> of that view runs <c>set_config</c> through its rule. Reading it, a table such as
/// <c>pg_settings_log</c> and a column named <c>pg_settings</c> are not refused;</item>
/// <item>any <c>U&amp;"..."</c> identifier, which this check does not decode (it fails closed).</item>
/// </list>
/// <para>
/// Checked and found to need nothing more on PostgreSQL 17: <c>set_config</c> (C function <c>set_config_by_name</c>) is the
/// only built-in function that sets a configuration value, no SQL-language built-in calls it, and <c>pg_settings</c> is the
/// only catalog or information_schema view with a write rule.
/// </para>
/// </summary>
internal static class SessionStatements
{
    // The cache holds verdicts of short texts only (EF's own SQL repeats); its size is counted without locking.
    private const int CacheLimit = 4096;
    private const int MaxCachedLength = 4096;

    private static readonly ConcurrentDictionary<string, string?> Verdicts = new(StringComparer.Ordinal);

    private static readonly HashSet<string> SessionSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "search_path", "schema", "role", "session_authorization", "standard_conforming_strings",
    };

    private static int _cached;

    private enum Kind
    {
        Word,
        QuotedWord,
        UnicodeWord,
        PlainString,
        OtherString,
        Semicolon,
        Comma,
        Dot,
        OpenParen,
        Other,
    }

    /// <summary>
    /// Why <paramref name="commandText"/> is refused, naming the statement kind, or <see langword="null"/> when it changes
    /// nothing the binding relies on.
    /// </summary>
    internal static string? Refusal(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return null;
        }

        if (commandText.Length > MaxCachedLength)
        {
            return Analyze(commandText);
        }

        if (Verdicts.TryGetValue(commandText, out var cached))
        {
            return cached;
        }

        var verdict = Analyze(commandText);
        if (Volatile.Read(ref _cached) < CacheLimit && Verdicts.TryAdd(commandText, verdict))
        {
            Interlocked.Increment(ref _cached);
        }

        return verdict;
    }

    private static string? Analyze(string sql)
    {
        var tokens = Tokenize(sql);
        if (tokens.Exists(t => t.Kind == Kind.UnicodeWord))
        {
            return "a U&\"...\" identifier";
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (WriteTarget(tokens, i) is var target && target >= 0 && NamesPgSettings(tokens, target))
            {
                return "a write to pg_settings, which sets configuration values";
            }

            if (IsWord(tokens[i], "set_config") && i + 1 < tokens.Count && tokens[i + 1].Kind == Kind.OpenParen && !IsHarmlessSetConfig(tokens, i + 2))
            {
                return "a set_config call that can change the session's path or role";
            }
        }

        var start = 0;
        for (var i = 0; i <= tokens.Count; i++)
        {
            if (i == tokens.Count || tokens[i].Kind == Kind.Semicolon)
            {
                if (StatementRefusal(tokens, start, i) is { } refusal)
                {
                    return refusal;
                }

                start = i + 1;
            }
        }

        return null;
    }

    private static string? StatementRefusal(List<Token> tokens, int start, int end)
    {
        string? Word(int offset) =>
            start + offset < end && tokens[start + offset].Kind is Kind.Word or Kind.QuotedWord ? tokens[start + offset].Value : null;

        var first = Word(0)?.ToUpperInvariant();
        var second = Word(1);
        switch (first)
        {
            case "DISCARD" when !(second is { } kind && (kind.Equals("plans", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("temp", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("temporary", StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("sequences", StringComparison.OrdinalIgnoreCase))):
                return "DISCARD " + second?.ToUpperInvariant();
            case "RESET" when second is not null && (second.Equals("all", StringComparison.OrdinalIgnoreCase)
                    || second.Equals("session", StringComparison.OrdinalIgnoreCase) || SessionSettings.Contains(second)):
                return "RESET " + second.ToUpperInvariant();
            case "SET":
                var modifier = second is not null && (second.Equals("session", StringComparison.OrdinalIgnoreCase)
                    || second.Equals("local", StringComparison.OrdinalIgnoreCase));
                var name = modifier ? Word(2) : second;
                if (modifier && second!.Equals("session", StringComparison.OrdinalIgnoreCase)
                    && name is not null && name.Equals("authorization", StringComparison.OrdinalIgnoreCase))
                {
                    return "SET SESSION AUTHORIZATION";
                }

                return name is not null && SessionSettings.Contains(name) ? "SET " + name.ToUpperInvariant() : null;
            case "ALTER" when second is not null && (second.Equals("role", StringComparison.OrdinalIgnoreCase)
                    || second.Equals("database", StringComparison.OrdinalIgnoreCase)
                    || second.Equals("system", StringComparison.OrdinalIgnoreCase)
                    || (second.Equals("user", StringComparison.OrdinalIgnoreCase) && !string.Equals(Word(2), "mapping", StringComparison.OrdinalIgnoreCase))):
                return "ALTER " + second.ToUpperInvariant();
            default:
                return null;
        }
    }

    // The index of the relation a write statement targets, at any position (a CTE, PREPARE), or -1: UPDATE [ONLY] x,
    // INSERT INTO x, DELETE FROM [ONLY] x, MERGE INTO [ONLY] x.
    private static int WriteTarget(List<Token> tokens, int i)
    {
        bool At(int index, string word) => index < tokens.Count && IsWord(tokens[index], word);

        var target = At(i, "update") ? i + 1
            : (At(i, "insert") && At(i + 1, "into")) || (At(i, "delete") && At(i + 1, "from")) || (At(i, "merge") && At(i + 1, "into")) ? i + 2
            : -1;
        return target >= 0 && At(target, "only") ? target + 1 : target;
    }

    // pg_settings alone (not the schema of something else), or pg_catalog.pg_settings.
    private static bool NamesPgSettings(List<Token> tokens, int index) =>
        index < tokens.Count
        && ((IsWord(tokens[index], "pg_settings") && (index + 1 >= tokens.Count || tokens[index + 1].Kind != Kind.Dot))
            || (IsWord(tokens[index], "pg_catalog") && index + 2 < tokens.Count && tokens[index + 1].Kind == Kind.Dot
                && IsWord(tokens[index + 2], "pg_settings")));

    // The first argument must be exactly one plain string constant, then a comma, naming a setting that is not a session one.
    private static bool IsHarmlessSetConfig(List<Token> tokens, int index) =>
        index + 1 < tokens.Count
        && tokens[index].Kind == Kind.PlainString
        && tokens[index + 1].Kind == Kind.Comma
        && tokens[index].Value.Trim().Length > 0
        && !SessionSettings.Contains(tokens[index].Value.Trim());

    private static bool IsWord(Token token, string name) =>
        token.Kind is Kind.Word or Kind.QuotedWord && token.Value.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c > '\u007f';

    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || char.IsDigit(c) || c == '$';

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && At(sql, i + 1, '-'))
            {
                var newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
            }
            else if (c == '/' && At(sql, i + 1, '*'))
            {
                i = BlockCommentEnd(sql, i);
            }
            else if (c == '\'')
            {
                i = ReadString(sql, i, backslash: false, Kind.PlainString, tokens);
            }
            else if (c == '"')
            {
                i = ReadQuotedIdentifier(sql, i, Kind.QuotedWord, tokens);
            }
            else if ((c is 'U' or 'u') && At(sql, i + 1, '&') && (At(sql, i + 2, '\'') || At(sql, i + 2, '"')))
            {
                i = At(sql, i + 2, '"')
                    ? ReadQuotedIdentifier(sql, i + 2, Kind.UnicodeWord, tokens)
                    : ReadString(sql, i + 2, backslash: false, Kind.OtherString, tokens);
            }
            else if ((c is 'E' or 'e') && At(sql, i + 1, '\''))
            {
                i = ReadString(sql, i + 1, backslash: true, Kind.OtherString, tokens);
            }
            else if ((c is 'B' or 'b' or 'X' or 'x' or 'N' or 'n') && At(sql, i + 1, '\''))
            {
                i = ReadString(sql, i + 1, backslash: false, Kind.OtherString, tokens);
            }
            else if (IsIdentifierStart(c))
            {
                var end = i + 1;
                while (end < sql.Length && IsIdentifierPart(sql[end]))
                {
                    end++;
                }

                tokens.Add(new Token(Kind.Word, sql[i..end]));
                i = end;
            }
            else if (c == '$' && DollarTagLength(sql, i) is var tagLength && tagLength > 0)
            {
                // Only here, where a token starts: a $ inside an identifier (x$a$) was read with the identifier.
                var tag = sql.Substring(i, tagLength);
                var close = sql.IndexOf(tag, i + tagLength, StringComparison.Ordinal);
                tokens.Add(new Token(Kind.OtherString, string.Empty));
                i = close < 0 ? sql.Length : close + tagLength;
            }
            else
            {
                tokens.Add(new Token(
                    c switch
                    {
                        ';' => Kind.Semicolon,
                        ',' => Kind.Comma,
                        '.' => Kind.Dot,
                        '(' => Kind.OpenParen,
                        _ => Kind.Other,
                    },
                    string.Empty));
                i++;
            }
        }

        return tokens;
    }

    // $$ or $tag$ where the tag is an identifier without $; 0 when not a dollar quote ($1 is a parameter).
    private static int DollarTagLength(string sql, int start)
    {
        var i = start + 1;
        if (i < sql.Length && IsIdentifierStart(sql[i]))
        {
            i++;
            while (i < sql.Length && (IsIdentifierStart(sql[i]) || char.IsDigit(sql[i])))
            {
                i++;
            }
        }

        return i < sql.Length && sql[i] == '$' ? i - start + 1 : 0;
    }

    private static int ReadString(string sql, int start, bool backslash, Kind kind, List<Token> tokens)
    {
        var value = new System.Text.StringBuilder();
        var i = start + 1;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (backslash && c == '\\')
            {
                if (i + 1 < sql.Length)
                {
                    value.Append(sql[i + 1]);
                }

                i += 2;
                continue;
            }

            if (c == '\'')
            {
                if (At(sql, i + 1, '\''))
                {
                    value.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            if (kind == Kind.PlainString && value.Length < 128)
            {
                value.Append(c);
            }

            i++;
        }

        tokens.Add(new Token(kind, kind == Kind.PlainString ? value.ToString() : string.Empty));
        return i;
    }

    private static int ReadQuotedIdentifier(string sql, int start, Kind kind, List<Token> tokens)
    {
        var value = new System.Text.StringBuilder();
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == '"')
            {
                if (At(sql, i + 1, '"'))
                {
                    value.Append('"');
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            value.Append(sql[i]);
            i++;
        }

        tokens.Add(new Token(kind, value.ToString()));
        return i;
    }

    private static bool At(string text, int index, char expected) => index < text.Length && text[index] == expected;

    private static int BlockCommentEnd(string sql, int start)
    {
        var depth = 0;
        var i = start;
        while (i < sql.Length - 1)
        {
            if (sql[i] == '/' && sql[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && sql[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return sql.Length;
    }

    private readonly record struct Token(Kind Kind, string Value);
}
