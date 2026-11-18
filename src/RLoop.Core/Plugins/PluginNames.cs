using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

public sealed class PluginNameReservations
{
    public PluginNameReservations(IEnumerable<string> commands, IEnumerable<string> skills, IEnumerable<string> recipes)
    {
        Commands = commands.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        Skills = skills.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        Recipes = recipes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Commands { get; }
    public IReadOnlySet<string> Skills { get; }
    public IReadOnlySet<string> Recipes { get; }
}

public static class PluginNames
{
    private static readonly Regex Pattern = new(@"\A[a-z][a-z0-9]*(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);
    public static bool IsValid(string? value) => value is { Length: >= 2 and <= 40 } && Pattern.IsMatch(value);

    public static IReadOnlyList<string> FindConflicts(PluginManifest manifest, IEnumerable<PluginManifest> installedManifests, PluginNameReservations reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        var conflicts = new List<string>();
        var installed = installedManifests.ToArray();
        if (installed.Any(other => Same(other.Name, manifest.Name)))
            conflicts.Add($"Plugin name '{manifest.Name}' is already installed.");
        if (reservations.Commands.Contains(manifest.Name) || reservations.Skills.Contains(manifest.Name))
            conflicts.Add($"Plugin name '{manifest.Name}' is reserved.");
        foreach (var command in manifest.Commands)
            if (reservations.Commands.Contains(command.Name) || installed.Any(other => other.Commands.Any(item => Same(item.Name, command.Name))))
                conflicts.Add($"Command name '{command.Name}' conflicts with another command.");
        foreach (var skill in manifest.Skills)
            if (skill.Name.StartsWith("resonite-", StringComparison.OrdinalIgnoreCase) || reservations.Skills.Contains(skill.Name)
                || installed.Any(other => other.Skills.Any(item => Same(item.Name, skill.Name))))
                conflicts.Add($"Skill name '{skill.Name}' is reserved or conflicts with another skill.");
        foreach (var recipe in manifest.Recipes)
            if (reservations.Recipes.Contains(recipe.Name))
                conflicts.Add($"Recipe name '{recipe.Name}' is reserved.");
        // Recipe effective names include the plugin namespace; duplicate plugin names are checked above.
        return conflicts.AsReadOnly();
    }

    private static bool Same(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
}
