using System.Collections.Frozen;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Cli;

/// <summary>The only list of built-in top-level command names. Built-ins always win over plugin commands.</summary>
public static class BuiltinCommandCatalog
{
    // Includes every name Program dispatches before and after connecting, plus the plugin management command.
    public static IReadOnlySet<string> Commands { get; } = new[]
    {
        "apply", "blender", "bundle", "capture", "component", "diff", "discover", "doctor", "find", "flux",
        "help", "hierarchy", "import", "init", "inspect", "item", "logs", "manifest", "observe", "ping",
        "plan", "plugin", "scene", "schema", "skills", "slot", "status", "test", "tool", "type", "uix",
        "validate", "version"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same reservations for install, command dispatch and doctor verification.</summary>
    public static PluginNameReservations Reservations { get; } =
        new(Commands, BundledSkillManager.Names, UixRecipes.Catalog.Select(recipe => recipe.Name));

    public static bool IsBuiltin(string name) => Commands.Contains(name);
}
