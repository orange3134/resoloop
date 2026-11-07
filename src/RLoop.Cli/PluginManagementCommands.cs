using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Cli;

/// <summary>`resoloop plugin install|list|remove`: offline project management; never starts plugin code.</summary>
public static class PluginManagementCommands
{
    public static void Run(ParsedArguments args, OutputWriter output, string resoloopVersion)
    {
        var subcommand = args.Positional(1, "plugin subcommand (install, list, remove)").ToLowerInvariant();
        var root = PluginRegistry.FindProjectRoot(args.Option("project"), Environment.CurrentDirectory);
        var registry = new PluginRegistry(root, resoloopVersion, BuiltinCommandCatalog.Reservations);
        switch (subcommand)
        {
            case "install":
            {
                RequireCount(args, 3, "plugin install SOURCE [--ref GIT_REF]");
                var plugin = new PluginInstaller(registry).Install(args.Positional(2, "plugin source"), args.Option("ref"));
                output.Success(new
                {
                    name = plugin.Name, version = plugin.Record.Version, installDirectory = plugin.InstallDirectory,
                    source = plugin.Record.Source, commit = plugin.Record.Commit
                }, writer =>
                {
                    writer.WriteLine($"installed {plugin.Name} {plugin.Record.Version}");
                    writer.WriteLine($"  {plugin.InstallDirectory}");
                });
                break;
            }
            case "list":
            {
                RequireCount(args, 2, "plugin list");
                var statuses = registry.List();
                output.Success(statuses.Select(status => new
                {
                    name = status.Name, ok = status.Ok, version = status.Record?.Version ?? status.Manifest?.Version,
                    problem = status.Problem is null ? null : new { status.Problem.Code, status.Problem.Message, status.Problem.Suggestions }
                }), writer =>
                {
                    if (statuses.Count == 0) writer.WriteLine("No plugins installed in " + registry.ProjectRoot);
                    foreach (var status in statuses)
                        writer.WriteLine(status.Problem is { } problem
                            ? $"{status.Name}  PROBLEM {problem.Code}: {problem.Message}"
                            : $"{status.Name}  {status.Record?.Version}");
                });
                break;
            }
            case "remove":
            {
                RequireCount(args, 3, "plugin remove NAME --yes");
                var name = args.Positional(2, "plugin name");
                new PluginInstaller(registry).Remove(name, args.Has("yes"));
                output.Success(new { name, removed = true }, writer => writer.WriteLine($"removed {name}"));
                break;
            }
            default:
                throw Program.UnknownCommand("plugin " + subcommand);
        }
    }

    private static void RequireCount(ParsedArguments args, int count, string usage)
    {
        if (args.Positionals.Count != count)
            throw new RLoopException("UNEXPECTED_ARGUMENT", "Usage: resoloop " + usage, ExitCodes.InvalidArguments);
    }
}
