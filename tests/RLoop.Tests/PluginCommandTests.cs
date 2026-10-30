using RLoop.Cli;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("RESOLOOP_PLUGIN_TEST_ROOT")
        ?? Path.GetTempPath(), "commands-" + Guid.NewGuid().ToString("N"));

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

    private string CreateProject(string name = "作業 ワールド")
    {
        var project = Path.Combine(_root, name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".resoloop.json"), "{}");
        return project;
    }

    private InstalledPlugin InstallPlugin(string project, string name, string commandsJson, PluginNameReservations? reservations = null)
    {
        var source = Path.Combine(_root, "導入 元", name);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "resoloop-plugin.json"),
            $$"""{"schemaVersion":1,"name":"{{name}}","version":"1.0.0","resoloop":{"minVersion":"0.2.0"},"commands":{{commandsJson}}}""");
        var registry = new PluginRegistry(project, "0.2.0", reservations ?? BuiltinCommandCatalog.Reservations);
        return new PluginInstaller(registry).Install(source);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) PluginPathPolicy.DeleteTree(_root, "PLUGIN_REMOVE_REFUSED");
    }
}
