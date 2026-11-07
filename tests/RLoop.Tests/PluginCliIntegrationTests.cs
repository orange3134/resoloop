using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

/// <summary>
/// The real CLI process against temporary projects and a fake plugin program. Each module's own guards live in
/// its own tests; these only check that the wiring picks the right project, route and confirmation.
/// </summary>
public sealed class PluginCliIntegrationTests(CommandProbeFixture probe) : IDisposable, IClassFixture<CommandProbeFixture>
{
    private readonly string _root = Directory.CreateTempSubdirectory("resoloop-cli-integ-").FullName;

    // S: a wiring slip would run another project's command, hand rewritten argv to the plugin, report the child's
    // failure as success, delete without --yes, or skip the skills/recipes/doctor of the installed plugin.
    [Fact]
    public async Task FourFeaturesWorkThroughTheRealCli()
    {
        var project = CreateProject("作業 ワールド");
        var source = PackageSource();

        Assert.Equal(0, (await Run(project, "plugin", "install", source)).ExitCode);
        var list = await Run(project, "plugin", "list", "--json");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("integ-plugin", list.Stdout);

        // Everything after the command name reaches the plugin unchanged, and the child's exit code is kept.
        string[] pluginArgs = ["--json", "--help", "--", "", "日本語 引数", "--project=x"];
        var call = await Run(project, ["integ-record", .. pluginArgs]);
        Assert.Equal(7, call.ExitCode);
        var received = ReadReceived(project);
        Assert.Equal(new[] { "record", "7" }.Concat(pluginArgs), received);

        Assert.Equal(0, (await Run(project, "skills", "sync", "--update")).ExitCode);
        Assert.Equal("# Integration guide\n", File.ReadAllText(Path.Combine(project, ".agents", "skills", "integ-guide", "SKILL.md")).ReplaceLineEndings("\n"));

        var recipe = await Run(project, "uix", "recipe", "describe", "integ-plugin:card");
        Assert.Equal(0, recipe.ExitCode);
        Assert.Contains("integ-plugin:card", recipe.Stdout);

        // The missing required tool makes the plugin's doctor check fail; it must not be reported as ready.
        var doctor = await Run(project, "doctor", "--json", "--url", "ws://127.0.0.1:1");
        Assert.Contains("plugin:integ-plugin:missing", doctor.Stdout);
        Assert.Contains("\"ready\":false", doctor.Stdout);

        var unconfirmed = await Run(project, "plugin", "remove", "integ-plugin");
        Assert.NotEqual(0, unconfirmed.ExitCode);
        Assert.Contains("integ-plugin", (await Run(project, "plugin", "list")).Stdout);
        Assert.Equal(0, (await Run(project, "plugin", "remove", "integ-plugin", "--yes")).ExitCode);
        Assert.DoesNotContain("integ-plugin", (await Run(project, "plugin", "list")).Stdout);
    }

    // S: the project named before the command must win over the project found from the working directory.
    [Fact]
    public async Task ExplicitProjectWinsOverWorkingDirectory()
    {
        var chosen = CreateProject("選択 ワールド");
        var other = CreateProject("別 ワールド");
        Assert.Equal(0, (await Run(chosen, "plugin", "install", PackageSource())).ExitCode);

        var wrong = await Run(other, "integ-record", "a");
        Assert.Equal("UNKNOWN_COMMAND", ErrorCode(wrong.Stderr));

        var right = await RunWithArguments(other, ["--project", chosen, "integ-record", "a"]);
        Assert.Equal(7, right.ExitCode);
        Assert.Equal(new[] { "record", "7", "a" }, ReadReceived(chosen));
    }

    // S: an unknown name must stop before any ResoniteLink connection attempt.
    [Fact]
    public async Task UnknownCommandStopsBeforeConnecting()
    {
        var project = CreateProject("接続 ワールド");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = 0;
        var accepting = Task.Run(async () =>
        {
            try { while (true) { using var _ = await listener.AcceptTcpClientAsync(); Interlocked.Increment(ref accepted); } }
            catch (Exception) { }
        });
        var url = "ws://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await RunWithArguments(project, ["no-such-plugin-command"], ("RESONITE_LINK_URL", url));
        Assert.Equal(ExitCodes.InvalidArguments, result.ExitCode);
        Assert.Equal("UNKNOWN_COMMAND", ErrorCode(result.Stderr));
        await Task.Delay(300);
        listener.Stop();
        await accepting;
        Assert.Equal(0, accepted);
    }

    private string PackageSource()
    {
        var source = Path.Combine(_root, "導入 元", Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "integration", "package"), source);
        CopyDirectory(probe.OutputDirectory, Path.Combine(source, "probe"));
        return source;
    }

    private string CreateProject(string name)
    {
        var project = Path.Combine(_root, "projects", name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".resoloop.json"), "{}");
        return project;
    }

    private static string[] ReadReceived(string project)
    {
        var path = Path.Combine(project, ".resoloop", "plugins", "integ-plugin", "data", "received.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("argv").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static string? ErrorCode(string stderr) =>
        stderr.Split('\n').Select(line => line.Split(':')[0].Trim()).FirstOrDefault(code => code.Length > 0 && code.All(c => char.IsUpper(c) || c == '_'));

    private Task<CliResult> Run(string cwd, params string[] args) => RunWithArguments(cwd, args);

    private static async Task<CliResult> RunWithArguments(string cwd, string[] args, params (string Key, string Value)[] environment)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "resoloop.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Remove("RESONITE_LINK_URL");
        foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from)) CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) PluginPathPolicy.DeleteTree(_root, "PLUGIN_REMOVE_REFUSED");
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
