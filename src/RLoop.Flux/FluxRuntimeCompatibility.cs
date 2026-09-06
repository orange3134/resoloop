using System.Text.RegularExpressions;
using RLoop.Core;

namespace RLoop.Flux;

public sealed record FluxRuntimeCompatibilityIssue(string Code, string Module, string Source, string Node,
    string ResoniteVersion, string FluxSdkVersion, string Message, string Workaround);

public static class FluxRuntimeCompatibility
{
    private const string ObservedResoniteVersion = "2026.9.2.1275";
    private const string ObservedFluxSdkVersion = "1.9.0+e674229f9b959ac8e6615ff35a2cd1d7971503e8";

    public static IReadOnlyList<FluxRuntimeCompatibilityIssue> CheckManifest(string manifestPath,
        string? resoniteVersion, string? fluxSdkVersion)
    {
        if (!string.Equals(resoniteVersion, ObservedResoniteVersion, StringComparison.Ordinal) ||
            !string.Equals(fluxSdkVersion, ObservedFluxSdkVersion, StringComparison.Ordinal)) return [];
        var manifest = FluxManifestOrchestrator.Inspect(manifestPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var issues = new List<FluxRuntimeCompatibilityIssue>();
        foreach (var module in manifest.Modules)
        {
            var source = Path.GetFullPath(module.Source, directory);
            if (!Regex.IsMatch(File.ReadAllText(source), @"\bRangeLoopInt\s*\(", RegexOptions.CultureInvariant)) continue;
            issues.Add(new FluxRuntimeCompatibilityIssue("FLUX_RUNTIME_NODE_INCOMPATIBLE", module.Name, source,
                "RangeLoopInt", resoniteVersion!, fluxSdkVersion!,
                "This exact Resonite/Flux-SDK pair was observed to build RangeLoopInt successfully but fail while loading the deployed record.",
                "Replace RangeLoopInt with a bounded While loop and an explicit int32 cursor."));
        }
        return issues;
    }

    public static void ThrowIfKnownIncompatible(string manifestPath, string? resoniteVersion, string? fluxSdkVersion)
    {
        var issues = CheckManifest(manifestPath, resoniteVersion, fluxSdkVersion);
        if (issues.Count == 0) return;
        throw new RLoopException("FLUX_RUNTIME_NODE_INCOMPATIBLE",
            "The manifest uses a node known to fail runtime loading on this exact Resonite/Flux-SDK pair.",
            ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["issues"] = issues },
            issues.Select(issue => issue.Workaround).Distinct(StringComparer.Ordinal).ToArray());
    }
}
