using RLoop.Core;

namespace RLoop.Tests;

public sealed class ProjectInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rloop-init-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatesPortableStarterProject()
    {
        var result = ProjectInitializer.Initialize(_root);

        Assert.Equal(Path.GetFullPath(_root), result.RootDirectory);
        Assert.Equal(11, result.Created.Count);
        Assert.True(File.Exists(Path.Combine(_root, ".rloop.json")));
        var apply = ApplyDocument.Load(Path.Combine(_root, "content", "main.json"));
        Assert.Equal("1", apply.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(apply.Ownership?.Key));
        Assert.Equal("root", apply.Slot!.Key);
        Assert.StartsWith("RLoop_Test_", apply.Slot!.Name);
        Assert.Equal("FrooxEngine.Grabbable", Assert.Single(apply.Components!).Type);
        Assert.Contains("module Main", File.ReadAllText(Path.Combine(_root, "flux", "Main.pg")));
        Assert.True(File.Exists(Path.Combine(_root, "flux", "rloop.flux.json")));
        Assert.NotNull(apply.Cameras!["main"]);
        foreach (var skillName in new[] { "resonite-build", "resonite-debug", "resonite-flux", "resonite-inspect" })
        {
            var skillPath = Path.Combine(_root, ".agents", "skills", skillName, "SKILL.md");
            Assert.True(File.Exists(skillPath), $"Expected bundled skill at {skillPath}");
            Assert.StartsWith("---", File.ReadAllText(skillPath));
        }
    }

    [Fact]
    public void IsIdempotentWhenGeneratedFilesAreUnchanged()
    {
        ProjectInitializer.Initialize(_root);

        var result = ProjectInitializer.Initialize(_root);

        Assert.Empty(result.Created);
        Assert.Equal(11, result.Unchanged.Count);
    }

    [Fact]
    public void TreatsLineEndingOnlyChangesAsUnchanged()
    {
        ProjectInitializer.Initialize(_root);
        var configPath = Path.Combine(_root, ".rloop.json");
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace("\n", "\r\n", StringComparison.Ordinal));

        var result = ProjectInitializer.Initialize(_root);

        Assert.Empty(result.Created);
        Assert.Equal(11, result.Unchanged.Count);
    }

    [Fact]
    public void RefusesConflictsBeforeWritingAnyOtherFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".rloop.json"), "user content");

        var ex = Assert.Throws<RLoopException>(() => ProjectInitializer.Initialize(_root));

        Assert.Equal("INIT_FILE_EXISTS", ex.Code);
        Assert.False(File.Exists(Path.Combine(_root, "content", "main.json")));
    }

    [Fact]
    public void RefusesModifiedProjectSkillBeforeWritingAnyOtherFile()
    {
        var skillDirectory = Path.Combine(_root, ".agents", "skills", "resonite-build");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "user content");

        var ex = Assert.Throws<RLoopException>(() => ProjectInitializer.Initialize(_root));

        Assert.Equal("INIT_FILE_EXISTS", ex.Code);
        Assert.Contains(".agents/skills/resonite-build/SKILL.md", Assert.IsType<List<string>>(ex.Context!["conflicts"]));
        Assert.False(File.Exists(Path.Combine(_root, ".rloop.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
