namespace MyApp.Api.Tests.Architecture.Modular.ContractsSamples;

// Sample types for the self-tests of the shape rule of a Contracts assembly: what is accepted and each kind of type that
// is not. They are not endpoints, handlers or modules, so nothing that scans the test assembly picks them up.

public interface ISample
{
    string Name { get; }
}

public sealed record SampleRecord(string Name);

public readonly record struct SampleRecordStruct(int Value);

public enum SampleKind
{
    First,
    Second,
}

/// <summary>The marker the rule exempts, as the Contracts assembly of a module named Sample would hold it.</summary>
public static class SampleContractsAssembly;

/// <summary>The marker of another module: not exempt in the Contracts of Sample.</summary>
public static class OtherContractsAssembly;

public sealed class SampleService
{
    private readonly string _name = "service";

    public string Name => _name;
}

public abstract class SampleAbstract;

public static class SampleStaticHelper
{
    public static string Name => "helper";
}

public struct SamplePlainStruct
{
    public int Value { get; set; }
}

public delegate void SampleCallback();

/// <summary>Holds a lambda, so the compiler generates a closure class for it.</summary>
public static class ContractsSamplesCompilerGenerated
{
    public static Func<int> Make() => static () => 1;
}

public static class Impostor
{
    /// <summary>A second type with the name of the marker, in another namespace: not a static class.</summary>
    public sealed class SampleContractsAssembly;
}
