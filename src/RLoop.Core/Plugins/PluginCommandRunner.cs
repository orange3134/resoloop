using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RLoop.Core.Plugins;

/// <summary>One plugin command call. The caller holds the lease that produced <see cref="Plugin"/>.</summary>
public sealed record PluginCommandRequest(InstalledPlugin Plugin, PluginCommand Command, string ProjectRoot,
    IReadOnlyList<string> Arguments, IReadOnlyList<string> HostArgv, string HostVersion, TimeSpan Timeout,
    IReadOnlyDictionary<string, string?> BaseEnvironment);

/// <summary>Runs a plugin command without a shell, in a process tree that is always stopped before returning.</summary>
public static class PluginCommandRunner
{
    internal const string StartFailed = "PLUGIN_COMMAND_START_FAILED";
    internal const string StopFailed = "PLUGIN_COMMAND_STOP_FAILED";

    /// <summary>
    /// Returns the command's exit code. Keep the PluginReadLease that produced request.Plugin until this returns:
    /// the process group/job, including leftover descendants, has stopped by then.
    /// </summary>
    public static async Task<int> RunAsync(PluginCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = Context(request);
        if (request.HostArgv.Count == 0)
            throw Error(StartFailed, "Could not determine how to call ResoLoop back.", context, ExitCodes.ExternalToolFailed);
        var executable = PluginExecutable.ResolveProgram(request.Plugin, request.Command, request.BaseEnvironment, context);
        PrepareDataDirectory(request.Plugin.DataDirectory);
        var arguments = PluginRuntimeEnvironment.ExpandArguments(request.Plugin, request.Command, request.Arguments);
        var environment = PluginRuntimeEnvironment.Create(request);
        if (cancellationToken.IsCancellationRequested) throw Cancelled(context);
        using var deadline = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        using var process = PluginProcess.Start(executable, arguments, request.ProjectRoot, environment, captureOutput: false, context);
        var interrupted = false;
        try { await process.Exited.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { interrupted = true; }
        // Stop leftover descendants too: the lease is released right after this returns.
        var exitCode = await process.StopAsync().ConfigureAwait(false);
        // Ctrl+C also reaches a Windows child, which may then exit 0 on its own: never report that as success.
        if (cancellationToken.IsCancellationRequested) throw Cancelled(context);
        if (interrupted)
            throw Error("PLUGIN_COMMAND_TIMEOUT", "The plugin command exceeded its deadline and was stopped.",
                new Dictionary<string, object?>(context) { ["timeoutSeconds"] = request.Timeout.TotalSeconds }, ExitCodes.Timeout,
                ["Increase timeoutSeconds in the plugin manifest or commandTimeoutSeconds after checking the command's progress."]);
        return exitCode;
    }

    private static void PrepareDataDirectory(string data)
    {
        PluginPathPolicy.EnsureNoLinks(data, "PLUGIN_PATH_UNSAFE", allowMissing: true);
        if (File.Exists(data)) throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Plugin data path must be a directory.");
        try { Directory.CreateDirectory(data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Could not create the plugin data directory.", innerException: ex);
        }
        PluginPathPolicy.EnsureNoLinks(data, "PLUGIN_PATH_UNSAFE");
    }

    private static Dictionary<string, object?> Context(PluginCommandRequest request) => new()
    {
        ["plugin"] = request.Plugin.Name, ["command"] = request.Command.Name, ["program"] = request.Command.Program
    };

    private static RLoopException Cancelled(IReadOnlyDictionary<string, object?> context) =>
        Error("PLUGIN_COMMAND_CANCELLED", "The plugin command was cancelled and stopped.", context, ExitCodes.OperationFailed);

    internal static RLoopException Error(string code, string message, IReadOnlyDictionary<string, object?>? context, int exitCode,
        IReadOnlyList<string>? suggestions = null, Exception? innerException = null) =>
        new(code, message, exitCode, context, suggestions, innerException);
}

/// <summary>Resolves programs to absolute paths in the host, never through a shell or the child's cwd.</summary>
internal static class PluginExecutable
{
    /// <summary>A separator-free name from absolute PATH entries only. Windows launches .exe files only.</summary>
    public static string? Find(string name, IReadOnlyDictionary<string, string?> environment)
    {
        if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0) return null;
        var fileName = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        foreach (var entry in (Variable(environment, "PATH") ?? "").Split(Path.PathSeparator))
        {
            // Relative or empty entries would name the host cwd or the project (the child's cwd).
            var directory = OperatingSystem.IsWindows() ? entry.Trim('"') : entry;
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
            if (IsLaunchable(candidate)) return candidate;
        }
        return null;
    }

    public static string ResolveProgram(InstalledPlugin plugin, PluginCommand command, IReadOnlyDictionary<string, string?> environment,
        IReadOnlyDictionary<string, object?> context)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw NotStarted("Plugin commands run only on Linux and Windows.", context);
        if (!command.Program.StartsWith("./", StringComparison.Ordinal))
            return Find(command.Program, environment)
                ?? throw NotStarted($"'{command.Program}' was not found in an absolute PATH entry.", context,
                    OperatingSystem.IsWindows() ? "Install the program as an .exe on PATH; .cmd/.bat files are not launched." : null);
        // Package-relative programs are used as written: install verification checked exactly this file.
        var path = PluginPathPolicy.Combine(plugin.PackageDirectory, command.Program[2..], "PLUGIN_MODIFIED");
        if (!File.Exists(path)) throw NotStarted("The package program is not a regular file.", context);
        if (OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw NotStarted("Only .exe package programs can be launched on Windows.", context,
                "Call an interpreter on PATH with ${pluginRoot} for scripts that should run on every OS.");
        if (!IsLaunchable(path)) throw NotStarted("The package program is not executable.", context,
            "Keep the executable bit (mode 100755) in the plugin source.");
        return path;
    }

    private static bool IsLaunchable(string path) => File.Exists(path) && (OperatingSystem.IsWindows()
        ? path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        : PluginProcess.UnixNative.euidaccess(path, 1) == 0);

    private static string? Variable(IReadOnlyDictionary<string, string?> environment, string name) =>
        OperatingSystem.IsWindows()
            ? environment.FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
            : environment.GetValueOrDefault(name);

    private static RLoopException NotStarted(string message, IReadOnlyDictionary<string, object?> context, string? suggestion = null) =>
        PluginCommandRunner.Error(PluginCommandRunner.StartFailed, message, context, ExitCodes.ExternalToolFailed,
            suggestion is null ? null : [suggestion]);
}

// A command owns a kernel containment boundary (Linux process group / Windows job), not a snapshot of
// descendant PIDs. The boundary stays identifiable until StopAsync confirms that nothing in it executes.
// Same approach as PluginGit's private launcher; that file is owned by the plugin core (create-reso-world#297).
internal abstract class PluginProcess : IDisposable
{
    private static readonly TimeSpan StopConfirmation = TimeSpan.FromSeconds(5);
    private Task<int>? _completion;
    protected IReadOnlyDictionary<string, object?> Context { get; private set; } = new Dictionary<string, object?>();

    public AnonymousPipeServerStream? Output { get; private set; }
    public AnonymousPipeServerStream? ErrorOutput { get; private set; }
    public Task Exited { get; protected set; } = Task.CompletedTask;

    /// <summary>
    /// captureOutput=false inherits the host stdout/stderr unchanged; stdin is inherited only when redirected
    /// (a terminal becomes empty input). captureOutput=true reads stdout/stderr through pipes with empty stdin.
    /// </summary>
    public static PluginProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, bool captureOutput, IReadOnlyDictionary<string, object?> context)
    {
        PluginProcess process = OperatingSystem.IsLinux() ? new LinuxProcess()
            : OperatingSystem.IsWindows() ? new WindowsProcess()
            : throw PluginCommandRunner.Error(PluginCommandRunner.StartFailed, "Plugin commands run only on Linux and Windows.",
                context, ExitCodes.ExternalToolFailed);
        process.Context = context;
        try
        {
            if (captureOutput)
            {
                var inheritability = OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None;
                process.Output = new AnonymousPipeServerStream(PipeDirection.In, inheritability);
                process.ErrorOutput = new AnonymousPipeServerStream(PipeDirection.In, inheritability);
            }
            foreach (var value in arguments.Prepend(executable).Concat(environment.Keys).Concat(environment.Values))
                if (value.Contains('\0')) throw process.NotStarted("An argument or environment value contains NUL.", null);
            try { process.Launch(executable, arguments, workingDirectory, environment); }
            catch (Win32Exception ex) { throw process.NotStarted("The plugin program could not be started.", ex); }
            process.Output?.DisposeLocalCopyOfClientHandle();
            process.ErrorOutput?.DisposeLocalCopyOfClientHandle();
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    protected abstract void Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment);
    protected abstract Task<int> StopCoreAsync(TimeSpan confirmation);
    protected virtual void CloseNativeHandles() { }

    /// <summary>Stop the whole group/job (also after a normal exit), confirm it, and return the leader's exit code.</summary>
    public Task<int> StopAsync() => _completion ??= StopWithErrorAsync();

    private async Task<int> StopWithErrorAsync()
    {
        try { return await StopCoreAsync(StopConfirmation).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw StopError("Could not confirm that the plugin process tree stopped.", ex);
        }
    }

    protected RLoopException StopError(string message, Exception? innerException = null) =>
        PluginCommandRunner.Error(PluginCommandRunner.StopFailed, message, Context, ExitCodes.OperationFailed,
            ["Check for leftover plugin processes before retrying."], innerException);

    private RLoopException NotStarted(string message, Exception? innerException) =>
        PluginCommandRunner.Error(PluginCommandRunner.StartFailed, message, Context, ExitCodes.ExternalToolFailed,
            innerException: innerException);

    public void Dispose()
    {
        // Callers report StopAsync failures themselves; this only guarantees a stop on early exits.
        try { StopAsync().GetAwaiter().GetResult(); }
        catch (RLoopException) { }
        finally { Output?.Dispose(); ErrorOutput?.Dispose(); CloseNativeHandles(); }
    }

    private sealed class LinuxProcess : PluginProcess
    {
        private int _pid;

        protected override void Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment)
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
                    UnixNative.Check(UnixNative.posix_spawn_file_actions_addchdir_np(ref actions, workingDirectory));
                    if (Output is not null && ErrorOutput is not null)
                    {
                        var outputFd = checked((int)Output.ClientSafePipeHandle.DangerousGetHandle());
                        var errorFd = checked((int)ErrorOutput.ClientSafePipeHandle.DangerousGetHandle());
                        // Do not let a host with closed standard descriptors alias our pipes.
                        if (outputFd <= 2 || errorFd <= 2) throw new Win32Exception(UnixNative.BadDescriptor);
                        UnixNative.Check(UnixNative.posix_spawn_file_actions_adddup2(ref actions, outputFd, 1));
                        UnixNative.Check(UnixNative.posix_spawn_file_actions_adddup2(ref actions, errorFd, 2));
                        UnixNative.Check(UnixNative.posix_spawn_file_actions_addopen(ref actions, 0, "/dev/null", 0, 0));
                    }
                    // The child leads its own group, so it is not in the terminal's foreground: reading the
                    // terminal would stop it until the deadline. A terminal stdin becomes empty input instead.
                    else if (!Console.IsInputRedirected)
                        UnixNative.Check(UnixNative.posix_spawn_file_actions_addopen(ref actions, 0, "/dev/null", 0, 0));
                    // Never hand the plugin lock or other host descriptors to the plugin.
                    UnixNative.Check(UnixNative.posix_spawn_file_actions_addclosefrom_np(ref actions, 3));
                    using var argv = new Utf8Vector(arguments.Prepend(executable));
                    using var envp = new Utf8Vector(environment.Select(item => item.Key + "=" + item.Value));
                    UnixNative.Check(UnixNative.posix_spawn(out var pid, executable, ref actions, ref attributes, argv.Pointer, envp.Pointer));
                    _pid = pid;
                    // WNOWAIT deliberately pins the leader PID/PGID until group cleanup is confirmed.
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

        protected override async Task<int> StopCoreAsync(TimeSpan confirmation)
        {
            if (_pid == 0) return -1;
            if (_pid <= 1) throw StopError("Invalid plugin process group.");
            var stopped = Stopwatch.StartNew();
            // Signal the pinned group only; never enumerate and signal individual PIDs.
            if (UnixNative.kill(-_pid, UnixNative.KillSignal) != 0 && Marshal.GetLastPInvokeError() != UnixNative.NoSuchProcess)
                throw StopError("Could not stop the plugin process group.");
            await Exited.WaitAsync(confirmation).ConfigureAwait(false);
            while (GroupHasExecutingMembers())
            {
                if (stopped.Elapsed >= confirmation) throw StopError("Plugin process group termination was not confirmed.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            int result, status;
            do { result = UnixNative.waitpid(_pid, out status, 0); }
            while (result < 0 && Marshal.GetLastPInvokeError() == UnixNative.Interrupted);
            if (result != _pid) throw StopError("Could not reap the plugin process.");
            _pid = 0;
            return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
        }

        private bool GroupHasExecutingMembers()
        {
            // Verifies kernel PGID membership AFTER group termination; not a way to discover what to signal.
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
                if (fields.Length < 3) throw StopError("Could not verify plugin process group termination.");
                // Dead entries are not executing; a reaping (X) entry reports its pgrp as -1.
                if (fields[0] is "Z" or "X") continue;
                if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var group))
                    throw StopError("Could not verify plugin process group termination.");
                if (group == _pid) return true;
            }
            return false;
        }
    }

    private sealed class WindowsProcess : PluginProcess
    {
        private SafeFileHandle? _job, _process;
        private Task<int> _exitCode = Task.FromResult(-1);
        private bool _assigned;

        protected override void Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment)
        {
            _job = WindowsNative.Own(WindowsNative.CreateJobObjectW(IntPtr.Zero, null));
            var limits = new WindowsNative.ExtendedLimits { Basic = new() { Flags = WindowsNative.KillOnJobClose } };
            WindowsNative.Check(WindowsNative.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<WindowsNative.ExtendedLimits>()));
            var security = new WindowsNative.SecurityAttributes { Size = Marshal.SizeOf<WindowsNative.SecurityAttributes>(), Inherit = 1 };
            var capture = Output is not null && ErrorOutput is not null;
            // A console stdin becomes empty input, matching Linux; redirected stdin is passed through.
            using var input = capture || !Console.IsInputRedirected
                ? WindowsNative.OpenNull(ref security, write: false)
                : WindowsNative.DuplicateStandard(-10, ref security, write: false);
            using var output = capture ? null : WindowsNative.DuplicateStandard(-11, ref security, write: true);
            using var error = capture ? null : WindowsNative.DuplicateStandard(-12, ref security, write: true);
            var outputHandle = output?.DangerousGetHandle() ?? Output!.ClientSafePipeHandle.DangerousGetHandle();
            var errorHandle = error?.DangerousGetHandle() ?? ErrorOutput!.ClientSafePipeHandle.DangerousGetHandle();
            using var attributes = new WindowsAttributes([input.DangerousGetHandle(), outputHandle, errorHandle]);
            var startup = new WindowsNative.StartupInfoEx
            {
                Info = new() { Size = Marshal.SizeOf<WindowsNative.StartupInfoEx>(), Flags = 0x100,
                    Input = input.DangerousGetHandle(), Output = outputHandle, Error = errorHandle },
                Attributes = attributes.Pointer
            };
            var command = new StringBuilder(string.Join(' ', arguments.Prepend(executable).Select(QuoteWindowsArgument)));
            var block = Marshal.StringToHGlobalUni(string.Join('\0', environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Key + "=" + item.Value)) + "\0\0");
            WindowsNative.ProcessInformation child;
            try
            {
                // Suspended creation prevents even an immediate child launch before job assignment.
                // No CREATE_NO_WINDOW: descendants share the host console instead of opening new windows.
                WindowsNative.Check(WindowsNative.CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    WindowsNative.CreateSuspended | WindowsNative.UnicodeEnvironment | WindowsNative.ExtendedStartup,
                    block, workingDirectory, ref startup, out child));
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

        protected override async Task<int> StopCoreAsync(TimeSpan confirmation)
        {
            if (_process is null) return -1;
            // Job handles remain valid after the original process exits; no parent PID is needed.
            if (_assigned) WindowsNative.Check(WindowsNative.TerminateJobObject(_job!, 1));
            else
            {
                WindowsNative.Check(WindowsNative.TerminateProcess(_process, 1)); // still suspended, never executed
                if (WindowsNative.WaitForSingleObject(_process, (uint)confirmation.TotalMilliseconds) != 0)
                    throw StopError("Suspended plugin process termination was not confirmed.");
                return -1;
            }
            var stopped = Stopwatch.StartNew();
            await _exitCode.WaitAsync(confirmation).ConfigureAwait(false);
            while (true)
            {
                WindowsNative.Check(WindowsNative.QueryInformationJobObject(_job!, 1, out var accounting,
                    (uint)Marshal.SizeOf<WindowsNative.Accounting>(), IntPtr.Zero));
                if (accounting.ActiveProcesses == 0) break;
                if (stopped.Elapsed >= confirmation) throw StopError("Plugin job termination was not confirmed.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            return await _exitCode.ConfigureAwait(false);
        }

        protected override void CloseNativeHandles() { _process?.Dispose(); _job?.Dispose(); }

        // CreateProcess receives one command line; quote each argument with the Windows CRT backslash/quote
        // rules (not shell escaping). The application path is passed explicitly, so no search happens.
        private static string QuoteWindowsArgument(string value)
        {
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

    private sealed class Utf8Vector : IDisposable
    {
        private readonly List<IntPtr> _strings = [];
        public IntPtr Pointer { get; private set; }
        public Utf8Vector(IEnumerable<string> values)
        {
            try
            {
                foreach (var value in values) _strings.Add(Marshal.StringToCoTaskMemUTF8(value));
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
                // PROC_THREAD_ATTRIBUTE_HANDLE_LIST: inherit the three standard handles and nothing else.
                WindowsNative.Check(WindowsNative.UpdateProcThreadAttribute(Pointer, 0, 0x00020002, _handles,
                    (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
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
        public const uint CreateSuspended = 4, UnicodeEnvironment = 0x400, ExtendedStartup = 0x80000, KillOnJobClose = 0x2000;
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
        public static SafeFileHandle OpenNull(ref SecurityAttributes security, bool write) =>
            Own(CreateFileW("NUL", write ? 0x40000000u : 0x80000000u, 3, ref security, 3, 0, IntPtr.Zero));
        /// <summary>An inheritable duplicate of a standard handle, or NUL when the host has none.</summary>
        public static SafeFileHandle DuplicateStandard(int standardHandle, ref SecurityAttributes security, bool write)
        {
            var handle = GetStdHandle(standardHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return OpenNull(ref security, write);
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

    internal static class UnixNative
    {
        public const short SpawnSetProcessGroup = 0x02;
        public const int WaitExited = 4, WaitNoReap = 0x01000000, Interrupted = 4, NoSuchProcess = 3, BadDescriptor = 9, KillSignal = 9;
        // glibc spawn.h ABI (same layout as PluginGit). Attribute fields are accessed only through libc setters;
        // GNU chdir/closefrom actions fail closed if unavailable.
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
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addopen(ref SpawnActions actions, int descriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addclosefrom_np(ref SpawnActions actions, int from);
        [DllImport("libc")] public static extern int posix_spawn_file_actions_addchdir_np(ref SpawnActions actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport("libc")] public static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            ref SpawnActions actions, ref SpawnAttributes attributes, IntPtr arguments, IntPtr environment);
        [DllImport("libc")] public static extern int euidaccess([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
        [DllImport("libc", SetLastError = true)] public static extern int kill(int pid, int signal);
        [DllImport("libc", SetLastError = true)] public static extern int waitid(int type, uint id, out SignalInfo info, int options);
        [DllImport("libc", SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
    }
}
