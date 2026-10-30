using RLoop.Core;

namespace RLoop.Cli;

/// <summary>A plugin command call: tokens after the command name are passed to the plugin unchanged.</summary>
public sealed record PluginInvocation(string? Project, string Command, IReadOnlyList<string> Arguments);

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
}
