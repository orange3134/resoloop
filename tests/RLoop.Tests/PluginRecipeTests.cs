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
        var otherProject = Project("cwd ワールド");
        AddPlugin(otherProject, "aa-plugin", "別ワールド");
        using (var positive = new PluginRecipeResolver(Registry(otherProject)))
            Assert.NotEmpty(positive.Read("aa-plugin:sample-card"));
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
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(otherProject);
            Assert.Throws<RLoopException>(() => UixRecipes.Read("aa-plugin:sample-card"));
        }
        finally { Directory.SetCurrentDirectory(previous); }
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

    // S: selecting the include directory or cwd silently reads another world's same-named plugin.
    [Fact]
    public void MainInputWinsOverIncludeAndWorkingDirectory()
    {
        var mainProject = Project("主 ワールド");
        var otherProject = Project("別 ワールド");
        AddPlugin(mainProject, "aa-plugin", "正しい主入力");
        AddPlugin(otherProject, "aa-plugin", "間違った別入力");
        var include = Path.Combine(otherProject, "include.json");
        File.WriteAllText(include, Document("aa-plugin:sample-card"));
        var main = Path.Combine(mainProject, "main.json");
        File.WriteAllText(main, JsonSerializer.Serialize(new { include }));
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(otherProject);
            var first = ApplyDocumentCompiler.Compile(main, "0.2.0", Reservations());
            var second = ApplyDocumentCompiler.Compile(main, "0.2.0", Reservations());
            Assert.Equal(first.Json, second.Json);
            using var expanded = JsonDocument.Parse(first.Json);
            var slot = expanded.RootElement.GetProperty("children")[0].GetProperty("slot");
            Assert.Equal("正しい主入力", slot.GetProperty("name").GetString());
            Assert.Equal("uix-plugin--aa-plugin--sample-card--panel_1", slot.GetProperty("key").GetString());
        }
        finally { Directory.SetCurrentDirectory(previous); }
    }

    // S: missing plugins must not look successful; failed expansion must not strand the project lease.
    [Fact]
    public void MissingPluginAndFailedExpansionReleaseLease()
    {
        var project = Project("失敗と復旧");
        AddPlugin(project, "aa-plugin", "正常");
        var path = Path.Combine(project, "main.json");
        var valid = Document("aa-plugin:sample-card");
        File.WriteAllText(path, valid);
        var expected = ApplyDocumentCompiler.Compile(path, "0.2.0", Reservations()).Json;
        File.WriteAllText(path, Document("zz-plugin:sample-card"));
        Assert.Equal("PLUGIN_NOT_INSTALLED", Assert.Throws<RLoopException>(() =>
            ApplyDocumentCompiler.Compile(path, "0.2.0", Reservations())).Code);
        File.WriteAllText(path, valid.Replace("\"key\"", "\"wrong\"", StringComparison.Ordinal));
        Assert.Throws<RLoopException>(() => ApplyDocumentCompiler.Compile(path, "0.2.0", Reservations()));
        File.WriteAllText(path, valid);
        Assert.Equal(expected, ApplyDocumentCompiler.Compile(path, "0.2.0", Reservations()).Json);
    }

    private static string Document(string recipe) => JsonSerializer.Serialize(new
    {
        children = new[] { new Dictionary<string, object> { ["$recipe"] = recipe, ["$with"] = new { key = "panel_1" } } }
    });

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
