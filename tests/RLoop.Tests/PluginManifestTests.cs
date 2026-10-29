using System.Diagnostics;
using System.Text.Json;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-plugin-tests-" + Guid.NewGuid().ToString("N"));
    private static PluginNameReservations EmptyReservations() => new([], [], []);

    // S: accepting an escaping/aliased reference could read or execute outside the package.
    [Theory]
    [InlineData("../outside")]
    [InlineData("outside-link")]
    [InlineData("NUL")]
    public void PortablePathsKeepBoundaries(string unsafePath)
    {
        var package = CreatePackage();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "SKILL.md");
        File.WriteAllText(sentinel, "outside sentinel");
        var valid = PluginManifest.Read(package);
        Assert.Equal("./実行 files/run.sh", Assert.Single(valid.Commands).Program);
        Assert.Equal("技能 docs", Assert.Single(valid.Skills).Path);
        Assert.Equal("レシピ data/main.json", Assert.Single(valid.Recipes).Path);
        if (unsafePath == "outside-link")
            Directory.CreateSymbolicLink(Path.Combine(package, "outside-link"), outside);
        WriteManifest(package, skillPath: unsafePath);

        var error = Assert.Throws<RLoopException>(() => PluginManifest.Read(package));

        Assert.Equal("PLUGIN_MANIFEST_INVALID", error.Code);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<string>>(error.Context["errors"]));
        Assert.Equal("outside sentinel", File.ReadAllText(sentinel));
    }

    // S: overflow or build-sensitive ordering silently selects incompatible versions.
    [Fact]
    public void SafetyDefinitionsRemainStable_HugePrereleaseOrdering()
    {
        var lower = SemanticVersion.Parse("0.2.0-999999999999999999999999999999");
        var higher = SemanticVersion.Parse("0.2.0-1000000000000000000000000000000");
        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher.CompareTo(SemanticVersion.Parse("0.2.0")) < 0);
        Assert.Equal(0, SemanticVersion.Parse("0.2.0+first").CompareTo(SemanticVersion.Parse("0.2.0+second")));
    }

    // S: dropping either endpoint can report an unsupported plugin as compatible.
    [Fact]
    public void SafetyDefinitionsRemainStable_CompatibilityRange()
    {
        var manifest = PluginManifest.Read(CreatePackage());
        manifest.EnsureCompatible("0.2.0");
        Assert.Equal("PLUGIN_INCOMPATIBLE_VERSION", Assert.Throws<RLoopException>(() => manifest.EnsureCompatible("0.3.0")).Code);
        Assert.Equal("PLUGIN_INCOMPATIBLE_VERSION", Assert.Throws<RLoopException>(() => manifest.EnsureCompatible("0.1.9")).Code);
    }

    // S: retaining a caller's mutable catalog can silently release reserved names.
    [Fact]
    public void SafetyDefinitionsRemainStable_ReservationSnapshot()
    {
        var package = CreatePackage();
        var manifest = PluginManifest.Read(package);
        var commands = new List<string> { "RENDER-WORLD" };
        var reservations = new PluginNameReservations(commands, [], []);
        commands.Clear();
        Assert.NotEmpty(PluginNames.FindConflicts(manifest, [], reservations));
        Assert.Empty(PluginNames.FindConflicts(manifest, [], EmptyReservations()));
        Assert.NotEmpty(PluginNames.FindConflicts(manifest, [manifest], EmptyReservations()));
    }

    // S: last-wins dictionaries silently change recipe parameter meaning.
    [Fact]
    public void SafetyDefinitionsRemainStable_DuplicateDictionaryRejected()
    {
        var package = CreatePackage();
        Assert.Single(PluginManifest.Read(package).Recipes);
        var file = Path.Combine(package, "resoloop-plugin.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"size\": \"number\"", "\"size\": \"number\", \"size\": \"string\"", StringComparison.Ordinal));
        var error = Assert.Throws<RLoopException>(() => PluginManifest.Read(package));
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<string>>(error.Context["errors"]), value => value.Contains("$.recipes[0].parameters.size", StringComparison.Ordinal));
    }

    // S: recipes without a stable key parameter can lose the caller's ownership identity.
    [Fact]
    public void SafetyDefinitionsRemainStable_RecipeKeyRequired()
    {
        var package = CreatePackage();
        Assert.True(Assert.Single(PluginManifest.Read(package).Recipes).Parameters.ContainsKey("key"));
        var file = Path.Combine(package, "resoloop-plugin.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"key\": \"Stable identity\", ", "", StringComparison.Ordinal));
        var error = Assert.Throws<RLoopException>(() => PluginManifest.Read(package));
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<string>>(error.Context["errors"]), value => value.Contains("$.recipes[0].parameters.key", StringComparison.Ordinal));
    }

    // S: comparing bare recipe names incorrectly prevents independent plugin namespaces.
    [Fact]
    public void SafetyDefinitionsRemainStable_RecipeNamespaces()
    {
        var first = PluginManifest.Read(CreatePackage());
        var second = first with { Name = "other-plugin", Commands = [], Skills = [] };
        Assert.Empty(PluginNames.FindConflicts(second, [first], EmptyReservations()));
        Assert.NotEmpty(PluginNames.FindConflicts(second, [first], new([], [], ["make-room"])));
    }

    // S: missing checks could select a colliding command/skill instead of its owner.
    [Fact]
    public void NamesProtectEachNamespace()
    {
        var manifest = PluginManifest.Read(CreatePackage());
        var other = manifest with { Name = "other-plugin", Recipes = [] };
        Assert.NotEmpty(PluginNames.FindConflicts(other with { Skills = [] }, [manifest], EmptyReservations()));
        Assert.NotEmpty(PluginNames.FindConflicts(other with { Commands = [] }, [manifest], EmptyReservations()));
        Assert.NotEmpty(PluginNames.FindConflicts(other with { Commands = [], Skills = [new("resonite-custom", "技能 docs")] }, [], EmptyReservations()));
        Assert.NotEmpty(PluginNames.FindConflicts(manifest, [], new(["sample-plugin"], [], [])));
        Assert.NotEmpty(PluginNames.FindConflicts(manifest, [], new([], ["sample-plugin"], [])));
        Assert.NotEmpty(PluginNames.FindConflicts(manifest, [], new([], ["world-guide"], [])));
    }

    // S: case aliases can overwrite a distinct file when a package moves to Windows.
    [Fact]
    public void PortablePathsRejectCaseCollisions()
    {
        var package = CreatePackage();
        Assert.NotEmpty(PluginPathPolicy.EnumerateFiles(package, "PLUGIN_PATH_UNSAFE"));
        File.WriteAllText(Path.Combine(package, "README"), "one");
        File.WriteAllText(Path.Combine(package, "readme"), "two");
        Assert.Throws<RLoopException>(() => PluginPathPolicy.EnumerateFiles(package, "PLUGIN_PATH_UNSAFE"));
    }

    // S: nested repositories must not leak their ignored files via plain enumeration.
    [Fact]
    public void PortablePathsRejectNestedRepositories()
    {
        var package = CreatePackage();
        Assert.NotEmpty(PluginPathPolicy.EnumerateFiles(package, "PLUGIN_SOURCE_INVALID", rejectNestedGit: true));
        var nested = Path.Combine(package, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, ".git"), "gitdir: somewhere");
        Assert.Throws<RLoopException>(() => PluginPathPolicy.EnumerateFiles(package, "PLUGIN_SOURCE_INVALID", rejectNestedGit: true));
    }

    // S: FIFO/device acceptance can block a copy or read data outside ordinary files.
    [Fact]
    public void PortablePathsRejectSpecialFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var package = CreatePackage();
        Assert.NotEmpty(PluginPathPolicy.EnumerateFiles(package, "PLUGIN_SOURCE_INVALID"));
        using var process = Process.Start(new ProcessStartInfo("mkfifo")
        {
            UseShellExecute = false,
            ArgumentList = { Path.Combine(package, "pipe") }
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Throws<RLoopException>(() => PluginPathPolicy.EnumerateFiles(package, "PLUGIN_SOURCE_INVALID"));
    }

    // S: cleanup must never follow a child link or accept a substituted management root.
    [Fact]
    public void PortablePathsDeleteOnlyTheSelectedTree()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel");
        File.WriteAllText(sentinel, "keep");
        var parentAlias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(parentAlias, outside);
        Assert.Equal(Path.GetFullPath(outside), PluginPathPolicy.ResolveDirectory(parentAlias, "PLUGIN_PATH_UNSAFE"));
        var nested = Path.Combine(outside, "nested");
        Directory.CreateDirectory(nested);
        Assert.Equal(nested, PluginPathPolicy.ResolveDirectory(Path.Combine(parentAlias, "nested"), "PLUGIN_PATH_UNSAFE"));
        Assert.Throws<RLoopException>(() => PluginPathPolicy.DeleteTree(parentAlias, "PLUGIN_REMOVE_REFUSED"));
        Assert.Throws<RLoopException>(() => PluginPathPolicy.DeleteTree(Path.Combine(parentAlias, "nested"), "PLUGIN_REMOVE_REFUSED"));
        var tree = Path.Combine(_root, "selected");
        Directory.CreateDirectory(tree);
        Directory.CreateSymbolicLink(Path.Combine(tree, "child"), outside);
        File.CreateSymbolicLink(Path.Combine(tree, "dangling"), Path.Combine(outside, "missing"));
        PluginPathPolicy.DeleteTree(tree, "PLUGIN_REMOVE_REFUSED");
        Assert.False(Directory.Exists(tree));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(nested));
    }

    private string CreatePackage()
    {
        var package = Path.Combine(_root, "作業 ワールド");
        Directory.CreateDirectory(Path.Combine(package, "技能 docs"));
        Directory.CreateDirectory(Path.Combine(package, "実行 files"));
        Directory.CreateDirectory(Path.Combine(package, "レシピ data"));
        File.WriteAllText(Path.Combine(package, "技能 docs", "SKILL.md"), "# World guide");
        File.WriteAllText(Path.Combine(package, "実行 files", "run.sh"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(package, "レシピ data", "main.json"), "{\"prototypes\":{\"room\":{}}}");
        WriteManifest(package);
        return package;
    }

    private static void WriteManifest(string package, string skillPath = "技能 docs") =>
        File.WriteAllText(Path.Combine(package, "resoloop-plugin.json"), $$"""
        {
          "schemaVersion": 1, "name": "sample-plugin", "version": "1.0.0",
          "description": "日本語 package",
          "resoloop": { "minVersion": "0.2.0", "maxVersionExclusive": "0.3.0" },
          "commands": [{ "name": "render-world", "description": "Render world", "program": "./実行 files/run.sh", "args": ["${pluginRoot}", "${pluginData}"], "timeoutSeconds": 30 }],
          "skills": [{ "name": "world-guide", "path": {{JsonSerializer.Serialize(skillPath)}} }],
          "recipes": [{ "name": "make-room", "path": "レシピ data/main.json", "prototype": "room", "purpose": "Build room", "parameters": { "key": "Stable identity", "size": "number" } }],
          "tools": [{ "name": "shell-tool", "executable": "sh" }]
        }
        """);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
