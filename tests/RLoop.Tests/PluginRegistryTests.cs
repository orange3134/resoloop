using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("RESOLOOP_PLUGIN_TEST_ROOT")
        ?? Path.GetTempPath(), "registry-" + Guid.NewGuid().ToString("N"));
    private static PluginNameReservations EmptyReservations() => new([], [], []);

    // S: trusting record keys can read outside the package or alias two files on Windows.
    [Fact]
    public void UntrustedRecordNeverReadsOutside()
    {
        var project = CreateProject("作業 ワールド");
        var install = AddPlugin(project);
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        Assert.Equal("hello 日本語", File.ReadAllText(Path.Combine(Assert.Single(registry.LoadAll()).PackageDirectory, "内容.txt")));
        var recordPath = Path.Combine(install, "install.json");
        var original = File.ReadAllText(recordPath);
        var outside = Path.Combine(install, "outside");
        File.WriteAllText(outside, "outside sentinel");
        foreach (var keys in new[] { new[] { "../outside" }, new[] { "README", "readme" },
            new[] { "parent", "parent/child" }, new[] { "Assets/a", "assets/b" } })
        {
            var json = JsonNode.Parse(original)!;
            foreach (var key in keys) json["files"]![key] = Hash("outside sentinel");
            File.WriteAllText(recordPath, json.ToJsonString());
            Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Throws<RLoopException>(() => PluginInstallRecord.Read(recordPath)).Code);
            Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Single(registry.List()).Problem?.Code);
            Assert.Throws<RLoopException>(() => registry.LoadAll());
        }
        File.WriteAllText(recordPath, original);
        Assert.Single(registry.LoadAll());
        Assert.Equal("outside sentinel", File.ReadAllText(outside));
    }

    // S: a partial verifier misses added/missing files, a changed root, or tampered executable data.
    [Fact]
    public void VerifyEntirePackageBeforeUse()
    {
        var project = CreateProject("verification");
        var install = AddPlugin(project);
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        var package = Path.Combine(install, "package");
        var content = Path.Combine(package, "内容.txt");
        Assert.Single(registry.LoadAll());
        File.WriteAllText(content, "changed");
        Modified();
        File.WriteAllText(content, "hello 日本語");
        Assert.Single(registry.LoadAll());
        File.WriteAllText(Path.Combine(package, "extra"), "extra");
        Modified();
        File.Delete(Path.Combine(package, "extra"));
        File.Delete(content);
        Modified();
        File.WriteAllText(content, "hello 日本語");
        Assert.Single(registry.LoadAll());
        var saved = Path.Combine(_root, "saved-package");
        Directory.Move(package, saved);
        Directory.CreateSymbolicLink(package, saved); // #112: same hashes behind a substituted root are not verified.
        Modified();
        Directory.Delete(package);
        Directory.Move(saved, package);
        File.WriteAllText(Path.Combine(install, "data", "state"), "user update");
        var outsideSkill = Path.Combine(project, "distributed-skill.md");
        File.WriteAllText(outsideSkill, "user skill");
        Assert.Single(registry.LoadAll());
        Assert.Equal("user update", File.ReadAllText(Path.Combine(install, "data", "state")));
        Assert.Equal("user skill", File.ReadAllText(outsideSkill));

        void Modified()
        {
            Assert.Equal("PLUGIN_MODIFIED", Assert.Single(registry.List()).Problem?.Code);
            Assert.Equal("PLUGIN_MODIFIED", Assert.Throws<RLoopException>(() => registry.Load("sample-plugin")).Code);
            Assert.Throws<RLoopException>(() => registry.OpenVerified());
        }
    }

    // S: valid hashes must not authenticate a record for a different name/version; last-wins loses evidence.
    [Fact]
    public void RecordIdentityAndDuplicatePropertiesFailClosed()
    {
        var project = CreateProject("identity");
        var install = AddPlugin(project);
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        Assert.Single(registry.LoadAll());
        var path = Path.Combine(install, "install.json");
        var original = File.ReadAllText(path);
        foreach (var (key, value) in new[] { ("name", "other-plugin"), ("version", "2.0.0") })
        {
            var json = JsonNode.Parse(original)!;
            json[key] = value;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Single(registry.List()).Problem?.Code);
            Assert.Throws<RLoopException>(() => registry.LoadAll());
        }
        var duplicate = original.Replace("\"schemaVersion\":1", "\"schemaVersion\":2,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.NotEqual(original, duplicate);
        File.WriteAllText(path, duplicate);
        Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Single(registry.List()).Problem?.Code);
        File.WriteAllText(path, original);
        Assert.Single(registry.LoadAll());
    }

    // S: writing an unsafe record must not truncate existing state, and unchecked sizes can escape bounds.
    [Fact]
    public void RecordValidationPrecedesOverwriteAndEnforcesBounds()
    {
        var project = CreateProject("record-limits");
        var install = AddPlugin(project);
        var path = Path.Combine(install, "install.json");
        var record = PluginInstallRecord.Read(path);
        Assert.Equal(Hash("hello 日本語"), record.Files.GetValueOrDefault("内容.txt"));
        var output = Path.Combine(install, "copy.json");
        record.Write(output);
        using (var document = JsonDocument.Parse(File.ReadAllText(output)))
        {
            Assert.Equal("sample-plugin", document.RootElement.GetProperty("name").GetString());
            Assert.Equal(Hash("hello 日本語"), document.RootElement.GetProperty("files").GetProperty("内容.txt").GetString());
        }
        var safeOutput = File.ReadAllBytes(output);
        var invalid = record with { Files = new Dictionary<string, string> { ["../outside"] = Hash("outside") } };
        Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Throws<RLoopException>(() => invalid.Write(output)).Code);
        Assert.Equal(safeOutput, File.ReadAllBytes(output));
        // Valid oversized JSON: without the byte limit, parsing and record validation would succeed.
        File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(safeOutput) + new string(' ', 8 * 1024 * 1024));
        Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Throws<RLoopException>(() => PluginInstallRecord.Read(path)).Code);
        var json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(safeOutput))!;
        var files = (JsonObject)json["files"]!;
        for (var i = 0; i < 10000; i++) files["item-" + i] = Hash("x");
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Throws<RLoopException>(() => PluginInstallRecord.Read(path)).Code);
        File.WriteAllBytes(path, safeOutput);
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        Assert.Single(registry.LoadAll());
        var oversizedFile = Path.Combine(install, "package", "内容.txt");
        using (var stream = new FileStream(oversizedFile, FileMode.Open, FileAccess.Write))
            stream.SetLength(200L * 1024 * 1024 + 1);
        // Match the actual bytes so only the size guard, not hash mismatch, rejects the package.
        var oversizedRecord = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(safeOutput))!;
        using (var stream = File.OpenRead(oversizedFile))
            oversizedRecord["files"]!["内容.txt"] = Convert.ToHexStringLower(SHA256.HashData(stream));
        File.WriteAllText(path, oversizedRecord.ToJsonString());
        Assert.Equal("PLUGIN_MODIFIED", Assert.Single(registry.List()).Problem?.Code);
    }

    // S: Load(name) must not bypass a problem in another plugin or silently select a colliding command.
    [Fact]
    public void LoadingOnePluginStillValidatesTheWholeRegistry()
    {
        var project = CreateProject("conflicts");
        AddPlugin(project, "first-plugin", "first-command");
        var second = AddPlugin(project, "second-plugin", "second-command");
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        Assert.Equal(2, registry.LoadAll().Count);
        Assert.Equal("first-plugin", registry.Load("first-plugin").Name);
        AddPlugin(project, "second-plugin", "first-command");
        Assert.All(registry.List(), status => Assert.Equal("PLUGIN_NAME_CONFLICT", status.Problem?.Code));
        Assert.Equal("PLUGIN_NAME_CONFLICT", Assert.Throws<RLoopException>(() => registry.Load("first-plugin")).Code);
        AddPlugin(project, "second-plugin", "second-command");
        File.WriteAllText(Path.Combine(second, "install.json"), "{}");
        Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Throws<RLoopException>(() => registry.Load("first-plugin")).Code);
        AddPlugin(project, "second-plugin", "second-command");
        var reserved = new PluginRegistry(project, "0.2.0", new(["first-command"], [], []));
        Assert.Equal("PLUGIN_NAME_CONFLICT", Assert.Throws<RLoopException>(() => reserved.LoadAll()).Code);
        var incompatible = new PluginRegistry(project, "0.3.0", EmptyReservations());
        Assert.Equal("PLUGIN_INCOMPATIBLE_VERSION", Assert.Throws<RLoopException>(() => incompatible.LoadAll()).Code);
        Assert.Equal(2, registry.LoadAll().Count);
    }

    // S: an explicit invalid project must not fall back to a nearby user's project.
    [Fact]
    public void ProjectSelectionNeverFallsBackFromAnExplicitTarget()
    {
        var outer = CreateProject("outer");
        var nested = CreateProject(Path.Combine("outer", "nested"));
        var child = Path.Combine(nested, "child");
        Directory.CreateDirectory(child);
        Assert.Equal(nested, PluginRegistry.FindProjectRoot(null, child));
        Assert.Equal(outer, PluginRegistry.FindProjectRoot(outer, child));
        Assert.Equal(outer, PluginRegistry.FindProjectRoot("../..", child));
        var unmarked = Path.Combine(outer, "unmarked");
        Directory.CreateDirectory(unmarked);
        Assert.Equal("PLUGIN_PROJECT_NOT_FOUND", Assert.Throws<RLoopException>(() => PluginRegistry.FindProjectRoot(unmarked, child)).Code);
        Assert.Equal("PLUGIN_PROJECT_NOT_FOUND", Assert.Throws<RLoopException>(() => new PluginRegistry(unmarked, "0.2.0", EmptyReservations())).Code);
        var registry = new PluginRegistry(outer, "0.2.0", EmptyReservations());
        using (var lease = registry.OpenVerified()) Assert.Empty(lease.Plugins);
        Assert.False(Directory.Exists(Path.Combine(outer, ".resoloop")));
    }

    // S: following a substituted management parent exposes another project's installation and lock.
    [Fact]
    public void UnsafeManagementRootsAreGlobalFailures()
    {
        var project = CreateProject("linked-root");
        var other = CreateProject("other-root");
        AddPlugin(project);
        AddPlugin(other);
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        Assert.Single(registry.LoadAll());
        var management = Path.Combine(project, ".resoloop");
        var saved = Path.Combine(project, "saved-management");
        Directory.Move(management, saved);
        Directory.CreateSymbolicLink(management, Path.Combine(other, ".resoloop"));
        Assert.Equal("PLUGIN_PATH_UNSAFE", Assert.Throws<RLoopException>(() => registry.List()).Code);
        Assert.Equal("PLUGIN_PATH_UNSAFE", Assert.Throws<RLoopException>(() => registry.OpenVerified()).Code);
        Directory.Delete(management);
        Directory.Move(saved, management);
        Assert.Single(registry.LoadAll());
        File.Delete(Path.Combine(registry.PluginsDirectory, ".lock"));
        File.CreateSymbolicLink(Path.Combine(registry.PluginsDirectory, ".lock"), Path.Combine(_root, "absent-lock-target"));
        Assert.Equal("PLUGIN_PATH_UNSAFE", Assert.Throws<RLoopException>(() => registry.List()).Code);
        Assert.False(File.Exists(Path.Combine(_root, "absent-lock-target")));
    }

    // S: treating a malformed/case-alias directory as invisible silently hides an unverified installation.
    [Fact]
    public void PublishedDirectoryNamesCannotBeHiddenOrAliased()
    {
        var project = CreateProject("published-names");
        var install = AddPlugin(project);
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        Assert.Single(registry.LoadAll());
        var alias = Path.Combine(registry.PluginsDirectory, "SAMPLE-PLUGIN");
        // A case-insensitive filesystem cannot represent two distinct case aliases.
        if (!Directory.Exists(alias))
        {
            Directory.CreateDirectory(alias);
            Assert.Equal(2, registry.List().Count);
            Assert.Throws<RLoopException>(() => registry.LoadAll());
            Directory.Delete(alias);
        }
        Directory.Move(install, Path.Combine(registry.PluginsDirectory, "invalid_name"));
        Assert.False(Assert.Single(registry.List()).Ok);
        Assert.Throws<RLoopException>(() => registry.LoadAll());
    }

    // S: releasing verification's lock before use allows a cooperating installer/remover to invalidate the paths.
    [Fact]
    public void UsageLeaseOwnsProjectLock()
    {
        var project = CreateProject("lease");
        var other = CreateProject("lease-other");
        AddPlugin(project);
        AddPlugin(other);
        var probe = BuildProbe();
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        var lease = registry.OpenVerified();
        try
        {
            Assert.Equal(7, RunProbe(probe, project));
            Assert.Equal(0, RunProbe(probe, other));
            Assert.Equal("PLUGIN_PROJECT_BUSY", Assert.Throws<RLoopException>(() => registry.List()).Code);
            Assert.Equal("hello 日本語", File.ReadAllText(Path.Combine(Assert.Single(lease.Plugins).PackageDirectory, "内容.txt")));
        }
        finally { lease.Dispose(); lease.Dispose(); }
        Assert.Equal(0, RunProbe(probe, project));
        using (var child = StartProbe(probe, project, "hold"))
        {
            try
            {
                Assert.Equal("READY", child.StandardOutput.ReadLine());
                Assert.Equal("PLUGIN_PROJECT_BUSY", Assert.Throws<RLoopException>(() => registry.OpenVerified()).Code);
                child.StandardInput.WriteLine("exit");
                child.WaitForExit();
                Assert.Equal(0, child.ExitCode);
            }
            finally { if (!child.HasExited) { child.StandardInput.Close(); child.WaitForExit(); } }
        }
        using var recovered = registry.OpenVerified();
        Assert.Single(recovered.Plugins);
    }

    // S: the installer's lock-held verification must not switch to a different project's staging area.
    [Fact]
    public void LockHeldStagingVerificationStaysInItsProject()
    {
        var project = CreateProject("staging-project");
        var install = AddPlugin(project);
        var outside = AddPlugin(CreateProject("outside-project"));
        var registry = new PluginRegistry(project, "0.2.0", EmptyReservations());
        using var projectLock = PluginProjectLock.Acquire(registry.PluginsDirectory);
        Assert.Single(registry.LoadAllUnderLock());
        var staging = Path.Combine(registry.PluginsDirectory, ".staging-owned");
        Directory.Move(install, staging);
        Assert.Empty(registry.ListUnderLock());
        InstalledPlugin? verified = null;
        Assert.Null(Record.Exception(() => verified = registry.VerifyInstallationUnderLock("sample-plugin", staging)));
        Assert.Equal(Path.Combine(staging, "package"), verified!.PackageDirectory);
        Assert.Equal("hello 日本語", File.ReadAllText(Path.Combine(verified.PackageDirectory, "内容.txt")));
        Assert.Equal("PLUGIN_PATH_UNSAFE", Assert.Throws<RLoopException>(() => registry.VerifyInstallationUnderLock("sample-plugin", outside)).Code);
    }

    private string CreateProject(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".resoloop.json"), "{}");
        return path;
    }

    // Independent fixture: no PluginInstallRecord.Write or production hashing/path helpers.
    private static string AddPlugin(string project, string name = "sample-plugin", string? command = null)
    {
        var install = Path.Combine(project, ".resoloop", "plugins", name);
        var package = Path.Combine(install, "package");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(Path.Combine(install, "data"));
        File.WriteAllText(Path.Combine(install, "data", "state"), "original data");
        var commands = command is null ? "" : $",\"commands\":[{{\"name\":\"{command}\",\"description\":\"Run\",\"program\":\"echo\"}}]";
        var manifest = $$"""{"schemaVersion":1,"name":"{{name}}","version":"1.0.0","resoloop":{"minVersion":"0.2.0","maxVersionExclusive":"0.3.0"}{{commands}}} """;
        File.WriteAllText(Path.Combine(package, "resoloop-plugin.json"), manifest);
        File.WriteAllText(Path.Combine(package, "内容.txt"), "hello 日本語");
        File.WriteAllText(Path.Combine(install, "install.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, name, version = "1.0.0",
            source = new { kind = "directory", location = Path.GetFullPath(package), @ref = (string?)null },
            commit = (string?)null, installedAt = "2026-10-29T00:00:00Z", resoloopVersion = "0.2.0",
            files = new Dictionary<string, string> { ["resoloop-plugin.json"] = Hash(manifest), ["内容.txt"] = Hash("hello 日本語") }
        }));
        return install;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private string BuildProbe()
    {
        var artifacts = Path.Combine(_root, "probe-artifacts");
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "core", "lock-probe", "LockProbe.csproj");
        using var build = new Process { StartInfo = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "build", fixture, "--nologo", "--artifacts-path", artifacts,
            "-p:ResoloopCoreAssembly=" + typeof(PluginRegistry).Assembly.Location }) build.StartInfo.ArgumentList.Add(argument);
        build.Start();
        var stdout = build.StandardOutput.ReadToEndAsync();
        var stderr = build.StandardError.ReadToEndAsync();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        return Path.Combine(artifacts, "bin", "LockProbe", "debug", "LockProbe.dll");
    }

    private static Process StartProbe(string probe, string project, string mode = "try") => Process.Start(new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        ArgumentList = { probe, project, mode }
    })!;

    private static int RunProbe(string probe, string project)
    {
        using var child = StartProbe(probe, project);
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        child.WaitForExit();
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Assert.True(child.ExitCode is 0 or 7, output);
        return child.ExitCode;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
