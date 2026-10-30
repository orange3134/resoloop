using System.Text;

namespace RLoop.Core.Plugins;

/// <summary>Outcome: available, missing, failed or timed-out.</summary>
public sealed record PluginToolProbeResult(string Plugin, PluginTool Tool, string Outcome, string? ExecutablePath,
    int? ExitCode, string? Version);

/// <summary>Checks a plugin's declared external tool for doctor. Never installs or repairs anything.</summary>
public static class PluginToolProbe
{
    private const int MaximumOutputBytes = 64 * 1024;
    private const int MaximumVersionLength = 200;

    public static async Task<PluginToolProbeResult> ProbeAsync(string plugin, PluginTool tool, string workingDirectory,
        IReadOnlyDictionary<string, string?> environment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(environment);
        // Same resolution as plugin commands: absolute PATH entries only, .exe only on Windows.
        var path = PluginExecutable.Find(tool.Executable, environment);
        if (path is null) return new(plugin, tool, "missing", null, null, null);
        if (tool.VersionArgs.Count == 0) return new(plugin, tool, "available", path, null, null);
        var context = new Dictionary<string, object?> { ["plugin"] = plugin, ["tool"] = tool.Name, ["program"] = tool.Executable };
        var child = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in environment) if (value is not null) child[key] = value;
        PluginProcess process;
        try { process = PluginProcess.Start(path, tool.VersionArgs, workingDirectory, child, captureOutput: true, context); }
        catch (RLoopException) { return new(plugin, tool, "failed", path, null, null); }
        using (process)
        {
            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            var unreadable = false; // output limit or unreadable output: never a pass
            var stdout = Read(process.Output!);
            var stderr = Read(process.ErrorOutput!);
            var interrupted = false;
            try { await Task.WhenAll(stdout, stderr, process.Exited).WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { interrupted = true; }
            catch (IOException) { unreadable = true; }
            int exitCode;
            try { exitCode = await process.StopAsync().ConfigureAwait(false); }
            catch (RLoopException) { return new(plugin, tool, "failed", path, null, null); }
            finally
            {
                // The stopped tree has closed the pipe writers; observe the readers before the pipes are disposed.
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (unreadable) return new(plugin, tool, "failed", path, null, null);
            if (interrupted) return new(plugin, tool, "timed-out", path, null, null);
            if (exitCode != 0) return new(plugin, tool, "failed", path, exitCode, null);
            return new(plugin, tool, "available", path, exitCode, FirstLine(await stdout.ConfigureAwait(false)) ?? FirstLine(await stderr.ConfigureAwait(false)));

            async Task<byte[]> Read(Stream stream)
            {
                using var buffer = new MemoryStream();
                var block = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(block, linked.Token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + count > MaximumOutputBytes)
                    {
                        // Do not keep reading an unbounded stream; the tree is stopped next.
                        unreadable = true;
                        deadline.Cancel();
                        return [];
                    }
                    buffer.Write(block, 0, count);
                }
                return buffer.ToArray();
            }
        }
    }

    private static string? FirstLine(byte[] output)
    {
        foreach (var line in Encoding.UTF8.GetString(output).Split('\n'))
        {
            var text = new string(line.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (text.Length != 0) return text.Length > MaximumVersionLength ? text[..MaximumVersionLength] : text;
        }
        return null;
    }
}
