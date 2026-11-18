using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        var listing = git.ReadBare(checkout, ["ls-tree", "-r", "-z", "-l", "--full-tree", commit], MaximumListingBytes);
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
                var result = git.StreamBare(checkout, ["cat-file", "blob", blob.Id], output, blob.Size);
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
            var result = git.ReadBare(checkout, ["rev-parse", "--verify", "--end-of-options", candidate + "^{commit}"], 256);
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
            or InvalidOperationException or NotSupportedException or OverflowException or TimeoutException
            or DllNotFoundException or EntryPointNotFoundException)
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

        // Name a bare clone explicitly: cwd discovery is subject to the user's safe.bareRepository.
        public GitResult ReadBare(string repository, string[] arguments, int maximumBytes) =>
            Read(repository, ["--git-dir=" + repository, .. arguments], maximumBytes);

        public GitResult StreamBare(string repository, string[] arguments, Stream output, long maximumBytes) =>
            Stream(repository, ["--git-dir=" + repository, .. arguments], output, maximumBytes);

        private async Task<GitResult> RunAsync(string directory, string[] arguments, Stream output, long maximumBytes)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                if (!AuthenticationEnvironment.Contains(key)) start.Environment.Remove(key);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            foreach (var option in new[] { "core.fsmonitor=false", "core.hooksPath=" + EmptyHooksDirectory,
                "protocol.allow=never", "protocol.ext.allow=never", "protocol.file.allow=always", "protocol.http.allow=always",
                "protocol.https.allow=always", "protocol.ssh.allow=always", "protocol.git.allow=always", "credential.interactive=false" })
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(option);
            }
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var process = ContainedProcess.Start(start);
            var stdout = CopyOutput();
            var stderr = process.ErrorOutput.CopyToAsync(System.IO.Stream.Null, deadline.Token);
            var exited = process.Exited;
            try
            {
                await Task.WhenAll(stdout, stderr, exited).WaitAsync(deadline.Token).ConfigureAwait(false);
                return new GitResult(await process.CompleteAsync().ConfigureAwait(false), "");
            }
            catch
            {
                // Stop the launch-time group/job even if the original Git parent has exited.
                deadline.Cancel();
                try { await process.CompleteAsync().ConfigureAwait(false); }
                finally
                {
                    try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch { }
                }
                throw Error(Failed, "Git failed, exceeded an output limit, or timed out.");
            }

            async Task CopyOutput()
            {
                try
                {
                    var buffer = new byte[81920];
                    long count = 0;
                    int read;
                    while ((read = await process.Output.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
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

    // A command owns a kernel containment boundary, not a snapshot of descendant PIDs.
    // Keep that boundary alive until completion even when the Git parent has already exited.
    private abstract class ContainedProcess : IDisposable
    {
        public AnonymousPipeServerStream Output { get; } = new(PipeDirection.In,
            OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None);
        public AnonymousPipeServerStream ErrorOutput { get; } = new(PipeDirection.In,
            OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None);
        public Task Exited { get; protected set; } = Task.CompletedTask;
        private Task<int>? _completion;

        public static ContainedProcess Start(ProcessStartInfo start)
        {
            if (start.UseShellExecute || start.Arguments.Length != 0)
                throw Error(Failed, "Git requires a shell-less argument vector.");
            ContainedProcess process = OperatingSystem.IsLinux() ? new LinuxProcess()
                : OperatingSystem.IsWindows() ? new WindowsProcess()
                : throw Error(Failed, "Contained Git execution is unavailable on this platform.");
            try
            {
                process.Launch(start);
                process.Output.DisposeLocalCopyOfClientHandle();
                process.ErrorOutput.DisposeLocalCopyOfClientHandle();
                return process;
            }
            catch { process.Dispose(); throw; }
        }

        protected abstract void Launch(ProcessStartInfo start);
        protected abstract Task<int> CompleteCoreAsync();
        public Task<int> CompleteAsync() => _completion ??= CompleteCoreAsync();
        protected virtual void CloseNativeHandles() { }

        public void Dispose()
        {
            try { CompleteAsync().GetAwaiter().GetResult(); }
            finally { Output.Dispose(); ErrorOutput.Dispose(); CloseNativeHandles(); }
        }
    }

    private sealed class LinuxProcess : ContainedProcess
    {
        private int _pid;

        protected override void Launch(ProcessStartInfo start)
        {
            var actions = default(UnixNative.SpawnActions);
            var attributes = default(UnixNative.SpawnAttributes);
            UnixNative.Check(UnixNative.posix_spawn_file_actions_init(ref actions));
            try
            {
                UnixNative.Check(UnixNative.posix_spawnattr_init(ref attributes));
                try
                {
                    UnixNative.Check(UnixNative.posix_spawnattr_setflags(ref attributes, UnixNative.SpawnSetProcessGroup));
                    UnixNative.Check(UnixNative.posix_spawnattr_setpgroup(ref attributes, 0));
                    UnixNative.Check(UnixNative.posix_spawn_file_actions_addchdir_np(ref actions, start.WorkingDirectory));
                    var outputFd = checked((int)Output.ClientSafePipeHandle.DangerousGetHandle());
                    var errorFd = checked((int)ErrorOutput.ClientSafePipeHandle.DangerousGetHandle());
                    // Do not let a host with closed standard descriptors alias our source pipes.
                    if (outputFd <= 2 || errorFd <= 2) throw Error(Failed, "Git pipe descriptors are unavailable.");
                    UnixNative.Check(UnixNative.posix_spawn_file_actions_adddup2(ref actions, outputFd, 1));
                    UnixNative.Check(UnixNative.posix_spawn_file_actions_adddup2(ref actions, errorFd, 2));
                    UnixNative.Check(UnixNative.posix_spawn_file_actions_addclosefrom_np(ref actions, 3));
                    using var argv = new Utf8Vector(new[] { start.FileName }.Concat(start.ArgumentList));
                    using var envp = new Utf8Vector(start.Environment.Where(e => e.Value is not null).Select(e => e.Key + "=" + e.Value));
                    UnixNative.Check(UnixNative.posix_spawnp(out var pid, ResolveExecutable(start), ref actions, ref attributes, argv.Pointer, envp.Pointer));
                    _pid = pid;
                    // WNOWAIT deliberately pins the leader PID/PGID until group cleanup is confirmed.
                    // Reaping it sooner would permit PID reuse before a later group signal.
                    Exited = Task.Run(() =>
                    {
                        int result;
                        do { result = UnixNative.waitid(1, (uint)_pid, out _, UnixNative.WaitExited | UnixNative.WaitNoReap); }
                        while (result != 0 && Marshal.GetLastPInvokeError() == UnixNative.Interrupted);
                        if (result != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    });
                }
                finally { UnixNative.Check(UnixNative.posix_spawnattr_destroy(ref attributes)); }
            }
            finally { UnixNative.Check(UnixNative.posix_spawn_file_actions_destroy(ref actions)); }
        }

        private static string ResolveExecutable(ProcessStartInfo start)
        {
            // Resolve relative PATH entries in the caller, BEFORE the child-only chdir action.
            // Otherwise a package can supply its own executable at a matching relative PATH.
            if (start.FileName.Contains('/')) return Path.GetFullPath(start.FileName);
            start.Environment.TryGetValue("PATH", out var path);
            foreach (var directory in (path ?? "/bin:/usr/bin").Split(Path.PathSeparator))
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, start.FileName));
                if (File.Exists(candidate) && UnixNative.euidaccess(candidate, 1) == 0) return candidate;
            }
            throw Error(Failed, "Git executable was not found.");
        }

        protected override async Task<int> CompleteCoreAsync()
        {
            if (_pid == 0) return -1;
            if (_pid <= 1) throw Error(Failed, "Invalid Git process group.");
            var stopped = Stopwatch.StartNew();
            // The group remains identifiable after the original parent exits; no per-PID discovery/signaling.
            if (UnixNative.kill(-_pid, UnixNative.KillSignal) != 0 && Marshal.GetLastPInvokeError() != UnixNative.NoSuchProcess)
                throw Error(Failed, "Could not stop the Git process group.");
            await Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            while (GroupHasExecutingMembers())
            {
                if (stopped.Elapsed >= TimeSpan.FromSeconds(5)) throw Error(Failed, "Git process group termination was not confirmed.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            int result, status;
            do { result = UnixNative.waitpid(_pid, out status, 0); }
            while (result < 0 && Marshal.GetLastPInvokeError() == UnixNative.Interrupted);
            if (result != _pid) throw Error(Failed, "Could not reap the Git process.");
            _pid = 0;
            return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
        }

        private bool GroupHasExecutingMembers()
        {
            // This verifies kernel PGID membership AFTER group termination, not a heuristic for
            // discovering ownership. Signals target only the pinned group, never enumerated PIDs.
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                string stat;
                try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                // ESRCH: the process was reaped while its stat entry was being read.
                catch (IOException error) when ((error.HResult & 0xffff) == UnixNative.NoSuchProcess) { continue; }
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (fields.Length < 3) throw Error(Failed, "Could not verify Git process group termination.");
                // Dead entries are not executing; a reaping (X) entry reports its pgrp as -1.
                if (fields[0] is "Z" or "X") continue;
                if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var group))
                    throw Error(Failed, "Could not verify Git process group termination.");
                if (group == _pid) return true;
            }
            return false;
        }
    }

    private sealed class Utf8Vector : IDisposable
    {
        private readonly List<IntPtr> _strings = [];
        public IntPtr Pointer { get; private set; }
        public Utf8Vector(IEnumerable<string> values)
        {
            try
            {
                foreach (var value in values)
                {
                    if (value.Contains('\0')) throw Error(Failed, "Git argument/environment contains NUL.");
                    _strings.Add(Marshal.StringToCoTaskMemUTF8(value));
                }
                Pointer = Marshal.AllocHGlobal(checked((_strings.Count + 1) * IntPtr.Size));
                Marshal.Copy(_strings.Append(IntPtr.Zero).ToArray(), 0, Pointer, _strings.Count + 1);
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
            foreach (var value in _strings) Marshal.FreeCoTaskMem(value);
            _strings.Clear();
        }
    }

    private static class UnixNative
    {
        public const short SpawnSetProcessGroup = 0x02;
        public const int WaitExited = 4, WaitNoReap = 0x01000000, Interrupted = 4, NoSuchProcess = 3, KillSignal = 9;
        // glibc spawn.h ABI, verified on the Linux target. Attribute fields are accessed only
        // through libc setters; GNU chdir/closefrom actions fail closed if unavailable.
        [StructLayout(LayoutKind.Explicit, Size = 336)]
        public struct SpawnAttributes { [FieldOffset(0)] public long Alignment; }
        [StructLayout(LayoutKind.Sequential)]
        public struct SpawnActions { public int Allocated, Used; public IntPtr Actions; public Padding Reserved; }
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        public struct Padding { public int Alignment; }
        [StructLayout(LayoutKind.Explicit, Size = 128)]
        public struct SignalInfo { [FieldOffset(0)] public long Alignment; }
        public static void Check(int error) { if (error != 0) throw new Win32Exception(error); }
        [DllImport("libc")] public static extern int posix_spawnattr_init(ref SpawnAttributes attributes);
        [DllImport("libc")] public static extern int posix_spawnattr_destroy(ref SpawnAttributes attributes);
        [DllImport("libc")] public static extern int posix_spawnattr_setflags(ref SpawnAttributes attributes, short flags);
        [DllImport("libc")] public static extern int posix_spawnattr_setpgroup(ref SpawnAttributes attributes, int group);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_init(ref SpawnActions actions);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_destroy(ref SpawnActions actions);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_adddup2(ref SpawnActions actions, int source, int destination);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addclosefrom_np(ref SpawnActions actions, int from);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addchdir_np(ref SpawnActions actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport("libc")] public static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
            ref SpawnActions actions, ref SpawnAttributes attributes, IntPtr arguments, IntPtr environment);
        [DllImport("libc")] public static extern int euidaccess([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
        [DllImport("libc", SetLastError = true)] public static extern int kill(int pid, int signal);
        [DllImport("libc", SetLastError = true)] public static extern int waitid(int type, uint id, out SignalInfo info, int options);
        [DllImport("libc", SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
    }

    private sealed class WindowsProcess : ContainedProcess
    {
        private SafeFileHandle? _job, _process;
        private Task<int> _exitCode = Task.FromResult(-1);
        private bool _assigned;

        protected override void Launch(ProcessStartInfo start)
        {
            _job = WindowsNative.Own(WindowsNative.CreateJobObjectW(IntPtr.Zero, null));
            var limits = new WindowsNative.ExtendedLimits { Basic = new() { Flags = WindowsNative.KillOnJobClose } };
            WindowsNative.Check(WindowsNative.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<WindowsNative.ExtendedLimits>()));
            var security = new WindowsNative.SecurityAttributes { Size = Marshal.SizeOf<WindowsNative.SecurityAttributes>(), Inherit = 1 };
            using var input = WindowsNative.DuplicateInput(ref security);
            using var attributes = new WindowsAttributes([input.DangerousGetHandle(), Output.ClientSafePipeHandle.DangerousGetHandle(), ErrorOutput.ClientSafePipeHandle.DangerousGetHandle()]);
            var startup = new WindowsNative.StartupInfoEx
            {
                Info = new() { Size = Marshal.SizeOf<WindowsNative.StartupInfoEx>(), Flags = 0x100,
                    Input = input.DangerousGetHandle(), Output = Output.ClientSafePipeHandle.DangerousGetHandle(), Error = ErrorOutput.ClientSafePipeHandle.DangerousGetHandle() },
                Attributes = attributes.Pointer
            };
            var executable = ResolveWindowsExecutable(start);
            var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(start.ArgumentList).Select(QuoteWindowsArgument)));
            var environment = string.Join('\0', start.Environment.Where(e => e.Value is not null).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Key + "=" + e.Value)) + "\0\0";
            var block = Marshal.StringToHGlobalUni(environment);
            WindowsNative.ProcessInformation child;
            try
            {
                // Suspended creation prevents even an immediate fork/exit before job assignment.
                WindowsNative.Check(WindowsNative.CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    WindowsNative.CreateSuspended | WindowsNative.UnicodeEnvironment | WindowsNative.ExtendedStartup | WindowsNative.NoWindow,
                    block, start.WorkingDirectory, ref startup, out child));
            }
            finally { Marshal.FreeHGlobal(block); }
            _process = WindowsNative.Own(child.Process);
            using var thread = WindowsNative.Own(child.Thread);
            WindowsNative.Check(WindowsNative.AssignProcessToJobObject(_job, _process));
            _assigned = true;
            if (WindowsNative.ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _exitCode = Task.Run(() =>
            {
                if (WindowsNative.WaitForSingleObject(_process, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                WindowsNative.Check(WindowsNative.GetExitCodeProcess(_process, out var code));
                return unchecked((int)code);
            });
            Exited = _exitCode;
        }

        protected override async Task<int> CompleteCoreAsync()
        {
            if (_process is null) return -1;
            // Job handles remain valid after the original Git process exits; no parent PID is needed.
            if (_assigned) WindowsNative.Check(WindowsNative.TerminateJobObject(_job!, 1));
            else WindowsNative.Check(WindowsNative.TerminateProcess(_process, 1)); // still suspended, never executed
            if (!_assigned)
            {
                if (WindowsNative.WaitForSingleObject(_process, 5000) != 0) throw Error(Failed, "Suspended Git termination was not confirmed.");
                return -1;
            }
            var stopped = Stopwatch.StartNew();
            await _exitCode.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            while (true)
            {
                WindowsNative.Check(WindowsNative.QueryInformationJobObject(_job!, 1, out var accounting, (uint)Marshal.SizeOf<WindowsNative.Accounting>(), IntPtr.Zero));
                if (accounting.ActiveProcesses == 0) break;
                if (stopped.Elapsed >= TimeSpan.FromSeconds(5)) throw Error(Failed, "Git job termination was not confirmed.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            return await _exitCode.ConfigureAwait(false);
        }

        protected override void CloseNativeHandles() { _process?.Dispose(); _job?.Dispose(); }

        private static string ResolveWindowsExecutable(ProcessStartInfo start)
        {
            if (Path.IsPathFullyQualified(start.FileName)) return start.FileName;
            start.Environment.TryGetValue("PATH", out var path);
            foreach (var directory in (path ?? "").Split(Path.PathSeparator))
            {
                if (directory.Length == 0) continue;
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), start.FileName + ".exe"));
                if (File.Exists(candidate)) return candidate;
            }
            throw Error(Failed, "Git executable was not found.");
        }

        // CreateProcess receives a mutable command line; quote each ArgumentList item using the
        // Windows CRT backslash/quote rules, not shell escaping. The application path is explicit.
        private static string QuoteWindowsArgument(string value)
        {
            if (value.Contains('\0')) throw Error(Failed, "Git argument contains NUL.");
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }

    private sealed class WindowsAttributes : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private IntPtr _handles;
        private bool _initialized;
        public WindowsAttributes(IntPtr[] handles)
        {
            try
            {
                nuint size = 0;
                WindowsNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                Pointer = Marshal.AllocHGlobal(checked((nint)size));
                WindowsNative.Check(WindowsNative.InitializeProcThreadAttributeList(Pointer, 1, 0, ref size));
                _initialized = true;
                _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                Marshal.Copy(handles, 0, _handles, handles.Length);
                WindowsNative.Check(WindowsNative.UpdateProcThreadAttribute(Pointer, 0, 0x00020002, _handles, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) { WindowsNative.DeleteProcThreadAttributeList(Pointer); _initialized = false; }
            if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
            if (_handles != IntPtr.Zero) { Marshal.FreeHGlobal(_handles); _handles = IntPtr.Zero; }
        }
    }

    private static class WindowsNative
    {
        public const uint CreateSuspended = 4, UnicodeEnvironment = 0x400, ExtendedStartup = 0x80000, NoWindow = 0x08000000, KillOnJobClose = 0x2000;
        [StructLayout(LayoutKind.Sequential)]
        public struct SecurityAttributes { public int Size; public IntPtr Descriptor; public int Inherit; }
        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfo
        {
            public int Size; public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            public ushort Show, ReservedBytes; public IntPtr ReservedData, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BasicLimits
        {
            public long ProcessTime, JobTime; public uint Flags;
            public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveLimit;
            public nuint Affinity; public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        public struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)]
        public struct Accounting { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        public static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        public static SafeFileHandle Own(IntPtr handle)
        {
            var owned = new SafeFileHandle(handle, ownsHandle: true);
            if (owned.IsInvalid) { owned.Dispose(); throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            return owned;
        }
        public static SafeFileHandle DuplicateInput(ref SecurityAttributes security)
        {
            var handle = GetStdHandle(-10);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return Own(CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero));
            Check(DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var duplicate, 0, true, 2));
            return Own(duplicate);
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out Accounting accounting, uint length, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int standardHandle);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateFileW(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    }
}
