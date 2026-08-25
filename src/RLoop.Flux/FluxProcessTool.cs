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
            return new FluxResult(process.ExitCode == 0, process.ExitCode, await stdout, await stderr, outputPath);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RLoopException("FLUX_SDK_NOT_FOUND", $"Could not start Flux-SDK executable '{executable}'.", ExitCodes.ExternalToolFailed,
                new Dictionary<string, object?> { ["executable"] = executable },
                ["Install with: dotnet tool install --global Papaltine.FluxSDK --version 1.9.0", "Or configure RLOOP_FLUX_EXECUTABLE."], ex);
        }
    }
}
