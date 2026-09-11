using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RLoop.Core;

public sealed record BlenderLocation(string Executable, string Source);
public sealed record BlenderProcessResult(string Executable, int ExitCode, string StandardOutput, string StandardError);

public static class BlenderDiscovery
{
    // Bounded discovery only. Portable/custom/Steam installs can be selected explicitly.
    public static BlenderLocation Resolve(string? configured = null,
        IEnumerable<BlenderLocation>? candidates = null, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured);
            if (exists(path)) return new(path, "configuration");
            throw new RLoopException("BLENDER_PATH_INVALID", $"Configured Blender executable does not exist: {path}", ExitCodes.ConfigurationError);
        }
        foreach (var item in candidates ?? Candidates())
            if (exists(item.Executable)) return item with { Executable = Path.GetFullPath(item.Executable) };
        throw new RLoopException("BLENDER_NOT_FOUND", "Blender was not found in PATH, standard install locations or Windows registry.",
            ExitCodes.ConfigurationError, suggestions: [
                "Set --blender-executable PATH, RESOLOOP_BLENDER_EXECUTABLE, or blenderExecutable in configuration for a portable/custom installation.",
                "If Blender is not installed, ask the user for installation permission before installing; resoloop never installs it automatically."]);
    }

    private static IEnumerable<BlenderLocation> Candidates()
    {
        var executable = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(dir)) yield return new(Path.Combine(dir.Trim('"'), executable), "PATH");
        if (OperatingSystem.IsWindows())
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") };
            foreach (var root in roots.Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                var foundation = Path.Combine(root, "Blender Foundation");
                foreach (var directory in InstallDirectories(foundation))
                    yield return new(Path.Combine(directory, executable), "standard-install");
                yield return new(Path.Combine(root, "Blender", executable), "standard-install");
            }
            foreach (var location in RegistryCandidates()) yield return location;
        }
        else
        {
            foreach (var path in new[] { "/Applications/Blender.app/Contents/MacOS/Blender",
                         "/usr/bin/blender", "/usr/local/bin/blender", "/snap/bin/blender" })
                yield return new(path, "standard-install");
        }
    }

    private static string[] InstallDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root, "Blender*")
                .OrderByDescending(x => Version.TryParse(Path.GetFileName(x).Replace("Blender", "").Trim(), out var v) ? v : new Version())
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<BlenderLocation> RegistryCandidates()
    {
        var found = new List<BlenderLocation>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var app = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\blender.exe");
                if (app?.GetValue(null) is string path) found.Add(new(path.Trim('"'), "registry-app-path"));
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || !display.StartsWith("Blender", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.GetValue("InstallLocation") is string install && !string.IsNullOrWhiteSpace(install))
                        found.Add(new(Path.Combine(install.Trim('"'), "blender.exe"), "registry-install-location"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return found;
    }
}

public static class BlenderProcess
{
    public static IReadOnlyList<string> ScriptArguments(string script, string? blendFile = null, IEnumerable<string>? arguments = null)
    {
        var result = new List<string> { "--background", "--factory-startup", "--disable-autoexec" };
        if (blendFile is not null) result.Add(Path.GetFullPath(blendFile));
        result.AddRange(["--python-exit-code", "1", "--python", Path.GetFullPath(script), "--"]);
        result.AddRange(arguments ?? []);
        return result;
    }

    public static async Task<BlenderProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        { throw new RLoopException("BLENDER_START_FAILED", ex.Message, ExitCodes.OperationFailed, innerException: ex); }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        var result = new BlenderProcessResult(executable, process.ExitCode, await stdout, await stderr);
        if (result.ExitCode != 0)
            throw new RLoopException("BLENDER_FAILED", $"Blender exited with code {result.ExitCode}.", ExitCodes.OperationFailed,
                new Dictionary<string, object?> { ["result"] = result }, ["Inspect the Python error; failed exports must not be imported."]);
        return result;
    }
}
