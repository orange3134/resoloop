using System.Text.Json;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

[CollectionDefinition("Plugin recipe environment", DisableParallelization = true)]
public sealed class PluginRecipeEnvironment;

[Collection("Plugin recipe environment")]
public sealed class PluginRecipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("RESOLOOP_PLUGIN_TEST_ROOT")
        ?? Path.GetTempPath(), "recipes-" + Guid.NewGuid().ToString("N"));
    private static PluginNameReservations Reservations() => new([], [], []);
    private static PluginRegistry Registry(string project) => new(project, "0.2.0", Reservations());

    // S: a bare/local-name index silently selects the wrong plugin; export must not destroy caller files.
    [Fact]
    public void CatalogRoundTripIsNamespacedAndExportNeverOverwrites()
    {
        var project = Project("主 ワールド");
        AddPlugin(project, "aa-plugin", "日本語 A");
        AddPlugin(project, "bb-plugin", "日本語 B");
        using var resolver = new PluginRecipeResolver(Registry(project));
        var entries = UixRecipes.List(resolver).Where(recipe => recipe.Name.Contains(':')).ToArray();
        Assert.Equal(new[] { "aa-plugin:sample-card", "bb-plugin:sample-card" }, entries.Select(recipe => recipe.Name));
        foreach (var entry in entries)
        {
            Assert.Equal(entry, UixRecipes.Describe(entry.Name, resolver));
            var text = UixRecipes.Read(entry.Name, resolver);
            using var parsed = JsonDocument.Parse(text);
            Assert.Equal(entry.Name.StartsWith("aa-", StringComparison.Ordinal) ? "日本語 A" : "日本語 B",
                parsed.RootElement.GetProperty("prototypes").GetProperty("sample.card").GetProperty("slot").GetProperty("name").GetString());
            var output = Path.Combine(_root, entry.Name.Split(':')[0] + ".json");
            Assert.Equal(output, UixRecipes.Export(entry.Name, output, resolver));
            Assert.Equal(text, File.ReadAllText(output));
            File.WriteAllText(output, "caller sentinel");
            Assert.Throws<RLoopException>(() => UixRecipes.Export(entry.Name, output, resolver));
            Assert.Equal("caller sentinel", File.ReadAllText(output));
        }
        Assert.Throws<RLoopException>(() => UixRecipes.Read("sample-card", resolver));
        Assert.Throws<RLoopException>(() => UixRecipes.Read("aa-plugin:sample-card"));
        Assert.Equal("button", UixRecipes.Describe("button", resolver).Name);
    }

    // S: skipping OpenVerified makes modified package JSON look like legitimate authoring input.
    [Fact]
    public void ModifiedPackageIsRejectedBeforeRecipeRead()
    {
        var project = Project("照合");
        var installed = AddPlugin(project, "aa-plugin", "original 日本語");
        string original;
        using (var valid = new PluginRecipeResolver(Registry(project)))
            original = valid.Read("aa-plugin:sample-card");
        var file = Path.Combine(installed.PackageDirectory, "内容.json");
        File.WriteAllText(file, original.Replace("original", "tampered", StringComparison.Ordinal));
        using (var invalid = new PluginRecipeResolver(Registry(project)))
            Assert.Equal("PLUGIN_MODIFIED", Assert.Throws<RLoopException>(() => invalid.Read("aa-plugin:sample-card")).Code);
        File.WriteAllText(file, original);
        using var restored = new PluginRecipeResolver(Registry(project));
        Assert.Equal(original, restored.Read("aa-plugin:sample-card"));
    }

    private string Project(string name)
    {
        var path = Path.Combine(_root, "worlds", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".resoloop.json"), "{}");
        return path;
    }

    private InstalledPlugin AddPlugin(string project, string name, string label)
    {
        var source = Path.Combine(_root, "packages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        var recipe = $$"""
            {
              "prototypes": {
                "sample.card": {
                  "slot": { "key": "${key}", "name": {{JsonSerializer.Serialize(label)}} }
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(source, "内容.json"), recipe);
        File.WriteAllText(Path.Combine(source, "resoloop-plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, name, version = "1.0.0",
            resoloop = new { minVersion = "0.2.0", maxVersionExclusive = "0.3.0" },
            recipes = new[] { new { name = "sample-card", path = "内容.json", prototype = "sample.card", purpose = "日本語のカード",
                parameters = new Dictionary<string, string> { ["key"] = "Stable key" } } }
        }));
        return new PluginInstaller(Registry(project)).Install(source);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
