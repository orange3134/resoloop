using System.Text.Json;

namespace RLoop.Core;

public sealed record RLoopConfig(
    string? ResoniteLinkUrl = null,
    int TimeoutSeconds = 0,
    int CommandTimeoutSeconds = 0,
    string? FluxExecutable = null,
    string? FluxDeployerPath = null,
    string? ResoniteManagedDataPath = null,
    string? ResoniteLogPath = null,
    string? ScreenshotsDirectory = null);

public sealed record ConfigResolution(RLoopConfig Config, IReadOnlyDictionary<string, string> Sources);

public static class ConfigResolver
{
    public const string ProjectFileName = ".resoloop.json";

    public static ConfigResolution Resolve(
        string startDirectory,
        IReadOnlyDictionary<string, string?> cli,
        Func<string, string?>? getEnvironment = null,
        string? userProfile = null)
    {
        getEnvironment ??= Environment.GetEnvironmentVariable;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var userPath = Path.Combine(userProfile, ".resoloop", "config.json");
        var projectPath = FindProjectConfigPath(startDirectory);
        var user = ReadConfig(userPath);
        var project = ReadConfig(projectPath);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? Pick(string cliKey, string envKey, Func<RLoopConfig, string?> selector, string setting)
        {
            if (cli.TryGetValue(cliKey, out var cliValue) && !string.IsNullOrWhiteSpace(cliValue))
            {
                sources[setting] = "cli";
                return cliValue;
            }
            var env = getEnvironment(envKey);
            if (!string.IsNullOrWhiteSpace(env))
            {
                sources[setting] = $"environment:{envKey}";
                return env;
            }
            var projectValue = selector(project);
            if (!string.IsNullOrWhiteSpace(projectValue))
            {
                sources[setting] = projectPath!;
                return projectValue;
            }
            var userValue = selector(user);
            if (!string.IsNullOrWhiteSpace(userValue))
            {
                sources[setting] = userPath;
                return userValue;
            }
            return null;
        }

        var url = Pick("url", "RESONITE_LINK_URL", x => x.ResoniteLinkUrl, "resoniteLinkUrl");
        var flux = Pick("flux-executable", "RESOLOOP_FLUX_EXECUTABLE", x => x.FluxExecutable, "fluxExecutable") ?? "flux-sdk";
        var helper = Pick("flux-deployer", "RESOLOOP_FLUX_DEPLOYER", x => x.FluxDeployerPath, "fluxDeployerPath");
        var managed = Pick("library-path", "RESONITE_MANAGED_DATA_PATH", x => x.ResoniteManagedDataPath, "resoniteManagedDataPath");
        var logs = Pick("log-path", "RESONITE_LOG_PATH", x => x.ResoniteLogPath, "resoniteLogPath");
        var screenshots = Pick("screenshots-dir", "RESOLOOP_SCREENSHOTS_DIR", x => x.ScreenshotsDirectory, "screenshotsDirectory");

        var timeout = project.TimeoutSeconds > 0 ? project.TimeoutSeconds : user.TimeoutSeconds > 0 ? user.TimeoutSeconds : 30;
        if (cli.TryGetValue("timeout", out var timeoutText) && !string.IsNullOrWhiteSpace(timeoutText))
        {
            if (!int.TryParse(timeoutText, out timeout) || timeout <= 0)
                throw new RLoopException("INVALID_TIMEOUT", $"Timeout must be a positive number of seconds, got '{timeoutText}'.", ExitCodes.InvalidArguments);
            sources["timeoutSeconds"] = "cli";
        }
        else if (int.TryParse(getEnvironment("RESOLOOP_TIMEOUT_SECONDS"), out var envTimeout) && envTimeout > 0)
        {
            timeout = envTimeout;
            sources["timeoutSeconds"] = "environment:RESOLOOP_TIMEOUT_SECONDS";
        }

        var commandTimeout = project.CommandTimeoutSeconds > 0 ? project.CommandTimeoutSeconds :
            user.CommandTimeoutSeconds > 0 ? user.CommandTimeoutSeconds : 900;
        if (cli.TryGetValue("command-timeout", out var commandTimeoutText) && !string.IsNullOrWhiteSpace(commandTimeoutText))
        {
            if (!int.TryParse(commandTimeoutText, out commandTimeout) || commandTimeout <= 0)
                throw new RLoopException("INVALID_COMMAND_TIMEOUT", $"Command timeout must be a positive number of seconds, got '{commandTimeoutText}'.", ExitCodes.InvalidArguments);
            sources["commandTimeoutSeconds"] = "cli";
        }
        else if (int.TryParse(getEnvironment("RESOLOOP_COMMAND_TIMEOUT_SECONDS"), out var envCommandTimeout) && envCommandTimeout > 0)
        {
            commandTimeout = envCommandTimeout;
            sources["commandTimeoutSeconds"] = "environment:RESOLOOP_COMMAND_TIMEOUT_SECONDS";
        }

        return new ConfigResolution(new RLoopConfig(url, timeout, commandTimeout, flux, helper, managed, logs, screenshots), sources);
    }

    public static Uri RequireUrl(RLoopConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ResoniteLinkUrl))
            throw new RLoopException(
                "RESONITE_LINK_URL_MISSING",
                "No ResoniteLink WebSocket URL was configured.",
                ExitCodes.ConfigurationError,
                suggestions: ["Set RESONITE_LINK_URL=ws://localhost:<port> or pass --url ws://localhost:<port>."]);
        if (!Uri.TryCreate(config.ResoniteLinkUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
            throw new RLoopException("INVALID_RESONITE_LINK_URL", $"'{config.ResoniteLinkUrl}' is not a valid ws:// or wss:// URL.", ExitCodes.ConfigurationError);
        return uri;
    }

    public static string? FindProjectConfigPath(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ProjectFileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private static RLoopConfig ReadConfig(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new RLoopConfig();
        try
        {
            return JsonSerializer.Deserialize<RLoopConfig>(File.ReadAllText(path), JsonOptions) ?? new RLoopConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new RLoopException("CONFIG_INVALID", $"Could not read configuration '{path}': {ex.Message}", ExitCodes.ConfigurationError,
                new Dictionary<string, object?> { ["path"] = path }, innerException: ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
