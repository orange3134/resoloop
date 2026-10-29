using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

internal sealed record PluginGitExport(PluginSource Source, string Commit, IReadOnlyList<string> Files);

/// <summary>Git discovery and raw-object export. Never checks out or runs package code.</summary>
internal static class PluginGit
{
    private const string Invalid = "PLUGIN_SOURCE_INVALID";
    private const string Failed = "PLUGIN_GIT_FAILED";
    private const int MaximumFiles = 10000;
    private const long MaximumBytes = 200L * 1024 * 1024;
    private const int MaximumListingBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex ObjectId = new(@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AuthenticationEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "GIT_SSH", "GIT_SSH_COMMAND", "GIT_ASKPASS", "GIT_SSL_CAINFO", "GIT_SSL_CAPATH", "GIT_PROXY_COMMAND"
    };

    public static IReadOnlyList<string> GetLocalFiles(string sourceDirectory) => Sanitize(() =>
    {
        var root = PluginPathPolicy.ResolveDirectory(sourceDirectory, Invalid);
        using var git = new GitProcess();
        var discovery = git.Read(root, ["rev-parse", "--show-toplevel"], 32768);
        if (discovery.ExitCode != 0)
        {
            // Exit 128 alone is NOT proof that copying everything is safe. Inspect every ancestor
            // (including inaccessible/dangling markers) and verify Git is operational independently.
            if (HasRepositoryAncestor(root)) throw Error(Invalid, "Could not verify the source repository.");
            var health = git.Read(root, ["rev-parse", "--local-env-vars"], 32768);
            if (discovery.ExitCode != 128 || health.ExitCode != 0 || health.Text.Length == 0)
                throw Error(Failed, "Git source discovery failed.");
            return PluginPathPolicy.EnumerateFiles(root, Invalid, rejectNestedGit: true);
        }
        var top = PluginPathPolicy.ResolveDirectory(discovery.Text.TrimEnd('\r', '\n'), Invalid);
        if (!SamePath(root, top)) throw Error(Invalid, "A local Git source must be the working tree root.");
        var bare = git.Read(root, ["rev-parse", "--is-bare-repository", "--is-inside-work-tree"], 128);
        if (bare.ExitCode != 0 || bare.Text.Replace("\r", "", StringComparison.Ordinal) != "false\ntrue\n")
            throw Error(Invalid, "A local Git source must be a working tree.");
        RejectNestedRepositories(root);
        var listing = git.Read(root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], MaximumListingBytes);
        if (listing.ExitCode != 0) throw Error(Failed, "Could not list the source repository.");
        var files = SplitNull(listing.Text).Distinct(StringComparer.Ordinal).ToArray();
        ValidatePaths(files);
        foreach (var file in files)
        {
            var full = PluginPathPolicy.Combine(root, file, Invalid);
            PluginPathPolicy.EnsureNoLinks(full, Invalid);
            if (!File.Exists(full) || Directory.Exists(full)) throw Error(Invalid, "Git source contains a missing or non-file entry.");
        }
        Array.Sort(files, StringComparer.Ordinal);
        return Array.AsReadOnly(files);
    });

    public static PluginGitExport Export(string url, string gitRef, string checkoutDirectory, string packageDirectory) => Sanitize(() =>
    {
        // Reject credentials before any operation that might include the input in a diagnostic.
        var source = ParseSource(url, gitRef);
        var checkout = RequireEmptyDirectory(checkoutDirectory);
        var package = RequireEmptyDirectory(packageDirectory);
        if (Overlaps(checkout, package)) throw Error(Invalid, "Git staging directories must be separate.");
        using var git = new GitProcess();
        var clone = git.Read(package, ["clone", "--bare", "--no-local", "--template=" + git.EmptyHooksDirectory, "--", url, checkout], 32768);
        if (clone.ExitCode != 0) throw Error(Failed, "Could not clone the plugin repository.");
        var commit = ResolveCommit(git, checkout, gitRef);
        var listing = git.Read(checkout, ["ls-tree", "-r", "-z", "-l", "--full-tree", commit], MaximumListingBytes);
        if (listing.ExitCode != 0) throw Error(Failed, "Could not read the plugin tree.");
        var blobs = ParseTree(listing.Text);
        ValidatePaths(blobs.Select(b => b.Path));
        long total = 0;
        foreach (var blob in blobs)
        {
            total = checked(total + blob.Size);
            if (total > MaximumBytes) throw Error(Invalid, "Plugin package exceeds the size limit.");
        }
        // Validate the COMPLETE tree and budget before opening even the first package file.
        foreach (var blob in blobs)
        {
            var destination = PluginPathPolicy.Combine(package, blob.Path, Invalid);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            PluginPathPolicy.EnsureNoLinks(destination, Invalid, allowMissing: true);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var result = git.Stream(checkout, ["cat-file", "blob", blob.Id], output, blob.Size);
                if (result.ExitCode != 0 || output.Length != blob.Size) throw Error(Failed, "Could not export the plugin blob.");
            }
            if (!OperatingSystem.IsWindows())
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                if (blob.Executable) mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(destination, mode);
            }
        }
        return new PluginGitExport(source, commit, Array.AsReadOnly(blobs.Select(b => b.Path).Order(StringComparer.Ordinal).ToArray()));
    });

    /// <summary>Validate connection input separately from credential-free persisted metadata.</summary>
    internal static PluginSource ParseSource(string url, string gitRef) => Sanitize(() =>
    {
        if (string.IsNullOrWhiteSpace(gitRef)) throw Error("PLUGIN_GIT_REF_REQUIRED", "A Git ref is required.");
        if (gitRef.StartsWith('-') || gitRef.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c))
            || gitRef.Contains("..", StringComparison.Ordinal) || gitRef.Contains("@{", StringComparison.Ordinal)
            || gitRef.Contains("//", StringComparison.Ordinal) || gitRef.EndsWith('/') || gitRef.EndsWith('.')
            || gitRef.EndsWith(".lock", StringComparison.Ordinal))
            throw Error(Invalid, "Invalid Git ref.");
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl) || url.Contains('?') || url.Contains('#') || url.StartsWith('-'))
            throw Error(Invalid, "Invalid Git source URL.");
        string location;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ssh" or "git" or "file"))
                throw Error(Invalid, "Unsupported Git source URL.");
            if (uri.UserInfo.Length != 0 && (uri.Scheme != "ssh" || Uri.UnescapeDataString(uri.UserInfo).Contains(':')))
                throw Error(Invalid, "Credentials must not be included in a Git source URL.");
            // URI escaping must not allow forbidden credentials/query/fragment to reappear in saved metadata.
            if (uri.Scheme != "file" && uri.Host.Length == 0) throw Error(Invalid, "Invalid Git host.");
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            location = builder.Uri.AbsoluteUri;
        }
        else
        {
            // scp-style [public-user@]host:path. No password, option, or external helper syntax.
            var match = Regex.Match(url, @"\A(?:(?<user>[A-Za-z0-9._-]+)@)?(?<host>[A-Za-z0-9][A-Za-z0-9._-]*):(?<path>[^\s:@?#\\]+)\z", RegexOptions.CultureInvariant);
            if (!match.Success) throw Error(Invalid, "Invalid Git source URL.");
            location = match.Groups["host"].Value + ":" + match.Groups["path"].Value;
        }
        if (location.Contains('@')) throw Error(Invalid, "Invalid Git source location.");
        return new PluginSource("git", location, gitRef);
    });

    private static string ResolveCommit(GitProcess git, string checkout, string gitRef)
    {
        var candidates = gitRef == "HEAD" || gitRef.StartsWith("refs/", StringComparison.Ordinal) || ObjectId.IsMatch(gitRef)
            ? new[] { gitRef }
            : new[] { "refs/heads/" + gitRef, "refs/remotes/origin/" + gitRef, "refs/tags/" + gitRef, gitRef };
        var commits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var result = git.Read(checkout, ["rev-parse", "--verify", "--end-of-options", candidate + "^{commit}"], 256);
            if (result.ExitCode != 0) continue;
            var id = result.Text.TrimEnd('\r', '\n');
            if (!ObjectId.IsMatch(id)) throw Error(Failed, "Git returned an invalid commit ID.");
            commits.Add(id);
        }
        if (commits.Count == 0) throw Error("PLUGIN_GIT_REF_NOT_FOUND", "The requested Git ref was not found.");
        if (commits.Count != 1) throw Error(Invalid, "The requested Git ref is ambiguous; use a full ref name.");
        return commits.Single();
    }

    private sealed record Blob(string Path, string Id, long Size, bool Executable);

    private static IReadOnlyList<Blob> ParseTree(string text)
    {
        var blobs = new List<Blob>();
        foreach (var entry in SplitNull(text))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) throw Error(Failed, "Git returned an invalid tree entry.");
            var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4 || fields[0] is not ("100644" or "100755") || fields[1] != "blob"
                || !ObjectId.IsMatch(fields[2]) || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw Error(Invalid, "Only regular Git blobs are allowed in a plugin package.");
            blobs.Add(new Blob(entry[(tab + 1)..], fields[2], size, fields[0] == "100755"));
        }
        return blobs;
    }

    private static string[] SplitNull(string text)
    {
        if (text.Length == 0) return [];
        if (text[^1] != '\0') throw Error(Failed, "Git returned an incomplete file listing.");
        return text[..^1].Split('\0');
    }

    private static void ValidatePaths(IEnumerable<string> files)
    {
        var paths = new Dictionary<string, (string Spelling, bool File)>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var file in files)
        {
            if (++count > MaximumFiles) throw Error(Invalid, "Plugin package exceeds the file count limit.");
            PluginPathPolicy.ValidateRelativePath(file, Invalid);
            var parts = file.Split('/');
            var prefix = "";
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Equals(".git", StringComparison.OrdinalIgnoreCase)) throw Error(Invalid, "Repository metadata is not package content.");
                prefix = i == 0 ? parts[i] : prefix + "/" + parts[i];
                var isFile = i == parts.Length - 1;
                if (paths.TryGetValue(prefix, out var previous))
                {
                    if (previous.Spelling != prefix || previous.File || isFile) throw Error(Invalid, "Git package paths collide.");
                }
                else paths.Add(prefix, (prefix, isFile));
            }
        }
    }

    private static bool HasRepositoryAncestor(string root)
    {
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            if (EntryExists(Path.Combine(current.FullName, ".git"))) return true;
            if (EntryExists(Path.Combine(current.FullName, "HEAD")) && EntryExists(Path.Combine(current.FullName, "objects"))) return true;
        }
        return false;
    }

    private static bool EntryExists(string path)
    {
        // File.Exists returns false for access failures and dangling links, which is unsafe for discovery.
        if (new FileInfo(path).LinkTarget is not null) return true;
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RejectNestedRepositories(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    if (SamePath(directory, root) && Path.GetFileName(entry) == ".git") continue;
                    throw Error(Invalid, "Nested repositories are not allowed in a plugin package.");
                }
                // Never follow child links, including ignored ones.
                if (new FileInfo(entry).LinkTarget is null && Directory.Exists(entry)) stack.Push(entry);
            }
        }
    }

    private static string RequireEmptyDirectory(string path)
    {
        PluginPathPolicy.EnsureNoLinks(path, Invalid);
        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
            throw Error(Invalid, "Git export requires empty staging directories.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Overlaps(string a, string b) => SamePath(a, b)
        || a.StartsWith(b + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || b.StartsWith(a + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static RLoopException Error(string code, string message) => PluginErrors.Create(code, message);
    private static T Sanitize<T>(Func<T> action)
    {
        try { return action(); }
        // Do not preserve raw Git stderr, input URLs, exception messages, or inner exceptions.
        catch (RLoopException error) { throw Error(error.Code, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException
            or InvalidOperationException or NotSupportedException or OverflowException)
        {
            throw Error(Failed, "Plugin Git operation failed.");
        }
    }

    private sealed record GitResult(int ExitCode, string Text);

    private sealed class GitProcess : IDisposable
    {
        public string EmptyHooksDirectory { get; } = Directory.CreateTempSubdirectory("resoloop-plugin-git-").FullName;

        public GitResult Read(string directory, string[] arguments, int maximumBytes)
        {
            using var output = new MemoryStream();
            var result = Stream(directory, arguments, output, maximumBytes);
            return result with { Text = Utf8.GetString(output.ToArray()) };
        }

        public GitResult Stream(string directory, string[] arguments, Stream output, long maximumBytes) =>
            RunAsync(directory, arguments, output, maximumBytes).GetAwaiter().GetResult();

        private async Task<GitResult> RunAsync(string directory, string[] arguments, Stream output, long maximumBytes)
        {
            using var process = new Process { StartInfo = new("git")
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            } };
            foreach (var key in process.StartInfo.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                if (!AuthenticationEnvironment.Contains(key)) process.StartInfo.Environment.Remove(key);
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            process.StartInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            foreach (var option in new[] { "core.fsmonitor=false", "core.hooksPath=" + EmptyHooksDirectory,
                "protocol.allow=never", "protocol.ext.allow=never", "protocol.file.allow=always", "protocol.http.allow=always",
                "protocol.https.allow=always", "protocol.ssh.allow=always", "protocol.git.allow=always", "credential.interactive=false" })
            {
                process.StartInfo.ArgumentList.Add("-c");
                process.StartInfo.ArgumentList.Add(option);
            }
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw Error(Failed, "Could not start Git.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var stdout = CopyOutput();
            var stderr = process.StandardError.BaseStream.CopyToAsync(System.IO.Stream.Null, deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            try
            {
                await Task.WhenAll(stdout, stderr, exited).WaitAsync(deadline.Token).ConfigureAwait(false);
                return new GitResult(process.ExitCode, "");
            }
            catch
            {
                // A timeout/output limit is never success, even if the process raced to exit zero.
                // Stop descendants as well as the parent, then confirm parent termination and drain completion.
                deadline.Cancel();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
                throw Error(Failed, "Git failed, exceeded an output limit, or timed out.");
            }

            async Task CopyOutput()
            {
                try
                {
                    var buffer = new byte[81920];
                    long count = 0;
                    int read;
                    while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                    {
                        count = checked(count + read);
                        if (count > maximumBytes) throw Error(Failed, "Git output exceeds the limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    }
                }
                catch { deadline.Cancel(); throw; }
            }
        }

        public void Dispose() => Directory.Delete(EmptyHooksDirectory);
    }
}
