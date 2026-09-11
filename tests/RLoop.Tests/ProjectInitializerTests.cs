using RLoop.Core;

namespace RLoop.Tests;

public sealed class ProjectInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-init-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatesPortableStarterProject()
    {
        var result = ProjectInitializer.Initialize(_root);

        Assert.Equal(Path.GetFullPath(_root), result.RootDirectory);
        Assert.Equal(9 + BundledSkillManager.Names.Count, result.Created.Count);
        Assert.True(File.Exists(Path.Combine(_root, ".resoloop.json")));
        var apply = ApplyDocument.Load(Path.Combine(_root, "content", "main.json"));
        Assert.Equal("1", apply.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(apply.Ownership?.Key));
        Assert.Equal("root", apply.Slot!.Key);
        Assert.StartsWith("ResoLoop_Test_", apply.Slot!.Name);
        Assert.Equal("FrooxEngine.Grabbable", Assert.Single(apply.Components!).Type);
        Assert.Contains("module Main", File.ReadAllText(Path.Combine(_root, "flux", "Main.pg")));
        Assert.True(File.Exists(Path.Combine(_root, "flux", "resoloop.flux.json")));
        Assert.Contains("resoloop doctor --json", File.ReadAllText(Path.Combine(_root, "AGENTS.md")));
        Assert.NotNull(apply.Cameras!["main"]);
        foreach (var skillName in BundledSkillManager.Names)
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
        Assert.Equal(8 + BundledSkillManager.Names.Count, result.Unchanged.Count);
    }

    [Fact]
    public void TreatsLineEndingOnlyChangesAsUnchanged()
    {
        ProjectInitializer.Initialize(_root);
        var configPath = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace("\n", "\r\n", StringComparison.Ordinal));

        var result = ProjectInitializer.Initialize(_root);

        Assert.Empty(result.Created);
        Assert.Equal(8 + BundledSkillManager.Names.Count, result.Unchanged.Count);
    }

    [Fact]
    public void PreservesExistingAgentInstructions()
    {
        Directory.CreateDirectory(_root);
        var agentsPath = Path.Combine(_root, "AGENTS.md");
        File.WriteAllText(agentsPath, "user instructions");

        var result = ProjectInitializer.Initialize(_root);

        Assert.Equal("user instructions", File.ReadAllText(agentsPath));
        Assert.DoesNotContain("AGENTS.md", result.Created);
        Assert.DoesNotContain("AGENTS.md", result.Unchanged);
        Assert.True(File.Exists(Path.Combine(_root, ".resoloop.json")));
    }

    [Fact]
    public void RefusesConflictsBeforeWritingAnyOtherFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".resoloop.json"), "user content");

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
        Assert.False(File.Exists(Path.Combine(_root, ".resoloop.json")));
    }

    [Fact]
    public void SkillSyncCheckIsCleanAfterInitialization()
    {
        ProjectInitializer.Initialize(_root);

        var result = BundledSkillManager.Sync(_root, update: false);

        Assert.True(result.Synchronized);
        Assert.All(result.Skills, skill => Assert.Equal("current", skill.Status));
    }

    [Fact]
    public void SkillSyncUpdateIsNoOpWhenAlreadyCurrent()
    {
        ProjectInitializer.Initialize(_root);
        var lockPath = Path.Combine(_root, ".agents", "skills", ".resoloop-bundled.json");
        var before = File.GetLastWriteTimeUtc(lockPath);

        var result = BundledSkillManager.Sync(_root, update: true);

        Assert.True(result.Synchronized);
        Assert.Empty(result.Updated);
        Assert.Equal(before, File.GetLastWriteTimeUtc(lockPath));
    }

    [Fact]
    public void SkillSyncUpdatesOnlyContentMatchingThePreviousLock()
    {
        ProjectInitializer.Initialize(_root);
        var skillPath = Path.Combine(_root, ".agents", "skills", "resonite-build", "SKILL.md");
        const string oldBundled = "---\nname: resonite-build\ndescription: old bundled\n---\n";
        File.WriteAllText(skillPath, oldBundled);
        var oldHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(oldBundled)));
        var lockPath = Path.Combine(_root, ".agents", "skills", ".resoloop-bundled.json");
        File.WriteAllText(lockPath, $$"""
            { "schemaVersion": 1, "skills": { "resonite-build": "{{oldHash}}" } }
            """);

        var check = BundledSkillManager.Sync(_root, update: false);
        var updated = BundledSkillManager.Sync(_root, update: true);

        Assert.False(check.Synchronized);
        Assert.Equal("update-available", check.Skills.Single(skill => skill.Name == "resonite-build").Status);
        Assert.True(updated.Synchronized);
        Assert.Contains(".agents/skills/resonite-build/SKILL.md", updated.Updated);
        Assert.Contains("Build or modify Resonite", File.ReadAllText(skillPath));
        Assert.True(BundledSkillManager.Sync(_root, update: false).Synchronized);
    }

    [Fact]
    public void SkillSyncRefusesUserModifiedContentBeforeUpdatingAnything()
    {
        ProjectInitializer.Initialize(_root);
        var modifiedPath = Path.Combine(_root, ".agents", "skills", "resonite-build", "SKILL.md");
        var untouchedPath = Path.Combine(_root, ".agents", "skills", "resonite-debug", "SKILL.md");
        File.WriteAllText(modifiedPath, "user content");
        var untouched = File.ReadAllText(untouchedPath);

        var error = Assert.Throws<RLoopException>(() => BundledSkillManager.Sync(_root, update: true));

        Assert.Equal("SKILL_SYNC_CONFLICT", error.Code);
        Assert.Equal("user content", File.ReadAllText(modifiedPath));
        Assert.Equal(untouched, File.ReadAllText(untouchedPath));
    }

    [Fact]
    public void SkillSyncUpdateRestoresMissingLockedSkill()
    {
        ProjectInitializer.Initialize(_root);
        var skillPath = Path.Combine(_root, ".agents", "skills", "resonite-flux", "SKILL.md");
        File.Delete(skillPath);

        var check = BundledSkillManager.Sync(_root, update: false);
        var updated = BundledSkillManager.Sync(_root, update: true);

        Assert.False(check.Synchronized);
        Assert.Equal("missing", check.Skills.Single(skill => skill.Name == "resonite-flux").Status);
        Assert.True(updated.Synchronized);
        Assert.True(File.Exists(skillPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
