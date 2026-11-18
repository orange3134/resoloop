using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Cli;

/// <summary>Doctor checks for the selected project's plugins. Empty when there are no plugins, so doctor stays unchanged.</summary>
public static class PluginDoctorChecks
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<DoctorCheck>> RunAsync(string? explicitProject, string startDirectory, string resoloopVersion,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        string root;
        IReadOnlyList<PluginStatus> statuses;
        try
        {
            root = PluginRegistry.FindProjectRoot(explicitProject, startDirectory);
            statuses = new PluginRegistry(root, resoloopVersion, BuiltinCommandCatalog.Reservations).List();
        }
        catch (RLoopException ex) when (ex.Code == "PLUGIN_PROJECT_NOT_FOUND" && explicitProject is null)
        {
            return [];
        }
        catch (RLoopException ex)
        {
            return [new DoctorCheck("plugins", "fail", true, $"{ex.Code}: {ex.Message}", ex.Suggestions.FirstOrDefault())];
        }
        var checks = new List<DoctorCheck>();
        foreach (var status in statuses)
        {
            // A broken plugin makes every plugin command in this project refuse to run.
            if (status.Problem is { } problem)
            {
                checks.Add(new DoctorCheck("plugin:" + status.Name, "fail", true, $"{problem.Code}: {problem.Message}",
                    problem.Suggestions.FirstOrDefault() ?? $"Reinstall the plugin, or remove it with resoloop plugin remove {status.Name} --yes."));
                continue;
            }
            foreach (var tool in status.Manifest!.Tools)
                checks.Add(ToolCheck(await PluginToolProbe.ProbeAsync(status.Name, tool, root, environment, ProbeTimeout, cancellationToken)
                    .ConfigureAwait(false)));
        }
        return checks.AsReadOnly();
    }

    private static DoctorCheck ToolCheck(PluginToolProbeResult result)
    {
        var tool = result.Tool;
        var name = $"plugin:{result.Plugin}:{tool.Name}";
        var problem = tool.Required ? "fail" : "warning";
        var hint = tool.Hint ?? $"Install {tool.Executable} and make it available on PATH.";
        var invocation = string.Join(' ', tool.VersionArgs.Prepend(tool.Executable));
        return result.Outcome switch
        {
            "available" => new DoctorCheck(name, "pass", tool.Required,
                result.Version is null ? result.ExecutablePath! : $"{result.ExecutablePath} {result.Version}"),
            "missing" => new DoctorCheck(name, problem, tool.Required, $"'{tool.Executable}' was not found in an absolute PATH entry.", hint),
            "timed-out" => new DoctorCheck(name, problem, tool.Required,
                $"'{invocation}' did not finish within {ProbeTimeout.TotalSeconds:0} seconds.", hint),
            _ => new DoctorCheck(name, problem, tool.Required, result.ExitCode is { } code
                ? $"'{invocation}' exited with code {code}." : $"'{invocation}' could not be run.", hint)
        };
    }
}
