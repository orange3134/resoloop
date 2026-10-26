using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ConfigurationTests : IDisposable
{
    [Fact]
    public void BlenderUsesCliEnvironmentProjectUserPrecedence()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"blenderExecutable\":\"user-blender\"}");
        var project = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(project, "{\"blenderExecutable\":\"project-blender\"}");
        var cli = new Dictionary<string, string?> { ["blender-executable"] = "cli-blender" };
        string? Env(string key) => key == "RESOLOOP_BLENDER_EXECUTABLE" ? "env-blender" : null;
        Assert.Equal("cli-blender", ConfigResolver.Resolve(_root, cli, Env, user).Config.BlenderExecutable);
        cli.Clear();
        Assert.Equal("env-blender", ConfigResolver.Resolve(_root, cli, Env, user).Config.BlenderExecutable);
        Assert.Equal("project-blender", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.BlenderExecutable);
        File.Delete(project);
        Assert.Equal("user-blender", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.BlenderExecutable);
    }

    [Fact]
    public void ScreenshotDirectoryUsesCliEnvironmentProjectUserPrecedence()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"screenshotsDirectory\":\"user-photos\"}");
        var project = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(project, "{\"screenshotsDirectory\":\"project-photos\"}");
        var cli = new Dictionary<string, string?> { ["screenshots-dir"] = "cli-photos" };
        string? Env(string key) => key == "RESOLOOP_SCREENSHOTS_DIR" ? "env-photos" : null;
        Assert.Equal("cli-photos", ConfigResolver.Resolve(_root, cli, Env, user).Config.ScreenshotsDirectory);
        cli.Clear();
        Assert.Equal("env-photos", ConfigResolver.Resolve(_root, cli, Env, user).Config.ScreenshotsDirectory);
        Assert.Equal("project-photos", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.ScreenshotsDirectory);
        File.Delete(project);
        Assert.Equal("user-photos", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.ScreenshotsDirectory);
    }

    [Fact]
    public void TextureTransferUsesCliEnvironmentProjectUserPrecedence()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"textureTransfer\":\"data\"}");
        var project = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(project, "{\"textureTransfer\":\"file\"}");
        var cli = new Dictionary<string, string?> { ["texture-transfer"] = "file" };
        string? Env(string key) => key == "RESOLOOP_TEXTURE_TRANSFER" ? "DATA " : null;

        var fromCli = ConfigResolver.Resolve(_root, cli, Env, user);
        Assert.Equal("file", fromCli.Config.TextureTransfer);
        Assert.Equal("cli", fromCli.Sources["textureTransfer"]);
        cli.Clear();
        var fromEnvironment = ConfigResolver.Resolve(_root, cli, Env, user);
        Assert.Equal("data", fromEnvironment.Config.TextureTransfer);
        Assert.Equal("environment:RESOLOOP_TEXTURE_TRANSFER", fromEnvironment.Sources["textureTransfer"]);
        Assert.Equal(TextureTransfer.Data, ConfigResolver.TextureTransferOf(fromEnvironment.Config));
        Assert.Equal("file", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.TextureTransfer);
        File.Delete(project);
        Assert.Equal("data", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.TextureTransfer);
        var none = ConfigResolver.Resolve(_root, cli, _ => null, Path.Combine(_root, "none"));
        Assert.Equal("file", none.Config.TextureTransfer);
        Assert.False(none.Sources.ContainsKey("textureTransfer"));
        Assert.Equal(TextureTransfer.File, ConfigResolver.TextureTransferOf(none.Config));
    }

    [Fact]
    public void RejectsUnknownTextureTransfer()
    {
        var user = Path.Combine(_root, "none");
        var fromCli = Assert.Throws<RLoopException>(() => ConfigResolver.Resolve(_root,
            new Dictionary<string, string?> { ["texture-transfer"] = "pixels" }, _ => null, user));
        Assert.Equal("INVALID_TEXTURE_TRANSFER", fromCli.Code);
        Assert.Equal(ExitCodes.InvalidArguments, fromCli.ExitCode);

        var project = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(project, "{\"textureTransfer\":\"bytes\"}");
        var fromProject = Assert.Throws<RLoopException>(() => ConfigResolver.Resolve(_root,
            new Dictionary<string, string?>(), _ => null, user));
        Assert.Equal("INVALID_TEXTURE_TRANSFER", fromProject.Code);
        Assert.Equal(ExitCodes.ConfigurationError, fromProject.ExitCode);
        Assert.Equal(project, fromProject.Context["source"]);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigurationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResolvesCliOverEnvironmentOverProjectOverUser()
    {
        var project = Path.Combine(_root, "project");
        var child = Path.Combine(project, "src");
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(project, ".resoloop.json"), JsonSerializer.Serialize(new { resoniteLinkUrl = "ws://project:3", timeoutSeconds = 11 }));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), JsonSerializer.Serialize(new { resoniteLinkUrl = "ws://user:4", fluxExecutable = "user-flux" }));
        var env = new Dictionary<string, string?> { ["RESONITE_LINK_URL"] = "ws://env:2" };

        var result = ConfigResolver.Resolve(child, new Dictionary<string, string?> { ["url"] = "ws://cli:1" }, key => env.GetValueOrDefault(key), user);

        Assert.Equal("ws://cli:1", result.Config.ResoniteLinkUrl);
        Assert.Equal(11, result.Config.TimeoutSeconds);
        Assert.Equal("user-flux", result.Config.FluxExecutable);
        Assert.Equal("cli", result.Sources["resoniteLinkUrl"]);
    }

    [Fact]
    public void MissingUrlProducesActionableError()
    {
        var result = ConfigResolver.Resolve(_root, new Dictionary<string, string?>(), _ => null, Path.Combine(_root, "none"));
        var ex = Assert.Throws<RLoopException>(() => ConfigResolver.RequireUrl(result.Config));
        Assert.Equal("RESONITE_LINK_URL_MISSING", ex.Code);
        Assert.NotEmpty(ex.Suggestions);
    }

    [Fact]
    public void RejectsNonWebSocketUrl()
    {
        var ex = Assert.Throws<RLoopException>(() => ConfigResolver.RequireUrl(new RLoopConfig("http://localhost:1")));
        Assert.Equal("INVALID_RESONITE_LINK_URL", ex.Code);
    }

    [Fact]
    public void ResolvesIndependentRequestAndCommandTimeouts()
    {
        var result = ConfigResolver.Resolve(_root,
            new Dictionary<string, string?> { ["timeout"] = "7", ["command-timeout"] = "120" }, _ => null,
            Path.Combine(_root, "none"));

        Assert.Equal(7, result.Config.TimeoutSeconds);
        Assert.Equal(120, result.Config.CommandTimeoutSeconds);
        Assert.Throws<RLoopException>(() => ConfigResolver.Resolve(_root,
            new Dictionary<string, string?> { ["command-timeout"] = "0" }, _ => null, Path.Combine(_root, "none")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
