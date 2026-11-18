namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>The answer to one question. Required: the template depends on it, so a FAIL makes the exit code non-zero.</summary>
public sealed class Outcome(string id, bool required)
{
    private readonly List<string> _facts = [];
    private readonly List<string> _failures = [];

    public string Id { get; } = id;

    public bool Required { get; } = required;

    public bool Passed => _failures.Count == 0;

    public string Summary { get; set; } = string.Empty;

    /// <summary>Prints and keeps an observed fact.</summary>
    public void Fact(string text)
    {
        _facts.Add(text);
        Console.WriteLine($"  {Id} | {text}");
    }

    /// <summary>Records a check; a false condition fails the question.</summary>
    public void Check(bool condition, string what)
    {
        if (!condition)
        {
            _failures.Add(what);
        }

        Console.WriteLine($"  {Id} | {(condition ? "ok  " : "FAIL")} {what}");
    }

    public string Line() =>
        $"{Id} {(Passed ? "PASS" : "FAIL")}{(Required ? string.Empty : " (not gating)")}: {Summary}" +
        (Passed ? string.Empty : $" Failed: {string.Join("; ", _failures)}");
}
