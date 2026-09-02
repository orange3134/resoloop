using System.Text.RegularExpressions;
using RLoop.Core;

namespace RLoop.Flux;

public sealed record FluxManagedDataProbeResult(
    bool Success,
    bool AutoDiscovery,
    string? LibraryPath,
    int? LoadedNodes,
    string Message,
    IReadOnlyList<FluxDiagnostic> Diagnostics);

public static partial class FluxManagedDataProbe
{
    public static async Task<FluxManagedDataProbeResult> RunAsync(IFluxTool flux, string? libraryPath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-flux-doctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "RLoopDoctor.pg");
        await File.WriteAllTextAsync(source, "module RLoopDoctor where { }\n", cancellationToken);
        try
        {
            var result = await flux.CheckAsync(new FluxBuildRequest(source, directory, null, libraryPath), cancellationToken);
            var combined = result.StandardOutput + "\n" + result.StandardError;
            var match = LoadedNodeCount().Match(combined);
            var loadedNodes = match.Success ? int.Parse(match.Groups[1].Value) : (int?)null;
            var diagnostics = result.Diagnostics ?? [];
            var message = result.Success
                ? loadedNodes is null ? "Flux-SDK check/build probe resolved the Froox libraries." :
                    $"Flux-SDK check/build probe loaded {loadedNodes} Froox nodes."
                : (result.PrimaryDiagnostics ?? diagnostics).FirstOrDefault()?.Message ??
                  combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ??
                  $"Flux-SDK check/build probe failed with exit code {result.ExitCode}.";
            return new FluxManagedDataProbeResult(result.Success, string.IsNullOrWhiteSpace(libraryPath),
                string.IsNullOrWhiteSpace(libraryPath) ? null : Path.GetFullPath(libraryPath), loadedNodes, message, diagnostics);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"Added\s+Froox\s+nodes:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LoadedNodeCount();
}
