using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginSkillSyncTests : IDisposable
{
    private const string Version = "0.2.0";
    private static readonly PluginNameReservations Reservations = new([], [], []);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "resoloop-plugin-skill-" + Guid.NewGuid().ToString("N"));
    // Packages must be outside any Git repository so the installer copies them as plain directories.
    private readonly string _sources = Directory.CreateTempSubdirectory("resoloop-plugin-skill-src-").FullName;
    private readonly string _project;

    public PluginSkillSyncTests()
    {
        _project = Path.Combine(_workspace, "作業 ワールド");
        Directory.CreateDirectory(_project);
    }

    // S: an update must not overwrite a user's edit, must not write anything (bundled included) before every
    // conflict is known, and remove must keep the distributed files and their protection record.
    [Fact]
    public void UserEditsSurviveRemoveAndReinstall()
    {
        ProjectInitializer.Initialize(_project);
        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v1 skill", ["references/手順.md"] = "v1 手順" });

        var check = Sync(update: false);
        Assert.False(check.Synchronized);
        Assert.All(check.Plugins!.Files, file => Assert.Equal("missing", file.Status));
        Assert.False(File.Exists(RecordPath));

        Sync(update: true);
        Assert.Equal("v1 skill", ReadSkill("alpha-guide/SKILL.md"));
        Assert.Equal("v1 手順", ReadSkill("alpha-guide/references/手順.md"));
        Assert.True(Sync(update: false).Synchronized);
        var recordV1 = File.ReadAllBytes(RecordPath);

        new PluginInstaller(Registry()).Remove("alpha-plugin", confirmed: true);
        Assert.Equal("v1 skill", ReadSkill("alpha-guide/SKILL.md"));
        Assert.Equal("v1 手順", ReadSkill("alpha-guide/references/手順.md"));
        Assert.Equal(recordV1, File.ReadAllBytes(RecordPath));
        var retained = Sync(update: false);
        Assert.True(retained.Synchronized);
        Assert.Equal(2, retained.Plugins!.Retained.Count);

        File.WriteAllText(Skill("alpha-guide/SKILL.md"), "user edit");
        File.Delete(Skill("resonite-flux/SKILL.md"));
        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v2 skill", ["references/手順.md"] = "v2 手順" }, "2.0.0");

        Assert.Equal("SKILL_SYNC_CONFLICT", Assert.Throws<RLoopException>(() => Sync(update: true)).Code);
        Assert.Equal("user edit", ReadSkill("alpha-guide/SKILL.md"));
        Assert.Equal("v1 手順", ReadSkill("alpha-guide/references/手順.md"));
        Assert.False(File.Exists(Skill("resonite-flux/SKILL.md")));
        Assert.Equal(recordV1, File.ReadAllBytes(RecordPath));

        File.Delete(Skill("alpha-guide/SKILL.md")); // the user resolves the conflict
        Sync(update: true);
        Assert.Equal("v2 skill", ReadSkill("alpha-guide/SKILL.md"));
        Assert.Equal("v2 手順", ReadSkill("alpha-guide/references/手順.md"));
        Assert.True(File.Exists(Skill("resonite-flux/SKILL.md")));
        Assert.True(Sync(update: false).Synchronized);
    }

    // S: a same-named file without an ownership record and a link to an outside directory are never written.
    [Fact]
    public void UnownedFilesAndLinkedDestinationsAreNeverWritten()
    {
        ProjectInitializer.Initialize(_project);
        Directory.CreateDirectory(Skill("beta-guide"));
        File.WriteAllText(Skill("beta-guide/SKILL.md"), "user's own skill");
        var outside = Path.Combine(_sources, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Skill("gamma-guide"), outside);
        Install("beta-plugin", "beta-guide", new() { ["SKILL.md"] = "beta" });
        Install("gamma-plugin", "gamma-guide", new() { ["SKILL.md"] = "gamma" });

        var error = Assert.Throws<RLoopException>(() => Sync(update: true));

        Assert.Equal("SKILL_SYNC_CONFLICT", error.Code);
        var conflicts = Assert.IsAssignableFrom<IEnumerable<PluginSkillFileStatus>>(error.Context["pluginConflicts"]);
        Assert.Equal(new Dictionary<string, string>
        {
            [".agents/skills/beta-guide/SKILL.md"] = "untracked-conflict",
            [".agents/skills/gamma-guide/SKILL.md"] = "unsafe-path-conflict"
        }, conflicts.ToDictionary(file => file.Path, file => file.Status));
        Assert.Equal("user's own skill", ReadSkill("beta-guide/SKILL.md"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        File.Delete(Skill("beta-guide/SKILL.md"));
        Directory.Delete(Skill("gamma-guide")); // removes only the link
        Sync(update: true);
        Assert.Equal("beta", ReadSkill("beta-guide/SKILL.md"));
        Assert.Equal("gamma", ReadSkill("gamma-guide/SKILL.md"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    // S: an update replaces a hardlinked distributed file instead of writing through it, so the content of the
    // other name outside the project (for example a world copied with cp -al) never changes.
    [Fact]
    public void UpdatesNeverWriteThroughHardlinks()
    {
        ProjectInitializer.Initialize(_project);
        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v1 skill" });
        Sync(update: true);
        var outside = Path.Combine(_sources, "copied-world-SKILL.md");
        File.Move(Skill("alpha-guide/SKILL.md"), outside);
        CreateHardLink(Skill("alpha-guide/SKILL.md"), outside);
        new PluginInstaller(Registry()).Remove("alpha-plugin", confirmed: true);
        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v2 skill" }, "2.0.0");

        Sync(update: true);

        Assert.Equal("v2 skill", ReadSkill("alpha-guide/SKILL.md"));
        Assert.Equal("v1 skill", File.ReadAllText(outside));
    }

    // S: a file that could not be written must not be recorded as distributed, and the failure is not a success.
    [Fact]
    public void FailedWritesAreNotRecordedAsDistributed()
    {
        // Linux root ignores the permissions that inject the failure.
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root") return;
        ProjectInitializer.Initialize(_project);
        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v1 skill", ["references/手順.md"] = "v1 手順" });
        Sync(update: true);
        var recordV1 = File.ReadAllBytes(RecordPath);
        new PluginInstaller(Registry()).Remove("alpha-plugin", confirmed: true);
        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v2 skill", ["references/手順.md"] = "v2 手順" }, "2.0.0");
        var blocked = Skill("alpha-guide/references/手順.md");
        var directory = Path.GetDirectoryName(blocked)!;
        // Windows cannot replace a read-only file; Unix cannot create a new name in a read-only directory.
        File.SetAttributes(blocked, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal("PLUGIN_SKILL_WRITE_FAILED", Assert.Throws<RLoopException>(() => Sync(update: true)).Code);
            Assert.Equal(recordV1, File.ReadAllBytes(RecordPath));
            Assert.Equal("v1 手順", ReadSkill("alpha-guide/references/手順.md"));
            Assert.Equal(["手順.md"], Directory.GetFiles(directory).Select(Path.GetFileName));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetAttributes(blocked, FileAttributes.Normal);
        }

        Sync(update: true);
        Assert.Equal("v2 skill", ReadSkill("alpha-guide/SKILL.md"));
        Assert.Equal("v2 手順", ReadSkill("alpha-guide/references/手順.md"));
        Assert.True(Sync(update: false).Synchronized);
    }

    // S: init must not overwrite a user's edit, and a project whose plugins declare no skills gets no record file.
    [Fact]
    public void InitDistributesPluginSkillsButNeverOverwritesEdits()
    {
        ProjectInitializer.Initialize(_project);
        Install("tool-plugin", null, []);
        var synced = Sync(update: true);
        Assert.Null(synced.Plugins);
        Assert.False(File.Exists(RecordPath));

        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v1 skill", ["references/手順.md"] = "v1 手順" });
        var init = ProjectInitializer.Initialize(_project, Version, Reservations);
        Assert.Equal("v1 skill", ReadSkill("alpha-guide/SKILL.md"));
        Assert.Equal("v1 手順", ReadSkill("alpha-guide/references/手順.md"));
        Assert.Contains(".agents/skills/alpha-guide/SKILL.md", init.Plugins!.Updated);
        Assert.Empty(init.Created);

        File.WriteAllText(Skill("alpha-guide/SKILL.md"), "user edit");
        Assert.Equal("INIT_FILE_EXISTS",
            Assert.Throws<RLoopException>(() => ProjectInitializer.Initialize(_project, Version, Reservations)).Code);
        Assert.Equal("user edit", ReadSkill("alpha-guide/SKILL.md"));
    }

    // S: a sync must never report success after saving a record that the next read rejects for its size.
    [Fact]
    public void OversizedRecordsAreRefusedBeforeAnyWrite()
    {
        ProjectInitializer.Initialize(_project);
        Install("alpha-plugin", "alpha-guide", new() { ["SKILL.md"] = "v1 skill" });
        // A valid record of a removed plugin that is within the read limit only while it stays compact.
        var hash = new string('a', 64);
        var compact = new StringBuilder("{\"schemaVersion\":1,\"files\":{");
        for (var i = 1; i <= 62_000; i++)
            compact.Append(i == 1 ? "" : ",").Append($"\"old-guide/f{i:D6}.md\":{{\"plugin\":\"old-plugin\",\"sha256\":\"{hash}\"}}");
        File.WriteAllText(RecordPath, compact.Append("}}").ToString(), new UTF8Encoding(false));
        var record = File.ReadAllBytes(RecordPath);
        Assert.True(record.Length < 8 * 1024 * 1024);
        File.Delete(Skill("resonite-flux/SKILL.md"));

        Assert.Equal("PLUGIN_SKILL_RECORD_TOO_LARGE", Assert.Throws<RLoopException>(() => Sync(update: true)).Code);
        Assert.False(File.Exists(Skill("alpha-guide/SKILL.md")));
        Assert.False(File.Exists(Skill("resonite-flux/SKILL.md")));
        Assert.Equal(record, File.ReadAllBytes(RecordPath));

        new PluginInstaller(Registry()).Remove("alpha-plugin", confirmed: true);
        Sync(update: true);
        Assert.True(File.Exists(Skill("resonite-flux/SKILL.md")));
        Assert.Equal(record, File.ReadAllBytes(RecordPath)); // unchanged entries are not rewritten
    }

    private string RecordPath => Skill(".resoloop-plugins.json");
    private string Skill(string relative) => Path.Combine(_project, ".agents", "skills", relative.Replace('/', Path.DirectorySeparatorChar));
    private string ReadSkill(string relative) => File.ReadAllText(Skill(relative));
    private PluginRegistry Registry() => new(_project, Version, Reservations);
    private SkillSyncResult Sync(bool update) => BundledSkillManager.Sync(_project, update, Version, Reservations);

    private void Install(string plugin, string? skill, Dictionary<string, string> files, string version = "1.0.0")
    {
        var source = Path.Combine(_sources, plugin + "-" + version);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "resoloop-plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, name = plugin, version, resoloop = new { minVersion = Version },
            skills = skill is null ? Array.Empty<object>() : [new { name = skill, path = "skills/" + skill }]
        }));
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(source, "skills", skill!, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        new PluginInstaller(Registry()).Install(source);
    }

    private static void CreateHardLink(string path, string existing)
    {
        var created = OperatingSystem.IsWindows() ? CreateHardLinkW(path, existing, IntPtr.Zero) : Link(existing, path) == 0;
        if (!created) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr securityAttributes);

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) PluginPathPolicy.DeleteTree(_workspace, "PLUGIN_REMOVE_REFUSED");
        if (Directory.Exists(_sources)) PluginPathPolicy.DeleteTree(_sources, "PLUGIN_REMOVE_REFUSED");
    }
}
