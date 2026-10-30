using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

/// <summary>The argv and environment a plugin command receives (create-reso-world#98 calling convention).</summary>
public static class PluginRuntimeEnvironment
{
    public const string Prefix = "RESOLOOP_PLUGIN_";
    public const string NameVariable = "RESOLOOP_PLUGIN_NAME";
    public const string CommandVariable = "RESOLOOP_PLUGIN_COMMAND";
    public const string RootVariable = "RESOLOOP_PLUGIN_ROOT";
    public const string DataVariable = "RESOLOOP_PLUGIN_DATA";
    public const string ProjectRootVariable = "RESOLOOP_PLUGIN_PROJECT_ROOT";
    public const string HostArgvVariable = "RESOLOOP_PLUGIN_HOST_ARGV";
    public const string HostVersionVariable = "RESOLOOP_PLUGIN_HOST_VERSION";
    private static readonly Regex Substitution = new(@"\$\{(pluginRoot|pluginData)\}", RegexOptions.CultureInvariant);

    /// <summary>Manifest args with the two validated substitutions, followed by the user's tokens unchanged.</summary>
    public static IReadOnlyList<string> ExpandArguments(InstalledPlugin plugin, PluginCommand command, IReadOnlyList<string> userArguments)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(userArguments);
        // One pass: a substituted path that happens to contain "${...}" text is never expanded again.
        var manifest = command.Args.Select(argument => Substitution.Replace(argument,
            match => match.Groups[1].Value == "pluginRoot" ? plugin.PackageDirectory : plugin.DataDirectory));
        return manifest.Concat(userArguments).ToArray().AsReadOnly();
    }

    /// <summary>The inherited environment without any stale plugin variables, plus this call's variables.</summary>
    public static IReadOnlyDictionary<string, string> Create(PluginCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var windows = OperatingSystem.IsWindows();
        var environment = new Dictionary<string, string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in request.BaseEnvironment)
        {
            // A parent plugin call (or a user shell) must not point this call at another project or plugin.
            if (value is null || key.StartsWith(Prefix, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            environment[key] = value;
        }
        var data = request.Plugin.DataDirectory;
        environment[NameVariable] = request.Plugin.Name;
        environment[CommandVariable] = request.Command.Name;
        environment[RootVariable] = request.Plugin.PackageDirectory;
        environment[DataVariable] = data;
        environment[ProjectRootVariable] = request.ProjectRoot;
        environment[HostArgvVariable] = JsonSerializer.Serialize(request.HostArgv);
        environment[HostVersionVariable] = request.HostVersion;
        // Runtime caches belong to data/: package/ is verified by hash before every use.
        environment["PYTHONPYCACHEPREFIX"] = Path.Combine(data, ".cache", "python");
        environment["UV_PROJECT_ENVIRONMENT"] = Path.Combine(data, ".venv");
        return new ReadOnlyDictionary<string, string>(environment);
    }
}
