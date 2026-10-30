using System.Diagnostics;
using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginCommandTests(CommandProbeFixture probe) : IDisposable, IClassFixture<CommandProbeFixture>
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("RESOLOOP_PLUGIN_TEST_ROOT")
        ?? Path.GetTempPath(), "commands-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _spawned = [];

    // S: a plugin installed by an older ResoLoop must never take over a built-in name.
    [Fact]
    public void BuiltinNamesNeverReachPlugins()
    {
        var project = CreateProject();
        InstallPlugin(project, "legacy-plugin",
            """[{"name":"import","description":"old","program":"dotnet"},{"name":"legacy-run","description":"run","program":"dotnet"}]""",
            new PluginNameReservations([], [], []));
        Assert.Null(PluginCommandDispatch.Parse(["--project", project, "import", "x"]));
        Assert.Null(PluginCommandDispatch.Parse(["--project=" + project, "IMPORT"]));
        var invocation = PluginCommandDispatch.Parse(["--project", project, "legacy-run", "--json"]);
        Assert.NotNull(invocation);
        Assert.Equal(("legacy-run", project), (invocation!.Command, invocation.Project));
        Assert.Equal(["--json"], invocation.Arguments);
    }

    // S: descendants left by a finished command must not keep using the package after the lease is released;
    // a failing child exit code must never become success.
    [Fact]
    public async Task NormalExitStopsLeftoverDescendants()
    {
        var project = CreateProject();
        var plugin = InstallPlugin(project, "probe-plugin",
            """[{"name":"probe-spawn","description":"spawn","program":"dotnet","args":["${pluginRoot}/probe/CommandProbe.dll","spawn","7"]}]""", withProbe: true);
        PluginPathPolicy.DeleteTree(plugin.DataDirectory, "PLUGIN_REMOVE_REFUSED");
        var exit = await PluginCommandRunner.RunAsync(Request(plugin, "probe-spawn", [], TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal(7, exit);
        var pids = ReadPids(plugin);
        Assert.False(IsRunning(pids.Child));
        Assert.False(IsRunning(pids.Grandchild));
    }

    // S: a deadline must never report success and must stop the whole tree.
    [Fact]
    public async Task TimedOutCommandsFailAndStopTheirProcessTree()
    {
        var project = CreateProject();
        var plugin = InstallPlugin(project, "probe-plugin",
            """[{"name":"probe-hang","description":"hang","program":"dotnet","args":["${pluginRoot}/probe/CommandProbe.dll","spawn","hang"]}]""", withProbe: true);
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            PluginCommandRunner.RunAsync(Request(plugin, "probe-hang", [], TimeSpan.FromSeconds(5)), CancellationToken.None));
        Assert.Equal(("PLUGIN_COMMAND_TIMEOUT", ExitCodes.Timeout), (error.Code, error.ExitCode));
        Assert.InRange(watch.Elapsed.TotalSeconds, 5, 40);
        var pids = ReadPids(plugin);
        Assert.False(IsRunning(pids.Child));
        Assert.False(IsRunning(pids.Grandchild));
    }

    // S: only the package or absolute PATH entries may supply the program; never cwd/project files or Windows batch files.
    [Fact]
    public async Task ProgramsResolveOnlyFromAbsolutePathOrThePackage()
    {
        var project = CreateProject();
        var marker = Path.Combine(_root, "marker-ran");
        // A launchable "evil" program inside the project, reachable only through relative PATH entries:
        // one relative to the child's cwd (the project) and one relative to the host's cwd.
        var tools = Path.Combine(project, "tools");
        CopyDirectory(probe.OutputDirectory, tools);
        File.Copy(Path.Combine(tools, ApphostName()), Path.Combine(tools, OperatingSystem.IsWindows() ? "evil.exe" : "evil"));
        var entries = new List<string> { "tools", Path.GetRelativePath(Environment.CurrentDirectory, tools) };
        if (OperatingSystem.IsWindows())
        {
            // An absolute entry that only offers a batch file, which CreateProcess would run through cmd.exe.
            var batch = Path.Combine(_root, "batch");
            Directory.CreateDirectory(batch);
            File.WriteAllText(Path.Combine(batch, "evil.cmd"), "@echo ran> \"" + marker + "\"\r\n");
            entries.Add(batch);
        }
        var environment = CopyEnvironment();
        environment["PATH"] = string.Join(Path.PathSeparator, entries.Append(environment["PATH"]));
        var plugin = InstallPlugin(project, "probe-plugin",
            $$"""[{"name":"probe-evil","description":"evil","program":"evil","args":["marker",{{JsonSerializer.Serialize(marker)}}]},{"name":"probe-local","description":"local","program":"./probe/{{ApphostName()}}","args":["record","0"]}]""",
            withProbe: true);
        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            PluginCommandRunner.RunAsync(Request(plugin, "probe-evil", [], TimeSpan.FromSeconds(30), environment), CancellationToken.None));
        Assert.Equal("PLUGIN_COMMAND_START_FAILED", error.Code);
        Assert.False(File.Exists(marker));
        Assert.Equal(0, await PluginCommandRunner.RunAsync(Request(plugin, "probe-local", [], TimeSpan.FromSeconds(60), environment), CancellationToken.None));
    }

    private string CreateProject(string name = "作業 ワールド")
    {
        var project = Path.Combine(_root, name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".resoloop.json"), "{}");
        return project;
    }

    private InstalledPlugin InstallPlugin(string project, string name, string commandsJson,
        PluginNameReservations? reservations = null, bool withProbe = false)
    {
        var source = Path.Combine(_root, "導入 元", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(source);
        if (withProbe) CopyDirectory(probe.OutputDirectory, Path.Combine(source, "probe"));
        File.WriteAllText(Path.Combine(source, "resoloop-plugin.json"),
            $$"""{"schemaVersion":1,"name":"{{name}}","version":"1.0.0","resoloop":{"minVersion":"0.2.0"},"commands":{{commandsJson}}}""");
        var registry = new PluginRegistry(project, "0.2.0", reservations ?? BuiltinCommandCatalog.Reservations);
        return new PluginInstaller(registry).Install(source);
    }

    private static PluginCommandRequest Request(InstalledPlugin plugin, string command, IReadOnlyList<string> args, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        new(plugin, plugin.Manifest.Commands.Single(item => item.Name == command),
            Path.GetFullPath(Path.Combine(plugin.InstallDirectory, "..", "..", "..")), args, ["/host/resoloop"], "0.2.0", timeout,
            environment ?? CopyEnvironment());

    private static Dictionary<string, string?> CopyEnvironment()
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value;
        return environment;
    }

    private (int Child, int Grandchild) ReadPids(InstalledPlugin plugin)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin.DataDirectory, "pids.json")));
        var pids = (json.RootElement.GetProperty("child").GetInt32(), json.RootElement.GetProperty("grandchild").GetInt32());
        _spawned.AddRange([pids.Item1, pids.Item2]);
        return pids;
    }

    private static bool IsRunning(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            // Adopted Linux children may remain as zombies until PID 1 reaps them; they cannot execute.
            try { return !File.ReadAllText("/proc/" + pid + "/stat").Contains(") Z ", StringComparison.Ordinal); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static string ApphostName() => OperatingSystem.IsWindows() ? "CommandProbe.exe" : "CommandProbe";

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from)) CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    public void Dispose()
    {
        // Also clean up after a failed assertion. Only stop a PID that still identifies this fixture's probe.
        foreach (var pid in _spawned.Where(IsRunning))
        {
            using var process = Process.GetProcessById(pid);
            var identity = OperatingSystem.IsLinux() ? File.ReadAllText("/proc/" + pid + "/cmdline") : process.ProcessName;
            if (identity.Contains("CommandProbe", StringComparison.Ordinal) || identity is "dotnet") process.Kill();
        }
        if (Directory.Exists(_root)) PluginPathPolicy.DeleteTree(_root, "PLUGIN_REMOVE_REFUSED");
    }
}

/// <summary>Builds the fake plugin program once per test class, outside the repository tree.</summary>
public sealed class CommandProbeFixture : IDisposable
{
    private readonly string _artifacts = Path.Combine(Path.GetTempPath(), "command-probe-" + Guid.NewGuid().ToString("N"));

    public CommandProbeFixture()
    {
        var project = Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "commands", "command-probe", "CommandProbe.csproj");
        using var build = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "build", project, "--nologo", "--artifacts-path", _artifacts }
        })!;
        var stdout = build.StandardOutput.ReadToEndAsync();
        var stderr = build.StandardError.ReadToEndAsync();
        build.WaitForExit();
        if (build.ExitCode != 0) throw new InvalidOperationException(stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        OutputDirectory = Path.Combine(_artifacts, "bin", "CommandProbe", "debug");
    }

    public string OutputDirectory { get; }

    public void Dispose()
    {
        if (Directory.Exists(_artifacts)) Directory.Delete(_artifacts, recursive: true);
    }
}
