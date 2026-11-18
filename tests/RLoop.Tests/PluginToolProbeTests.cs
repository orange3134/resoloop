using RLoop.Cli;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginToolProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("RESOLOOP_PLUGIN_TEST_ROOT")
        ?? Path.GetTempPath(), "tools-" + Guid.NewGuid().ToString("N"));

    // S: a missing or failing required tool must never be reported as passing (doctor readiness depends on it).
    [Fact]
    public async Task RequiredToolProblemsAreNeverPassing()
    {
        var project = CreateProject();
        InstallPlugin(project, "tools-plugin", """
            [{"name":"present","executable":"dotnet","versionArgs":["--version"]},
             {"name":"absent","executable":"resoloop-missing-tool-98"},
             {"name":"broken","executable":"dotnet","versionArgs":["--resoloop-invalid-option-98"]},
             {"name":"optional","executable":"resoloop-missing-tool-98","required":false}]
            """);
        var checks = await PluginDoctorChecks.RunAsync(project, _root, "0.2.0", CopyEnvironment(), CancellationToken.None);
        var byName = checks.ToDictionary(check => check.Name);
        Assert.Equal(("pass", true), (byName["plugin:tools-plugin:present"].Status, byName["plugin:tools-plugin:present"].Required));
        Assert.Equal(("fail", true), (byName["plugin:tools-plugin:absent"].Status, byName["plugin:tools-plugin:absent"].Required));
        Assert.Equal(("fail", true), (byName["plugin:tools-plugin:broken"].Status, byName["plugin:tools-plugin:broken"].Required));
        Assert.Equal(("warning", false), (byName["plugin:tools-plugin:optional"].Status, byName["plugin:tools-plugin:optional"].Required));
    }

    private string CreateProject()
    {
        var project = Path.Combine(_root, "作業 ワールド");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".resoloop.json"), "{}");
        return project;
    }

    private void InstallPlugin(string project, string name, string toolsJson)
    {
        var source = Path.Combine(_root, "導入 元", name);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "resoloop-plugin.json"),
            $$"""{"schemaVersion":1,"name":"{{name}}","version":"1.0.0","resoloop":{"minVersion":"0.2.0"},"tools":{{toolsJson}}}""");
        new PluginInstaller(new PluginRegistry(project, "0.2.0", BuiltinCommandCatalog.Reservations)).Install(source);
    }

    private static Dictionary<string, string?> CopyEnvironment()
    {
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value;
        return environment;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) PluginPathPolicy.DeleteTree(_root, "PLUGIN_REMOVE_REFUSED");
    }
}
