using System.Collections.Frozen;
using System.Text;

namespace RLoop.Core.Plugins;

/// <summary>A project-scoped recipe snapshot. Dispose after the last read or compilation.</summary>
public sealed class PluginRecipeResolver : IDisposable
{
    private readonly PluginRegistry _registry;
    private PluginReadLease? _lease;
    private FrozenDictionary<string, RecipeContent>? _recipes;
    private IReadOnlyList<UixRecipe>? _catalog;
    private bool _disposed;

    public PluginRecipeResolver(PluginRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string ProjectRoot => _registry.ProjectRoot;

    public IReadOnlyList<UixRecipe> Catalog
    {
        get { EnsureLoaded(); return _catalog!; }
    }

    public static PluginRecipeResolver ForDocument(string mainInputPath, string resoloopVersion,
        PluginNameReservations reservations)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(mainInputPath))!;
        var root = PluginRegistry.FindProjectRoot(null, directory);
        return new(new PluginRegistry(root, resoloopVersion, reservations));
    }

    public UixRecipe Describe(string name) => Find(name).Recipe;
    public string Read(string name) => Find(name).Text;

    private RecipeContent Find(string name)
    {
        var parts = name.Split(':');
        if (parts.Length != 2 || !PluginNames.IsValid(parts[0]) || !PluginNames.IsValid(parts[1]))
            throw NotFound(name);
        EnsureLoaded();
        if (_recipes!.TryGetValue(name, out var content)) return content;
        if (!_lease!.Plugins.Any(plugin => plugin.Name == parts[0]))
            throw PluginErrors.Create("PLUGIN_NOT_INSTALLED", $"Plugin '{parts[0]}' required by recipe '{name}' is not installed.");
        throw NotFound(name);
    }

    private void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recipes is not null) return;
        var lease = _registry.OpenVerified();
        try
        {
            var recipes = new Dictionary<string, RecipeContent>(StringComparer.Ordinal);
            foreach (var plugin in lease.Plugins)
                foreach (var recipe in plugin.Manifest.Recipes)
                {
                    var name = plugin.Name + ":" + recipe.Name;
                    var path = PluginPathPolicy.Combine(plugin.PackageDirectory, recipe.Path, "PLUGIN_MODIFIED");
                    string text;
                    try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                    {
                        throw PluginErrors.Create("PLUGIN_MODIFIED", $"Could not read verified recipe '{name}'.", innerException: ex);
                    }
                    recipes.Add(name, new(new UixRecipe(name, recipe.Prototype, recipe.Purpose,
                        recipe.Parameters, recipe.Ports, recipe.Requirements), text));
                }
            var snapshot = recipes.ToFrozenDictionary(StringComparer.Ordinal);
            var catalog = Array.AsReadOnly(snapshot.Values.Select(content => content.Recipe)
                .OrderBy(recipe => recipe.Name, StringComparer.Ordinal).ToArray());
            _recipes = snapshot;
            _catalog = catalog;
            _lease = lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static RLoopException NotFound(string name) => new("UIX_RECIPE_NOT_FOUND",
        $"Unknown UIX recipe '{name}'.", ExitCodes.NotFound);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease?.Dispose();
        _lease = null;
    }

    private sealed record RecipeContent(UixRecipe Recipe, string Text);
}
