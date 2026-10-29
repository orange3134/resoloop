using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginInstallerTests : IDisposable
{
    // A plain source must actually be outside any ancestor Git repository (the session directory is not).
    private readonly string _external = Directory.CreateTempSubdirectory("resoloop-git-external-").FullName;
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("RESOLOOP_PLUGIN_TEST_ROOT")
        ?? Path.GetTempPath(), "git-" + Guid.NewGuid().ToString("N"));

    // S: fallback copying, non-root discovery, or alias mismatches can silently include ignored credentials (#110).
    [Fact]
    public void GitSourceClassificationFailsClosed()
    {
        var plain = Path.Combine(_external, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "内容.txt"), "plain content");
        Assert.Equal(["内容.txt"], PluginGit.GetLocalFiles(plain));
        var repo = Repository("作業 ワールド");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "private\n");
        File.WriteAllText(Path.Combine(repo, "tracked.txt"), "tracked");
        Git(repo, "add", ".gitignore", "tracked.txt");
        File.WriteAllText(Path.Combine(repo, "allowed.txt"), "untracked");
        File.WriteAllText(Path.Combine(repo, "private"), FakeSecret());
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, repo);
        Assert.Equal([".gitignore", "allowed.txt", "tracked.txt"], PluginGit.GetLocalFiles(alias));
        var subdir = Path.Combine(repo, "subdir");
        Directory.CreateDirectory(subdir);
        Assert.Equal("PLUGIN_SOURCE_INVALID", Assert.Throws<RLoopException>(() => PluginGit.GetLocalFiles(subdir)).Code);
        var broken = Path.Combine(_external, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, ".git"), "gitdir: absent\n");
        Assert.Throws<RLoopException>(() => PluginGit.GetLocalFiles(broken));
        Git(plain, "init", "nested");
        Assert.Throws<RLoopException>(() => PluginGit.GetLocalFiles(plain));
        Repository(Path.Combine("作業 ワールド", "nested"));
        Assert.Throws<RLoopException>(() => PluginGit.GetLocalFiles(repo));
        var bare = Path.Combine(_external, "bare");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "--bare");
        Assert.Throws<RLoopException>(() => PluginGit.GetLocalFiles(bare));
    }

    // S: inherited GIT_* state must never select another repository or make ignored data look copyable.
    [Fact]
    public void GitIgnoresInjectedRepositoryState()
    {
        var repo = Repository("correct");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "private\n");
        File.WriteAllText(Path.Combine(repo, "correct.txt"), "correct");
        File.WriteAllText(Path.Combine(repo, "private"), FakeSecret());
        Git(repo, "add", ".gitignore", "correct.txt");
        var wrong = Repository("wrong");
        File.WriteAllText(Path.Combine(wrong, "wrong.txt"), "wrong");
        Git(wrong, "add", "wrong.txt");
        var config = Path.Combine(_root, "injected-config");
        File.WriteAllText(config, "[core]\n worktree = " + wrong + "\n");
        var probe = BuildProbe();
        var normal = Probe(probe, ["local", repo]);
        Assert.Equal(0, normal.Exit);
        Assert.Equal([".gitignore", "correct.txt"], JsonSerializer.Deserialize<string[]>(normal.Output)!);
        var injected = Probe(probe, ["local", repo], new()
        {
            ["GIT_DIR"] = Path.Combine(wrong, ".git"), ["GIT_WORK_TREE"] = wrong,
            ["GIT_INDEX_FILE"] = Path.Combine(wrong, ".git", "index"),
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(wrong, ".git", "objects"),
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(wrong, ".git", "objects"),
            ["GIT_CONFIG_GLOBAL"] = config, ["GIT_CONFIG_SYSTEM"] = config,
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "core.worktree", ["GIT_CONFIG_VALUE_0"] = wrong,
            ["GIT_CONFIG_PARAMETERS"] = "'core.worktree='" + wrong,
            ["GIT_CEILING_DIRECTORIES"] = repo, ["GIT_COMMON_DIR"] = Path.Combine(wrong, ".git")
        });
        Assert.Equal(0, injected.Exit);
        Assert.Equal(normal.Output, injected.Output);
    }

    // S: checkout/filter/hooks or resolving HEAD instead of the requested ref changes authenticated package bytes.
    [Fact]
    public void ExportPinsBlobsWithoutExecution()
    {
        if (OperatingSystem.IsWindows()) return;
        var repo = Repository("remote 作業");
        var content = Encoding.UTF8.GetBytes("original 日本語\n");
        File.WriteAllBytes(Path.Combine(repo, "run.sh"), content);
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "run.sh filter=probe\n");
        File.SetUnixFileMode(Path.Combine(repo, "run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "original");
        Git(repo, "tag", "release");
        var commit = Git(repo, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(repo, "run.sh"), "different HEAD");
        Git(repo, "commit", "-am", "newer");
        var sentinel = Path.Combine(_root, "EXECUTED");
        var hooks = DirectoryAt("hooks");
        var hook = Path.Combine(hooks, "post-checkout");
        File.WriteAllText(hook, "#!/bin/sh\nprintf hook > '" + sentinel + "'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var home = DirectoryAt("isolated-home");
        File.WriteAllText(Path.Combine(home, ".gitconfig"), "[core]\n hooksPath = " + hooks + "\n fsmonitor = " + hook
            + "\n[filter \"probe\"]\n smudge = touch '" + sentinel + "'\n required = true\n");
        var probe = BuildProbe();
        var checkout = DirectoryAt("clone");
        var package = DirectoryAt("package");
        var result = Probe(probe, ["export", new Uri(repo + "/").AbsoluteUri, "release", checkout, package], new() { ["HOME"] = home });
        Assert.Equal(0, result.Exit);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(commit, json.RootElement.GetProperty("Commit").GetString());
        Assert.Equal(new Uri(repo + "/").AbsoluteUri, json.RootElement.GetProperty("Source").GetProperty("Location").GetString());
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(File.ReadAllBytes(Path.Combine(package, "run.sh"))));
        Assert.True(File.GetUnixFileMode(Path.Combine(package, "run.sh")).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.Exists(sentinel));
        var local = Probe(probe, ["local", repo], new() { ["HOME"] = home });
        Assert.Equal(0, local.Exit);
        Assert.False(File.Exists(sentinel));
        Assert.Equal("PLUGIN_GIT_REF_NOT_FOUND", Assert.Throws<RLoopException>(() => PluginGit.Export(
            new Uri(repo + "/").AbsoluteUri, "missing", DirectoryAt("missing-clone"), DirectoryAt("missing-package"))).Code);
    }

    // S: returned failures/metadata are persisted or displayed; raw URLs and child diagnostics can leak credentials.
    [Fact]
    public void GitSecretsNeverReachErrorsOrMetadata()
    {
        var secret = FakeSecret();
        foreach (var url in new[] { "https://user:" + secret + "@invalid.test/repo", "https://" + secret + "@invalid.test/repo",
            "file:///absent?" + secret, "ssh://user:" + secret + "@invalid.test/repo", "file:///absent#" + secret })
        {
            var error = Assert.Throws<RLoopException>(() => PluginGit.Export(url, "main", DirectoryAt(Guid.NewGuid().ToString("N")), DirectoryAt(Guid.NewGuid().ToString("N"))));
            Assert.DoesNotContain(secret, error.ToString());
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(error.Context));
        }
        var parsed = PluginGit.ParseSource("ssh://public-user@invalid.test/repo", "main");
        Assert.Equal("ssh://invalid.test/repo", parsed.Location);
        Assert.Equal("invalid.test:repo", PluginGit.ParseSource("public-user@invalid.test:repo", "main").Location);
        var failed = Assert.Throws<RLoopException>(() => PluginGit.Export(new Uri(Path.Combine(_root, secret)).AbsoluteUri,
            "main", DirectoryAt("absent-clone"), DirectoryAt("absent-package")));
        Assert.Equal("PLUGIN_GIT_FAILED", failed.Code);
        Assert.DoesNotContain(secret, failed.ToString());
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(failed.Context));
    }

    // S: an export must not follow tree links, alias files across platforms, or overwrite a caller's existing package.
    [Fact]
    public void ExportRejectsUnsafeTreesAndOccupiedDestinations()
    {
        var repo = Repository("unsafe-tree");
        File.WriteAllText(Path.Combine(repo, "normal"), "good");
        Git(repo, "add", "."); Git(repo, "commit", "-m", "good");
        var url = new Uri(repo + "/").AbsoluteUri;
        var accepted = DirectoryAt("accepted-package");
        PluginGit.Export(url, "HEAD", DirectoryAt("accepted-clone"), accepted);
        Assert.Equal("good", File.ReadAllText(Path.Combine(accepted, "normal")));
        var package = DirectoryAt("occupied-package");
        File.WriteAllText(Path.Combine(package, "normal"), "preserve");
        Assert.Throws<RLoopException>(() => PluginGit.Export(url, "HEAD", DirectoryAt("occupied-clone"), package));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(package, "normal")));
        File.CreateSymbolicLink(Path.Combine(repo, "link"), "normal");
        Git(repo, "add", "."); Git(repo, "commit", "-m", "link");
        Assert.Throws<RLoopException>(() => PluginGit.Export(url, "HEAD", DirectoryAt("link-clone"), DirectoryAt("link-package")));
        File.Delete(Path.Combine(repo, "link"));
        // Only create the case-collision tree when this filesystem can keep both entries.
        if (!File.Exists(Path.Combine(repo, "NORMAL")))
        {
            File.WriteAllText(Path.Combine(repo, "NORMAL"), "collision");
            Git(repo, "add", "-A"); Git(repo, "commit", "-m", "collision");
            Assert.Throws<RLoopException>(() => PluginGit.Export(url, "HEAD", DirectoryAt("case-clone"), DirectoryAt("case-package")));
            File.Delete(Path.Combine(repo, "NORMAL"));
        }
        using (var oversized = File.Create(Path.Combine(repo, "large"))) oversized.SetLength(200L * 1024 * 1024 + 1);
        Git(repo, "add", "-A"); Git(repo, "commit", "-m", "oversized");
        var boundedPackage = DirectoryAt("bounded-package");
        Assert.Throws<RLoopException>(() => PluginGit.Export(url, "HEAD", DirectoryAt("bounded-clone"), boundedPackage));
        Assert.Empty(Directory.GetFiles(boundedPackage, "*", SearchOption.AllDirectories));
    }

    // S: command failure is not evidence of a Git-external directory; timeout must stop children even after their parent exits.
    [Fact]
    public void GitFailuresAndTimeoutNeverBecomeSuccessfulCopies()
    {
        if (OperatingSystem.IsWindows()) return;
        var probe = BuildProbe();
        var plain = Path.Combine(_external, "error-plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "private"), FakeSecret());
        var normal = Probe(probe, ["local", plain]);
        Assert.Equal(0, normal.Exit);
        Assert.Equal(["private"], JsonSerializer.Deserialize<string[]>(normal.Output)!);
        var home = DirectoryAt("broken-config-home");
        File.WriteAllText(Path.Combine(home, ".gitconfig"), "[broken\n");
        var brokenConfig = Probe(probe, ["local", plain], new() { ["HOME"] = home });
        Assert.Equal(7, brokenConfig.Exit);
        var tools = DirectoryAt("fake-tools");
        var executable = Path.Combine(tools, "git");
        File.WriteAllText(executable, "#!/bin/sh\nexit 42\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var failed = Probe(probe, ["local", plain], new() { ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") });
        Assert.Equal(7, failed.Exit);
        Assert.Contains("PLUGIN_GIT_FAILED", failed.Output);
        var secret = FakeSecret();
        var pid = Path.Combine(_root, "child-pid");
        // The fake Git exits immediately, leaving a child holding both redirected pipes.
        // It exercises the real deadline/cleanup path without a network connection.
        File.WriteAllText(executable, "#!/bin/sh\nprintf '%s' '" + secret + "' >&2\nsleep 240 &\necho $! > '" + pid + "'\nexit 0\n");
        var package = DirectoryAt("timeout-package");
        var watch = Stopwatch.StartNew();
        try
        {
            var result = Probe(probe, ["export", "file:///unused", "main", DirectoryAt("timeout-clone"), package],
                new() { ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") });
            Assert.Equal(7, result.Exit);
            Assert.Contains("PLUGIN_GIT_FAILED", result.Output);
            Assert.DoesNotContain(secret, result.Output);
            Assert.InRange(watch.Elapsed.TotalSeconds, 110, 150);
            Assert.False(IsExecuting(int.Parse(File.ReadAllText(pid))), "Timed-out Git left an orphan child executing.");
            Assert.Empty(Directory.GetFiles(package));
        }
        finally
        {
            // Also clean up the RED case and assertion failures. Never signal an unverified PID:
            // its executable arguments AND unique owned cwd must still identify this fixture.
            if (File.Exists(pid))
            {
                var childId = int.Parse(File.ReadAllText(pid));
                if (IsExecuting(childId))
                {
                    Assert.Equal("sleep\0240\0", File.ReadAllText("/proc/" + childId + "/cmdline"));
                    Assert.Equal(package, new DirectoryInfo("/proc/" + childId + "/cwd").ResolveLinkTarget(true)?.FullName);
                    using var child = Process.GetProcessById(childId);
                    child.Kill();
                    Assert.True(SpinWait.SpinUntil(() => !IsExecuting(childId), TimeSpan.FromSeconds(10)), "Owned test child termination was not confirmed.");
                }
            }
        }

        static bool IsExecuting(int childId)
        {
            try
            {
                // Adopted Linux children may remain as zombies until PID 1 reaps them; they cannot execute.
                return !File.ReadAllText("/proc/" + childId + "/stat").Contains(") Z ", StringComparison.Ordinal);
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
    }

    // S: native child chdir must not turn a relative PATH entry into execution of package-provided code.
    [Fact]
    public void ContainedLaunchDoesNotExecuteAGitFromThePackage()
    {
        if (!OperatingSystem.IsLinux()) return;
        var probe = BuildProbe();
        var launcher = DirectoryAt("launcher");
        var trustedTools = Path.Combine(launcher, "tools");
        Directory.CreateDirectory(trustedTools);
        var realGit = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.GetFullPath(Path.Combine(directory, "git"))).First(File.Exists);
        var wrapper = Path.Combine(trustedTools, "git");
        File.WriteAllText(wrapper, "#!/bin/sh\nexec '" + realGit + "' \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var source = Path.Combine(_external, "path-source");
        Directory.CreateDirectory(Path.Combine(source, "tools"));
        File.WriteAllText(Path.Combine(source, "content"), "safe");
        var sentinel = Path.Combine(_root, "PACKAGE_EXECUTED");
        var packagedGit = Path.Combine(source, "tools", "git");
        File.WriteAllText(packagedGit, "#!/bin/sh\nprintf unsafe > '" + sentinel + "'\nexit 42\n");
        File.SetUnixFileMode(packagedGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var normal = Probe(probe, ["local", source]);
        Assert.Equal(0, normal.Exit);
        Assert.Equal(["content", "tools/git"], JsonSerializer.Deserialize<string[]>(normal.Output)!);
        var relative = Probe(probe, ["local", source], new() { ["PATH"] = "tools" }, launcher);
        Assert.False(File.Exists(sentinel), "Native launch executed Git from inside the untrusted package.");
        Assert.Equal(0, relative.Exit);
        Assert.Equal(normal.Output, relative.Output);
    }

    // S: an unchecked Git list can expand installation beyond the promised package boundary without an error.
    [Fact]
    public void GitFileCountCannotExpandTheCopyBoundary()
    {
        var repo = Repository("file-count");
        File.WriteAllText(Path.Combine(repo, "one"), "one");
        Assert.Equal(["one"], PluginGit.GetLocalFiles(repo));
        for (var i = 0; i < 10000; i++) File.WriteAllText(Path.Combine(repo, "file-" + i), "");
        Assert.Throws<RLoopException>(() => PluginGit.GetLocalFiles(repo));
    }

    // S: a commit prefix is an explicit pin, not HEAD; a branch/tag ambiguity must not silently choose another commit.
    [Fact]
    public void ExplicitCommitPrefixesAndAmbiguousNamesDoNotSelectAnotherCommit()
    {
        var repo = Repository("ref-selection");
        File.WriteAllText(Path.Combine(repo, "content"), "first");
        Git(repo, "add", "."); Git(repo, "commit", "-m", "first");
        var first = Git(repo, "rev-parse", "HEAD").Trim();
        Git(repo, "branch", "release");
        File.WriteAllText(Path.Combine(repo, "content"), "second");
        Git(repo, "commit", "-am", "second"); Git(repo, "tag", "release");
        var url = new Uri(repo + "/").AbsoluteUri;
        var package = DirectoryAt("pin-package");
        var exported = PluginGit.Export(url, first[..12], DirectoryAt("pin-clone"), package);
        Assert.Equal(first, exported.Commit);
        Assert.Equal("first", File.ReadAllText(Path.Combine(package, "content")));
        Assert.Throws<RLoopException>(() => PluginGit.Export(url, "release", DirectoryAt("ambiguous-clone"), DirectoryAt("ambiguous-package")));
        var explicitPackage = DirectoryAt("branch-package");
        Assert.Equal(first, PluginGit.Export(url, "refs/heads/release", DirectoryAt("branch-clone"), explicitPackage).Commit);
        Assert.Equal("first", File.ReadAllText(Path.Combine(explicitPackage, "content")));
    }

    // S: installing from an alias must not publish ignored secrets; removal must not erase other data.
    [Fact]
    public void InstallVerifyRemovePreservesUnrelatedData()
    {
        var registry = Project("作業 ワールド");
        var source = Repository("install-source");
        WritePackage(source, "alpha-plugin");
        File.WriteAllText(Path.Combine(source, ".gitignore"), "private\n");
        Git(source, "add", ".");
        var secret = FakeSecret();
        File.WriteAllText(Path.Combine(source, "private"), secret);
        File.WriteAllText(Path.Combine(source, "追加.txt"), "untracked 日本語\nsecond line\n");
        var alias = Path.Combine(_root, "source-alias");
        Directory.CreateSymbolicLink(alias, source);
        var installer = new PluginInstaller(registry);
        var installed = installer.Install(alias);
        Assert.NotNull(installed);
        Assert.Equal(source, installed.Record.Source.Location);
        Assert.Equal("directory", installed.Record.Source.Kind);
        Assert.Null(installed.Record.Commit);
        Assert.Null(installed.Record.Source.Ref);
        Assert.Equal("untracked 日本語\nsecond line\n", File.ReadAllText(Path.Combine(installed.PackageDirectory, "追加.txt")));
        Assert.False(File.Exists(Path.Combine(installed.PackageDirectory, "private")));
        Assert.False(Directory.Exists(Path.Combine(installed.PackageDirectory, ".git")));
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(installed.InstallDirectory, "install.json")));
        AssertRecordedBytes(installed);
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(Path.Combine(installed.PackageDirectory, "run.sh")).HasFlag(UnixFileMode.UserExecute));
        var other = installer.Install(Package("other", "other-plugin"));
        File.WriteAllText(Path.Combine(installed.DataDirectory, "user-data"), "preserve until confirmed");
        var sentinel = Path.Combine(_external, "sentinel");
        File.WriteAllText(sentinel, "external bytes");
        Directory.CreateSymbolicLink(Path.Combine(installed.DataDirectory, "outside"), _external);
        var distributed = Path.Combine(registry.ProjectRoot, "distributed-skill");
        File.WriteAllText(distributed, "owned by user");
        var otherBytes = Snapshot(other.InstallDirectory);
        using (var lease = registry.OpenVerified())
        {
            var leased = Assert.Single(lease.Plugins, p => p.Name == "alpha-plugin");
            Assert.Equal("#!/bin/sh\nprintf '日本語\\n'\n", File.ReadAllText(Path.Combine(leased.PackageDirectory, "run.sh")));
        }
        var record = Path.Combine(installed.InstallDirectory, "install.json");
        File.WriteAllText(record, "broken record");
        Assert.Equal("PLUGIN_RECORD_INVALID", Assert.Single(registry.List(), p => p.Name == "alpha-plugin").Problem!.Code);
        var before = Snapshot(installed.InstallDirectory, skipLinks: true);
        Assert.Equal("CONFIRMATION_REQUIRED", Assert.Throws<RLoopException>(() => installer.Remove("alpha-plugin", false)).Code);
        Assert.Equal(before, Snapshot(installed.InstallDirectory, skipLinks: true));
        installer.Remove("alpha-plugin", true);
        Assert.False(Directory.Exists(installed.InstallDirectory));
        Assert.Equal("external bytes", File.ReadAllText(sentinel));
        Assert.Equal("owned by user", File.ReadAllText(distributed));
        Assert.Equal(otherBytes, Snapshot(other.InstallDirectory));
        Assert.Equal("other-plugin", Assert.Single(registry.LoadAll()).Name);
    }

    // S: stage failures must neither replace a working plugin nor clean up another operation's staging/data.
    [Fact]
    public void FailedInstallKeepsExistingInstallation()
    {
        var project = Project("failure-project");
        var registry = new PluginRegistry(project.ProjectRoot, "0.2.0", new PluginNameReservations(["reserved-command"], [], []));
        var installer = new PluginInstaller(registry);
        var good = installer.Install(Package("good", "alpha-plugin", "shared-command"));
        Assert.NotNull(good);
        File.WriteAllText(Path.Combine(good.DataDirectory, "user-data"), "user bytes");
        var before = Snapshot(good.InstallDirectory);
        var stale = Path.Combine(registry.PluginsDirectory, ".staging-not-ours");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "keep"), "old staging");
        var invalid = Package("invalid", "invalid-plugin");
        File.WriteAllText(Path.Combine(invalid, "resoloop-plugin.json"), "{}");
        var incompatible = Package("incompatible", "future-plugin", minVersion: "9.0.0");
        var conflict = Package("conflict", "conflict-plugin", "shared-command");
        var duplicate = Package("duplicate", "alpha-plugin");
        var reserved = Package("reserved", "reserved-plugin", "reserved-command");
        var oversized = Package("oversized", "large-plugin");
        using (var stream = File.Create(Path.Combine(oversized, "large"))) stream.SetLength(200L * 1024 * 1024 + 1);
        foreach (var (source, code) in new[] { (invalid, "PLUGIN_MANIFEST_INVALID"), (incompatible, "PLUGIN_INCOMPATIBLE_VERSION"),
            (conflict, "PLUGIN_NAME_CONFLICT"), (reserved, "PLUGIN_NAME_CONFLICT"), (duplicate, "PLUGIN_ALREADY_INSTALLED"), (oversized, "PLUGIN_SOURCE_INVALID") })
        {
            Assert.Equal(code, Assert.Throws<RLoopException>(() => installer.Install(source)).Code);
            Assert.Equal(before, Snapshot(good.InstallDirectory));
            Assert.Equal([stale], Directory.GetDirectories(registry.PluginsDirectory, ".staging-*"));
            Assert.Equal("old staging", File.ReadAllText(Path.Combine(stale, "keep")));
            Assert.Single(registry.LoadAll());
        }
        // A pre-existing public destination is never overwritten, even when it cannot be loaded.
        var occupied = Path.Combine(registry.PluginsDirectory, "occupied-plugin");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "user"), "do not overwrite");
        Assert.Throws<RLoopException>(() => installer.Install(Package("occupied", "occupied-plugin")));
        Assert.Equal("do not overwrite", File.ReadAllText(Path.Combine(occupied, "user")));
        Assert.Equal(before, Snapshot(good.InstallDirectory));
    }

    // S: an ignore conflict can expose payload/data/records to Git; installer must not replace user rules.
    [Fact]
    public void ManagementIgnoreProtectsAllArtifactsWithoutOverwritingUserRules()
    {
        var registry = Project("ignore-project", git: true);
        var management = Path.Combine(registry.ProjectRoot, ".resoloop");
        Directory.CreateDirectory(management);
        var outerIgnore = Path.Combine(management, ".gitignore");
        File.WriteAllText(outerIgnore, "user-rule\n");
        Directory.CreateDirectory(registry.PluginsDirectory);
        var ignore = Path.Combine(registry.PluginsDirectory, ".gitignore");
        File.WriteAllText(ignore, "different user rules\n");
        var installer = new PluginInstaller(registry);
        var source = Package("ignore-source", "ignore-plugin");
        Assert.Equal("PLUGIN_IGNORE_CONFLICT", Assert.Throws<RLoopException>(() => installer.Install(source)).Code);
        Assert.Equal("different user rules\n", File.ReadAllText(ignore));
        Assert.Empty(Directory.GetDirectories(registry.PluginsDirectory));
        File.Delete(ignore); // fixture owner removes its conflicting rules, not the installer
        var installed = installer.Install(source);
        Assert.NotNull(installed);
        File.WriteAllText(Path.Combine(installed.DataDirectory, "private"), FakeSecret());
        var stage = Path.Combine(registry.PluginsDirectory, ".staging-old");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "private"), FakeSecret());
        var targets = new[] { ".resoloop/plugins/ignore-plugin/package/resoloop-plugin.json", ".resoloop/plugins/ignore-plugin/install.json",
            ".resoloop/plugins/ignore-plugin/data/private", ".resoloop/plugins/.staging-old/private", ".resoloop/plugins/.gitignore" };
        var ignored = Git(registry.ProjectRoot, new[] { "check-ignore", "--no-index", "--" }.Concat(targets).ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(targets, ignored);
        Assert.Equal("user-rule\n", File.ReadAllText(outerIgnore));
    }

    // S (#111): removing through a linked management parent must never delete the external installation.
    [Fact]
    public void UnsafeParentsNeverDeleteTargets()
    {
        var registry = Project("unsafe-project");
        var installer = new PluginInstaller(registry);
        var source = Package("unsafe-source", "alpha-plugin");
        var installed = installer.Install(source);
        Assert.NotNull(installed);
        File.WriteAllText(Path.Combine(installed.DataDirectory, "sentinel"), "outside installation");
        var management = Path.Combine(registry.ProjectRoot, ".resoloop");
        var outside = Path.Combine(_root, "moved-management");
        Directory.Move(management, outside);
        Directory.CreateSymbolicLink(management, outside);
        var before = Snapshot(outside);
        Assert.Equal("PLUGIN_PATH_UNSAFE", Assert.Throws<RLoopException>(() => installer.Remove("alpha-plugin", true)).Code);
        Assert.Equal("PLUGIN_PATH_UNSAFE", Assert.Throws<RLoopException>(() => installer.Install(source)).Code);
        Assert.Equal(before, Snapshot(outside));
        Directory.Delete(management);
        Directory.Move(outside, management);
        installer.Remove("alpha-plugin", true);
        Assert.False(Directory.Exists(installed.InstallDirectory));
        Assert.Equal("alpha-plugin", installer.Install(source).Name);
    }

    // S: cached source identity/hashes must not be used after copying; only verified staged bytes are published.
    [Fact]
    public void CommitUsesStagedIdentity()
    {
        var registry = Project("staged-project");
        var source = Package("stage-source", "original-plugin");
        var installer = new PluginInstaller(registry, stagedPackage =>
        {
            WritePackage(stagedPackage, "staged-plugin", version: "2.3.4");
            File.WriteAllText(Path.Combine(stagedPackage, "run.sh"), "actual staged bytes\n");
        });
        var installed = installer.Install(source);
        Assert.NotNull(installed);
        Assert.Equal("staged-plugin", installed.Name);
        Assert.Equal("2.3.4", installed.Record.Version);
        Assert.Equal("staged-plugin", Path.GetFileName(installed.InstallDirectory));
        Assert.False(Directory.Exists(Path.Combine(registry.PluginsDirectory, "original-plugin")));
        Assert.Equal("original-plugin", PluginManifest.Read(source).Name);
        AssertRecordedBytes(installed);
        Assert.Equal("actual staged bytes\n", File.ReadAllText(Path.Combine(registry.Load("staged-plugin").PackageDirectory, "run.sh")));
    }

    // S: a source overlapping the project can recursively copy staging or leak project-owned content.
    [Fact]
    public void OverlappingSourcesNeverCaptureTheProject()
    {
        var registry = Project("overlap-project");
        var installer = new PluginInstaller(registry);
        var good = installer.Install(Package("overlap-good", "good-plugin"));
        Assert.NotNull(good);
        var before = Snapshot(good.InstallDirectory);
        var child = Path.Combine(registry.ProjectRoot, "source");
        Directory.CreateDirectory(child);
        WritePackage(child, "overlap-plugin");
        Assert.Equal("PLUGIN_SOURCE_INVALID", Assert.Throws<RLoopException>(() => installer.Install(child)).Code);
        Assert.Equal(before, Snapshot(good.InstallDirectory));
        Assert.Empty(Directory.GetDirectories(registry.PluginsDirectory, ".staging-*"));
    }

    // S: a file:// URL is still a local source; bypassing overlap checks can capture project-owned files.
    [Fact]
    public void FileUrlCannotCaptureItsOwnProject()
    {
        var registry = Project("file-url-overlap", git: true);
        WritePackage(registry.ProjectRoot, "overlap-plugin");
        File.WriteAllText(Path.Combine(registry.ProjectRoot, "user-content"), "project-owned bytes");
        Git(registry.ProjectRoot, "add", "."); Git(registry.ProjectRoot, "commit", "-m", "project source");
        var before = Snapshot(registry.ProjectRoot);
        Assert.Equal("PLUGIN_SOURCE_INVALID", Assert.Throws<RLoopException>(() => new PluginInstaller(registry)
            .Install(new Uri(registry.ProjectRoot + "/").AbsoluteUri, "main")).Code);
        Assert.Equal(before, Snapshot(registry.ProjectRoot));
        Assert.False(Directory.Exists(registry.PluginsDirectory));
        var remote = Repository("separate-file-url");
        WritePackage(remote, "accepted-plugin");
        Git(remote, "add", "."); Git(remote, "commit", "-m", "separate package");
        Assert.Equal("accepted-plugin", new PluginInstaller(registry).Install(new Uri(remote + "/").AbsoluteUri, "main").Name);
    }

    // S: publishing clone metadata can leak Git credentials/config and mutable repository state.
    [Fact]
    public void GitInstallPublishesOnlyVerifiedPackageAndPinnedRecord()
    {
        var registry = Project("git-install-project");
        var source = Repository("git-install-source@version");
        WritePackage(source, "remote-plugin");
        Git(source, "add", "."); Git(source, "commit", "-m", "package");
        var commit = Git(source, "rev-parse", "HEAD").Trim();
        var installed = new PluginInstaller(registry).Install(new Uri(source + "/").AbsoluteUri, "main");
        Assert.NotNull(installed);
        Assert.Equal(commit, installed.Record.Commit);
        Assert.Equal("main", installed.Record.Source.Ref);
        Assert.Equal("git", installed.Record.Source.Kind);
        Assert.Equal(["resoloop-plugin.json", "run.sh"], installed.Record.Files.Keys);
        AssertRecordedBytes(installed);
        Assert.Equal(["data", "package"], Directory.GetDirectories(installed.InstallDirectory).Select(path => Path.GetFileName(path)!).Order().ToArray());
        Assert.Equal(["install.json"], Directory.GetFiles(installed.InstallDirectory).Select(path => Path.GetFileName(path)!).ToArray());
        Assert.Empty(Directory.GetDirectories(registry.PluginsDirectory, ".staging-*"));
        using var lease = registry.OpenVerified();
        Assert.Equal("remote-plugin", Assert.Single(lease.Plugins).Name);
    }

    // S: the public Installer (not only the lock primitive) must serialize cross-process mutation with real use.
    [Fact]
    public void LeaseAndMutationSerialize()
    {
        var registry = Project("lease-project");
        var installer = new PluginInstaller(registry);
        var installed = installer.Install(Package("lease-source", "alpha-plugin"));
        Assert.NotNull(installed);
        var next = Package("lease-next", "next-plugin");
        var probe = BuildProbe();
        var before = Snapshot(installed.InstallDirectory);
        using (var lease = registry.OpenVerified())
        {
            foreach (var args in new[] { new[] { "install", registry.ProjectRoot, next }, new[] { "remove", registry.ProjectRoot, "alpha-plugin" } })
            {
                var blocked = Probe(probe, args);
                Assert.Equal(7, blocked.Exit);
                Assert.Contains("PLUGIN_PROJECT_BUSY", blocked.Output);
                Assert.Equal(before, Snapshot(installed.InstallDirectory));
                Assert.Equal("#!/bin/sh\nprintf '日本語\\n'\n", File.ReadAllText(Path.Combine(Assert.Single(lease.Plugins).PackageDirectory, "run.sh")));
                Assert.False(Directory.Exists(Path.Combine(registry.PluginsDirectory, "next-plugin")));
            }
        }
        Assert.Equal(0, Probe(probe, ["install", registry.ProjectRoot, next]).Exit);
        Assert.Equal(0, Probe(probe, ["remove", registry.ProjectRoot, "alpha-plugin"]).Exit);
        Assert.False(Directory.Exists(installed.InstallDirectory));
        using var finalLease = registry.OpenVerified();
        Assert.Equal("next-plugin", Assert.Single(finalLease.Plugins).Name);
    }

    private PluginRegistry Project(string name, bool git = false)
    {
        var path = git ? Repository(name) : DirectoryAt(name);
        File.WriteAllText(Path.Combine(path, ".resoloop.json"), "{}");
        return new(path, "0.2.0", new PluginNameReservations([], [], []));
    }

    private string Package(string directory, string name, string? command = null, string minVersion = "0.2.0")
    {
        var path = Path.Combine(_external, directory);
        Directory.CreateDirectory(path);
        WritePackage(path, name, command, minVersion);
        return path;
    }

    private static void WritePackage(string path, string name, string? command = null, string minVersion = "0.2.0", string version = "1.0.0")
    {
        File.WriteAllText(Path.Combine(path, "resoloop-plugin.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, name, version, resoloop = new { minVersion },
            commands = new[] { new { name = command ?? name + "-run", description = "offline 日本語 command", program = "./run.sh" } }
        }));
        File.WriteAllText(Path.Combine(path, "run.sh"), "#!/bin/sh\nprintf '日本語\\n'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(path, "run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string[] Snapshot(string directory, bool skipLinks = false) => Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = skipLinks ? FileAttributes.ReparsePoint : 0 })
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static void AssertRecordedBytes(InstalledPlugin installed)
    {
        var actual = Directory.GetFiles(installed.PackageDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(installed.PackageDirectory, path).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(actual, installed.Record.Files.Keys);
        foreach (var relative in actual)
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(installed.PackageDirectory, relative)))), installed.Record.Files[relative]);
        Assert.Equal(installed.Record.Files, PluginInstallRecord.Read(Path.Combine(installed.InstallDirectory, "install.json")).Files);
    }

    private string DirectoryAt(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Repository(string name)
    {
        var path = DirectoryAt(name);
        Git(path, "init", "-b", "main");
        Git(path, "config", "user.name", "Offline Test");
        Git(path, "config", "user.email", "offline@example.invalid");
        return path;
    }

    private static string FakeSecret() => string.Join('_', "fixture", Guid.NewGuid().ToString("N"));

    private static string Git(string directory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var key in process.StartInfo.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            process.StartInfo.Environment.Remove(key);
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr.GetAwaiter().GetResult());
        return stdout.GetAwaiter().GetResult();
    }

    private string BuildProbe()
    {
        var artifacts = Path.Combine(_root, "probe-" + Guid.NewGuid().ToString("N"));
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "core", "git-probe", "GitProbe.csproj");
        using var build = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "build", fixture, "--nologo", "--artifacts-path", artifacts,
                "-p:ResoloopCoreAssembly=" + typeof(PluginSource).Assembly.Location }
        })!;
        var stdout = build.StandardOutput.ReadToEndAsync(); var stderr = build.StandardError.ReadToEndAsync();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        return Path.Combine(artifacts, "bin", "GitProbe", "debug", "GitProbe.dll");
    }

    private static (int Exit, string Output) Probe(string probe, string[] arguments, Dictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        using var process = new Process { StartInfo = new("dotnet") { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        if (workingDirectory is not null) process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.ArgumentList.Add(probe);
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        if (environment is not null) foreach (var (key, value) in environment) process.StartInfo.Environment[key] = value;
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        Directory.Delete(_external, recursive: true);
    }
}
