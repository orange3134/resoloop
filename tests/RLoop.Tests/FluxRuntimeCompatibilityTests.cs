using RLoop.Core;
using RLoop.Flux;

namespace RLoop.Tests;

public sealed class FluxRuntimeCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-flux-compat-" + Guid.NewGuid().ToString("N"));

    public FluxRuntimeCompatibilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void QuarantinesObservedRangeLoopIntRuntimeFailureOnlyForExactVersionPair()
    {
        File.WriteAllText(Path.Combine(_root, "Loop.pg"), """
            module Loop
            where { RangeLoopInt(Count=3); }
            """);
        var manifest = Path.Combine(_root, "resoloop.flux.json");
        File.WriteAllText(manifest, """
            {"schemaVersion":"1","modules":[{"name":"loop","source":"Loop.pg","module":"Loop"}]}
            """);

        var error = Assert.Throws<RLoopException>(() => FluxRuntimeCompatibility.ThrowIfKnownIncompatible(manifest,
            "2026.9.2.1275", "1.9.0+e674229f9b959ac8e6615ff35a2cd1d7971503e8"));
        var otherVersion = FluxRuntimeCompatibility.CheckManifest(manifest, "2026.9.3.0",
            "1.9.0+e674229f9b959ac8e6615ff35a2cd1d7971503e8");

        Assert.Equal("FLUX_RUNTIME_NODE_INCOMPATIBLE", error.Code);
        Assert.Empty(otherVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
