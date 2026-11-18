using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Cli;

/// <summary>A plugin command call: tokens after the command name are passed to the plugin unchanged.</summary>
public sealed record PluginInvocation(string? Project, string Command, IReadOnlyList<string> Arguments);

/// <summary>What a plugin call inherits from the host process. Tests and embedding hosts pass explicit values.</summary>
public sealed record PluginDispatchContext(string ResoloopVersion, string StartDirectory,
    IReadOnlyDictionary<string, string?> Environment, IReadOnlyList<string> HostArgv)
{
    public static PluginDispatchContext Current(string resoloopVersion)
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value;
        return new(resoloopVersion, System.Environment.CurrentDirectory, environment.AsReadOnly(), CurrentHostArgv());
    }

    // An installed tool or apphost is one executable; `dotnet resoloop.dll` needs the muxer and the assembly.
    // Empty means undetermined: the runner then refuses to start instead of guessing another executable.
    private static IReadOnlyList<string> CurrentHostArgv()
    {
        var process = System.Environment.ProcessPath;
        if (string.IsNullOrEmpty(process)) return [];
        if (!Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return [process];
        var entry = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(entry) ? [] : [process, entry];
    }
}

public static class PluginCommandDispatch
{
    /// <summary>
    /// Separate a plugin call from the raw argv BEFORE the built-in parser consumes options.
    /// Returns null when the arguments belong to the built-in parser (no command, an option first, or a built-in name).
    /// </summary>
    public static PluginInvocation? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var projects = new List<string>();
        var index = 0;
        while (index < args.Count)
        {
            var token = args[index];
            if (token == "--project")
            {
                if (index + 1 >= args.Count) return null;
                projects.Add(args[index + 1]);
                index += 2;
            }
            else if (token.StartsWith("--project=", StringComparison.Ordinal))
            {
                projects.Add(token["--project=".Length..]);
                index++;
            }
            else break;
        }
        if (index >= args.Count) return null;
        var command = args[index];
        if (command.StartsWith('-') || BuiltinCommandCatalog.IsBuiltin(command)) return null;
        if (projects.Count > 1)
            throw new RLoopException("INVALID_ARGUMENT", "--project may be given only once before a plugin command.", ExitCodes.InvalidArguments);
        if (projects.Count == 1 && string.IsNullOrWhiteSpace(projects[0]))
            throw new RLoopException("INVALID_ARGUMENT", "--project requires a project directory.", ExitCodes.InvalidArguments);
        return new PluginInvocation(projects.Count == 1 ? projects[0] : null, command, args.Skip(index + 1).ToArray().AsReadOnly());
    }

    /// <summary>
    /// Resolve and run a plugin command before any ResoniteLink discovery/connection. Returns the child's exit code;
    /// host failures are thrown as RLoopException. The plugin lease is held until the process tree has stopped.
    /// </summary>
    public static async Task<int> RunAsync(PluginInvocation invocation, PluginDispatchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(context);
        string projectRoot;
        try { projectRoot = PluginRegistry.FindProjectRoot(invocation.Project, context.StartDirectory); }
        catch (RLoopException ex) when (ex.Code == "PLUGIN_PROJECT_NOT_FOUND" && invocation.Project is null)
        {
            // Outside any project a mistyped name stays an unknown command.
            throw UnknownCommand(invocation.Command, ex);
        }
        var registry = new PluginRegistry(projectRoot, context.ResoloopVersion, BuiltinCommandCatalog.Reservations);
        var config = ConfigResolver.Resolve(registry.ProjectRoot, new Dictionary<string, string?>(),
            key => context.Environment.GetValueOrDefault(key)).Config;
        using var lease = registry.OpenVerified();
        var match = lease.Plugins.SelectMany(plugin => plugin.Manifest.Commands.Select(command => (Plugin: plugin, Command: command)))
            .FirstOrDefault(item => item.Command.Name.Equals(invocation.Command, StringComparison.OrdinalIgnoreCase));
        if (match.Plugin is null) throw UnknownCommand(invocation.Command);
        var timeout = TimeSpan.FromSeconds(match.Command.TimeoutSeconds ?? config.CommandTimeoutSeconds);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var signals = new SignalCancellation(cancellation);
        return await PluginCommandRunner.RunAsync(new PluginCommandRequest(match.Plugin, match.Command, registry.ProjectRoot,
            invocation.Arguments, context.HostArgv, context.ResoloopVersion, timeout, context.Environment), cancellation.Token).ConfigureAwait(false);
    }

    private static RLoopException UnknownCommand(string command, Exception? innerException = null) =>
        new("UNKNOWN_COMMAND", $"Unknown command '{command}'.", ExitCodes.InvalidArguments,
            suggestions: ["Run resoloop help to list commands, or pass --project DIR for a plugin command."], innerException: innerException);

    // While a plugin runs, termination requests become a cancellation so the process tree is stopped and
    // confirmed before the host exits. On Linux the child is outside the terminal's foreground group.
    private sealed class SignalCancellation : IDisposable
    {
        private readonly List<PosixSignalRegistration> _registrations = [];

        public SignalCancellation(CancellationTokenSource cancellation)
        {
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
            {
                try
                {
                    _registrations.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true;
                        cancellation.Cancel();
                    }));
                }
                catch (PlatformNotSupportedException) { }
            }
        }

        public void Dispose()
        {
            foreach (var registration in _registrations) registration.Dispose();
        }
    }
}
