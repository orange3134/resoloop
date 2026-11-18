using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RLoop.Core.Plugins;

/// <summary>Shared portable paths and non-following filesystem operations for plugin data.</summary>
internal static class PluginPathPolicy
{
    public static string ResolveDirectory(string path, string code) => Guard(code, () => ResolveDirectoryCore(path, code, 40));

    private static string ResolveDirectoryCore(string path, string code, int remainingLinks)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                if (remainingLinks == 0) throw Unsafe(code, "Too many directory links.");
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw Unsafe(code, "Directory link has no target.");
                current = ResolveDirectoryCore(target.FullName, code, remainingLinks - 1);
            }
            if (!Directory.Exists(current)) throw Unsafe(code, "Expected an existing directory.");
            EnsureOrdinary(current, code);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    public static void ValidateRelativePath(string relative, string code)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
            throw Unsafe(code, "Expected a package-relative portable path.");
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || c == 127 || "\\:<>\"|?*".Contains(c)))
                throw Unsafe(code, "Path contains a non-portable component.");
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(stem[3])))
                throw Unsafe(code, "Path contains a reserved Windows device name.");
        }
    }

    public static string Combine(string root, string relative, string code) => Guard(code, () =>
    {
        ValidateRelativePath(relative, code);
        EnsureNoLinks(root, code);
        if (!Directory.Exists(root)) throw Unsafe(code, "Expected an existing root directory.");
        var result = Path.Combine(Path.GetFullPath(root), relative.Replace('/', Path.DirectorySeparatorChar));
        EnsureNoLinks(result, code, allowMissing: true);
        return result;
    });

    public static void EnsureNoLinks(string path, string code, bool allowMissing = false) => Guard(code, () =>
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        Check(current);
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Check(current)) break; // The remaining components cannot yet exist.
        }
        return true;

        bool Check(string candidate)
        {
            if (IsLink(candidate)) throw Unsafe(code, "Links are not allowed in this path.");
            try { EnsureOrdinary(candidate, code); return true; }
            catch (FileNotFoundException) when (allowMissing) { return false; }
            catch (DirectoryNotFoundException) when (allowMissing) { return false; }
        }
    });

    public static IReadOnlyList<string> EnumerateFiles(string root, string code, bool rejectNestedGit = false) => Guard(code, () =>
    {
        EnsureNoLinks(root, code);
        if (!Directory.Exists(root)) throw Unsafe(code, "Expected an existing package directory.");
        var files = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Full, string Relative)>();
        stack.Push((Path.GetFullPath(root), ""));
        while (stack.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Full))
            {
                var name = Path.GetFileName(entry);
                var relative = directory.Relative.Length == 0 ? name : directory.Relative + "/" + name;
                ValidateRelativePath(relative, code);
                if (!paths.Add(relative)) throw Unsafe(code, "Package paths collide ignoring case.");
                if (rejectNestedGit && name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    throw Unsafe(code, "Nested repositories are not allowed in a plain directory package.");
                if (IsLink(entry)) throw Unsafe(code, "Package contains a link.");
                var attributes = EnsureOrdinary(entry, code);
                if ((attributes & FileAttributes.Directory) != 0) stack.Push((entry, relative));
                else files.Add(relative);
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files.AsReadOnly();
    });

    public static void DeleteTree(string path, string code) => Guard(code, () =>
    {
        // Unlike child links, the selected root and its ancestors may never be links.
        EnsureNoLinks(path, code);
        if (!Directory.Exists(path)) throw Unsafe(code, "Expected an existing directory to delete.");
        var stack = new Stack<(string Path, bool Visited)>();
        stack.Push((Path.GetFullPath(path), false));
        while (stack.TryPop(out var item))
        {
            if (IsLink(item.Path))
            {
                if ((File.GetAttributes(item.Path) & FileAttributes.Directory) != 0) Directory.Delete(item.Path);
                else File.Delete(item.Path);
                continue;
            }
            // Copy restrictions do not apply to cleanup: unlink FIFO/socket/device entries
            // without opening them, while traversing only actual directories.
            var attributes = File.GetAttributes(item.Path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                // A file may be a hardlink sharing its inode with names outside this tree, so its
                // attributes stay unchanged. Unix unlink needs only the parent directory's write permission.
                if (OperatingSystem.IsWindows() && (attributes & FileAttributes.ReadOnly) != 0)
                    WindowsNative.DeleteReadOnlyFile(item.Path, attributes);
                else File.Delete(item.Path);
                continue;
            }
            // Directories cannot be hardlinked, so a non-link directory belongs only to this tree.
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(item.Path, attributes & ~FileAttributes.ReadOnly);
            if (item.Visited) { Directory.Delete(item.Path); continue; }
            stack.Push((item.Path, true));
            foreach (var child in Directory.EnumerateFileSystemEntries(item.Path)) stack.Push((child, false));
        }
        return true;
    });

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static FileAttributes EnsureOrdinary(string path, string code)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Device) != 0) throw Unsafe(code, "Special files are not allowed.");
        if (OperatingSystem.IsLinux())
        {
            // statx has a stable ABI across Linux architectures. FileAttributes alone treats FIFOs
            // and sockets as Normal; opening them to inspect would already risk blocking or I/O.
            if (Statx(-100, path, 0x100, 1, out var stat) != 0)
                throw new IOException("Could not determine the filesystem entry type.", Marshal.GetLastPInvokeError());
            if ((stat.Mask & 1) == 0 || (stat.Mode & 0xF000) is not (0x8000 or 0x4000))
                throw Unsafe(code, "Only ordinary files and directories are allowed.");
        }
        else if (!OperatingSystem.IsWindows())
        {
            throw Unsafe(code, "Safe filesystem type inspection is unavailable on this platform.");
        }
        return attributes;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx stat);

    private static class WindowsNative
    {
        private const uint Delete = 0x10000, ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3;
        private const uint OpenReparsePoint = 0x200000, BackupSemantics = 0x2000000;
        private const int FileDispositionInfoEx = 21;
        private const uint DispositionDelete = 0x1, PosixSemantics = 0x2, IgnoreReadOnlyAttribute = 0x10;

        // Git objects/packs can be read-only. A sole name may be normalized and deleted; a
        // hardlinked name is removed without touching the attributes shared with other names.
        public static void DeleteReadOnlyFile(string path, FileAttributes attributes)
        {
            using (var handle = CreateFileW(path, Delete | ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw Failure("Could not open a read-only file for deletion.");
                if (!GetFileInformationByHandle(handle, out var information)) throw Failure("Could not determine the file link count.");
                if (information.NumberOfLinks > 1)
                {
                    var disposition = new DispositionInfoEx { Flags = DispositionDelete | PosixSemantics | IgnoreReadOnlyAttribute };
                    if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, ref disposition, (uint)Marshal.SizeOf<DispositionInfoEx>()))
                        throw Failure("Could not delete a hardlinked read-only file without changing its attributes.");
                    return;
                }
            }
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }

        private static IOException Failure(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

        [StructLayout(LayoutKind.Explicit, Size = 52)]
        private struct ByHandleFileInformation
        {
            [FieldOffset(40)] public uint NumberOfLinks;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DispositionInfoEx { public uint Flags; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref DispositionInfoEx information, uint size);
    }

    private static RLoopException Unsafe(string code, string message) => PluginErrors.Create(code, message);
    private static T Guard<T>(string code, Func<T> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw PluginErrors.Create(code, "Plugin filesystem operation failed.", innerException: ex);
        }
    }
}
