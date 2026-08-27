using System.Diagnostics;
using System.Text.RegularExpressions;
using RLoop.Core;

namespace RLoop.Flux;

public sealed class FluxProcessTool(string executable, IFluxDeployer deployer) : IFluxTool
{
    public Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) =>
        RunBuild(request, [], cancellationToken);

    public Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) =>
        RunBuild(request, ["--stop-after-resolve"], cancellationToken);

    public Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) =>
        RunBuild(request, ["--watch"], cancellationToken);

    public Task<FluxResult> DeployAsync(FluxDeployRequest request, CancellationToken cancellationToken = default) =>
        deployer.DeployAsync(request, cancellationToken);

    public async Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Run(["--version"], Environment.CurrentDirectory, cancellationToken);
            var text = (result.StandardOutput + " " + result.StandardError).Trim();
            var version = Regex.Match(text, @"\d+\.\d+\.\d+(?:[-+][^\s]+)?").Value;
            return new FluxToolStatus(result.ExitCode == 0 || !string.IsNullOrEmpty(version), executable, string.IsNullOrEmpty(version) ? text : version);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new FluxToolStatus(false, executable, null);
        }
    }

    private Task<FluxResult> RunBuild(FluxBuildRequest request, IReadOnlyList<string> extra, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(request.Source);
        if (!File.Exists(source))
            throw new RLoopException("FLUX_SOURCE_NOT_FOUND", $"ProtoGraph source '{source}' does not exist.", ExitCodes.NotFound);
        var args = new List<string> { "build" };
        if (!string.IsNullOrWhiteSpace(request.ProjectDirectory)) { args.Add("--project-directory"); args.Add(Path.GetFullPath(request.ProjectDirectory)); }
        if (!string.IsNullOrWhiteSpace(request.Output)) { args.Add("--out"); args.Add(Path.GetFullPath(request.Output)); }
        if (!string.IsNullOrWhiteSpace(request.LibraryPath)) { args.Add("--library-path"); args.Add(Path.GetFullPath(request.LibraryPath)); }
        if (request.CompactErrors) args.Add("--compact-error-messages");
        args.AddRange(extra);
        args.Add(source);
        return Run(args, request.ProjectDirectory ?? Path.GetDirectoryName(source)!, cancellationToken);
    }

    private async Task<FluxResult> Run(IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
            var outIndex = args.Select((value, index) => (value, index)).FirstOrDefault(x => x.value == "--out").index;
            var outputPath = args.Contains("--out") && outIndex + 1 < args.Count ? args[outIndex + 1] : null;
            var standardOutput = await stdout;
            var standardError = await stderr;
            var diagnostics = FluxDiagnostics.Parse(standardOutput, standardError);
            return new FluxResult(process.ExitCode == 0, process.ExitCode, standardOutput, standardError, outputPath,
                diagnostics, diagnostics.Where(diagnostic => diagnostic.IsPrimary).ToArray());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RLoopException("FLUX_SDK_NOT_FOUND", $"Could not start Flux-SDK executable '{executable}'.", ExitCodes.ExternalToolFailed,
                new Dictionary<string, object?> { ["executable"] = executable },
                ["Install with: dotnet tool install --global Papaltine.FluxSDK --version 1.9.0", "Or configure RLOOP_FLUX_EXECUTABLE."], ex);
        }
    }
}

public static partial class FluxDiagnostics
{
    private static readonly Regex Location = new(
        @"^(?<file>.+?)\((?<sl>\d+),(?<sc>\d+),(?<el>\d+),(?<ec>\d+)\):\s*(?<severity>error|warning|hint):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<FluxDiagnostic> Parse(string standardOutput, string standardError)
    {
        var diagnostics = new List<FluxDiagnostic>();
        ParseChannel(standardOutput, "stdout", diagnostics);
        ParseChannel(standardError, "stderr", diagnostics);
        return diagnostics;
    }

    private static void ParseChannel(string text, string channel, List<FluxDiagnostic> diagnostics)
    {
        FluxDiagnostic? current = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Location.Match(line);
            if (match.Success)
            {
                if (current is not null) diagnostics.Add(current);
                var severity = match.Groups["severity"].Value.ToLowerInvariant();
                var message = match.Groups["message"].Value;
                var category = Classify(message, severity);
                current = new FluxDiagnostic(match.Groups["file"].Value,
                    int.Parse(match.Groups["sl"].Value), int.Parse(match.Groups["sc"].Value),
                    int.Parse(match.Groups["el"].Value), int.Parse(match.Groups["ec"].Value),
                    severity, message, channel, category, IsPrimary(severity, message, category));
            }
            else if (current is not null && string.IsNullOrWhiteSpace(line))
            {
                diagnostics.Add(current);
                current = null;
            }
            else if (current is not null)
                current = current with { Message = current.Message + "\n" + line };
        }
        if (current is not null) diagnostics.Add(current);
    }

    private static string Classify(string message, string severity)
    {
        if (message.Contains("bottom value", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Skipping node generation", StringComparison.OrdinalIgnoreCase)) return "cascade";
        if (message.Contains("parse", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Expecting:", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Error in ", StringComparison.OrdinalIgnoreCase)) return "parse";
        if (message.Contains("type", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("constraint", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unif", StringComparison.OrdinalIgnoreCase)) return "type";
        return severity == "hint" ? "hint" : "compiler";
    }

    private static bool IsPrimary(string severity, string message, string category) =>
        severity == "error" && category != "cascade" &&
        !message.StartsWith("Error in ", StringComparison.OrdinalIgnoreCase);
}
