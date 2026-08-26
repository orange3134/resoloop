namespace RLoop.Flux;

public sealed record FluxCompatibilityResult(bool Compatible, string? Version, string Expected, string Message);

public static class FluxCompatibility
{
    public const string SupportedSeries = "1.9";

    public static FluxCompatibilityResult Check(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return new FluxCompatibilityResult(false, version, SupportedSeries + ".x", "Flux-SDK version could not be determined.");
        var compatible = version.Equals(SupportedSeries, StringComparison.Ordinal) || version.StartsWith(SupportedSeries + ".", StringComparison.Ordinal);
        return new FluxCompatibilityResult(compatible, version, SupportedSeries + ".x",
            compatible ? "Flux-SDK is in the adapter-tested compatibility series." : $"Flux-SDK {version} is outside tested series {SupportedSeries}.x.");
    }
}
