using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace RLoop.Core.Plugins;

/// <summary>Publishes a verified copy with one rename; never updates an existing installation.</summary>
public sealed class PluginInstaller
{
    private const string UnsafePath = "PLUGIN_PATH_UNSAFE";
    private const string InvalidSource = "PLUGIN_SOURCE_INVALID";
    private const long MaximumPackageBytes = 200L * 1024 * 1024;
    private readonly PluginRegistry _registry;
    private readonly Action<string>? _stagedPackageReady;

    public PluginInstaller(PluginRegistry registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    // Deterministic copy/validation boundary for the staged-identity regression test. No public hook.
    internal PluginInstaller(PluginRegistry registry, Action<string> stagedPackageReady) : this(registry) =>
        _stagedPackageReady = stagedPackageReady;

    public InstalledPlugin Install(string source, string? gitRef = null)
    {
        try { return InstallCore(source, gitRef); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Source paths/URLs and raw IO diagnostics must not enter persisted/displayed errors.
            throw PluginErrors.Create(InvalidSource, "Could not install the plugin package.");
        }
    }

    private InstalledPlugin InstallCore(string source, string? gitRef)
    {
        string? local = null;
        IReadOnlyList<string>? localFiles = null;
        PluginSource metadata;
        if (Directory.Exists(source))
        {
            if (gitRef is not null) throw PluginErrors.Create(InvalidSource, "A local directory cannot have a Git ref.");
            local = PluginPathPolicy.ResolveDirectory(source, InvalidSource);
            if (Overlaps(local, _registry.ProjectRoot))
                throw PluginErrors.Create(InvalidSource, "Plugin source and project directories must not overlap.");
            metadata = new("directory", local, null);
            localFiles = PluginGit.GetLocalFiles(local);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(source)) throw PluginErrors.Create(InvalidSource, "A plugin source is required.");
            if (!source.Contains("://", StringComparison.Ordinal) && (Path.IsPathRooted(source) || !source.Contains(':')))
                throw PluginErrors.Create("PLUGIN_SOURCE_NOT_FOUND", "The local plugin source directory does not exist.");
            metadata = PluginGit.ParseSource(source, gitRef!);
            if (Uri.TryCreate(metadata.Location, UriKind.Absolute, out var uri) && uri.IsFile && Directory.Exists(uri.LocalPath)
                && Overlaps(PluginPathPolicy.ResolveDirectory(uri.LocalPath, InvalidSource), _registry.ProjectRoot))
                throw PluginErrors.Create(InvalidSource, "Plugin source and project directories must not overlap.");
        }

        EnsureManagementArea(create: true);
        using var projectLock = PluginProjectLock.Acquire(_registry.PluginsDirectory);
        EnsureManagementArea(create: false);
        EnsureIgnore();
        var existing = _registry.LoadAllUnderLock();
        string? staging = null;
        try
        {
            var stage = Path.Combine(_registry.PluginsDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
            PluginPathPolicy.EnsureNoLinks(stage, UnsafePath, allowMissing: true);
            if (Directory.Exists(stage) || File.Exists(stage)) throw PluginErrors.Create(UnsafePath, "Staging path is already occupied.");
            Directory.CreateDirectory(stage);
            staging = stage; // Only this operation's newly created staging root is eligible for cleanup.
            var package = Path.Combine(stage, "package");
            Directory.CreateDirectory(package);
            Directory.CreateDirectory(Path.Combine(stage, "data"));
            string? commit = null;
            if (local is not null) CopyLocal(local, package, localFiles!);
            else
            {
                var clone = Path.Combine(stage, "clone");
                Directory.CreateDirectory(clone);
                var export = PluginGit.Export(source, gitRef!, clone, package);
                metadata = export.Source;
                commit = export.Commit;
                // A bare repository is private working state, never part of the published installation.
                PluginPathPolicy.DeleteTree(clone, UnsafePath);
            }

            _stagedPackageReady?.Invoke(package);
            var manifest = PluginManifest.Read(package);
            manifest.EnsureCompatible(_registry.ResoloopVersion);
            if (existing.Any(plugin => StringComparer.OrdinalIgnoreCase.Equals(plugin.Name, manifest.Name)))
                throw PluginErrors.Create("PLUGIN_ALREADY_INSTALLED", "The plugin is already installed.");
            if (PluginNames.FindConflicts(manifest, existing.Select(plugin => plugin.Manifest), _registry.Reservations).Count != 0)
                throw PluginErrors.Create("PLUGIN_NAME_CONFLICT", "The plugin conflicts with installed or reserved names.");
            var record = new PluginInstallRecord(1, manifest.Name, manifest.Version, metadata, commit,
                DateTimeOffset.UtcNow, _registry.ResoloopVersion, HashPackage(package));
            record.Write(Path.Combine(stage, "install.json"));
            var verified = _registry.VerifyInstallationUnderLock(manifest.Name, stage);
            var destination = Path.Combine(_registry.PluginsDirectory, manifest.Name);
            PluginPathPolicy.EnsureNoLinks(destination, UnsafePath, allowMissing: true);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw PluginErrors.Create("PLUGIN_ALREADY_INSTALLED", "The plugin destination is already occupied.");
            // Prepare all result state before publication. Nothing needing verification or cleanup follows the rename.
            var result = verified with { InstallDirectory = destination, PackageDirectory = Path.Combine(destination, "package"),
                DataDirectory = Path.Combine(destination, "data") };
            Directory.Move(stage, destination);
            staging = null;
            return result;
        }
        finally
        {
            if (staging is not null) PluginPathPolicy.DeleteTree(staging, UnsafePath);
        }
    }

    public void Remove(string name, bool confirmed)
    {
        if (!PluginNames.IsValid(name)) throw PluginErrors.Create("PLUGIN_NAME_INVALID", "Invalid plugin name.");
        if (!confirmed) throw PluginErrors.Create("CONFIRMATION_REQUIRED", "Removing a plugin requires explicit confirmation.");
        try
        {
            if (!EnsureManagementArea(create: false)) throw PluginErrors.Create("PLUGIN_NOT_INSTALLED", "The plugin is not installed.");
            using var projectLock = PluginProjectLock.Acquire(_registry.PluginsDirectory);
            EnsureManagementArea(create: false);
            var target = Path.Combine(_registry.PluginsDirectory, name);
            PluginPathPolicy.EnsureNoLinks(target, UnsafePath, allowMissing: true);
            if (!Directory.Exists(target)) throw PluginErrors.Create("PLUGIN_NOT_INSTALLED", "The plugin is not installed.");
            // A broken record is removable, but a linked package root is never a deletion target.
            PluginPathPolicy.EnsureNoLinks(Path.Combine(target, "package"), UnsafePath, allowMissing: true);
            PluginPathPolicy.DeleteTree(target, UnsafePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw PluginErrors.Create("PLUGIN_REMOVE_REFUSED", "Could not safely remove the plugin.");
        }
    }

    private bool EnsureManagementArea(bool create)
    {
        PluginPathPolicy.EnsureNoLinks(_registry.ProjectRoot, UnsafePath);
        var management = Path.Combine(_registry.ProjectRoot, ".resoloop");
        EnsureDirectory(management);
        PluginPathPolicy.EnsureNoLinks(Path.Combine(management, ".gitignore"), UnsafePath, allowMissing: true);
        EnsureDirectory(_registry.PluginsDirectory);
        PluginPathPolicy.EnsureNoLinks(Path.Combine(_registry.PluginsDirectory, ".gitignore"), UnsafePath, allowMissing: true);
        PluginPathPolicy.EnsureNoLinks(Path.Combine(_registry.PluginsDirectory, ".lock"), UnsafePath, allowMissing: true);
        return Directory.Exists(_registry.PluginsDirectory);

        void EnsureDirectory(string path)
        {
            PluginPathPolicy.EnsureNoLinks(path, UnsafePath, allowMissing: true);
            if (File.Exists(path)) throw PluginErrors.Create(UnsafePath, "Plugin management paths must be directories.");
            if (create) Directory.CreateDirectory(path);
        }
    }

    private void EnsureIgnore()
    {
        var path = Path.Combine(_registry.PluginsDirectory, ".gitignore");
        PluginPathPolicy.EnsureNoLinks(path, UnsafePath, allowMissing: true);
        if (Directory.Exists(path)) throw PluginErrors.Create("PLUGIN_IGNORE_CONFLICT", "Plugin ignore path is not a file.");
        if (File.Exists(path))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != 2 || input.ReadByte() != '*' || input.ReadByte() != '\n')
                throw PluginErrors.Create("PLUGIN_IGNORE_CONFLICT", "Existing plugin ignore rules differ from the managed rules.");
        }
        else
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write("*\n"u8);
        }
    }

    private static void CopyLocal(string source, string package, IReadOnlyList<string> files)
    {
        if (files.Count > 10000) throw PluginErrors.Create(InvalidSource, "Plugin package exceeds the file count limit.");
        long total = 0;
        foreach (var relative in files)
        {
            var length = new FileInfo(PluginPathPolicy.Combine(source, relative, InvalidSource)).Length;
            if (length > MaximumPackageBytes - total) throw PluginErrors.Create(InvalidSource, "Plugin package exceeds the size limit.");
            total += length;
        }
        total = 0;
        var buffer = new byte[81920];
        foreach (var relative in files)
        {
            var from = PluginPathPolicy.Combine(source, relative, InvalidSource);
            var to = PluginPathPolicy.Combine(package, relative, InvalidSource);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    if (count > MaximumPackageBytes - total) throw PluginErrors.Create(InvalidSource, "Plugin package exceeds the size limit.");
                    total += count;
                    output.Write(buffer, 0, count);
                }
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
    }

    private static IReadOnlyDictionary<string, string> HashPackage(string package)
    {
        var files = PluginPathPolicy.EnumerateFiles(package, InvalidSource);
        if (files.Count > 10000) throw PluginErrors.Create(InvalidSource, "Plugin package exceeds the file count limit.");
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var buffer = new byte[81920];
        foreach (var relative in files)
        {
            using var stream = new FileStream(PluginPathPolicy.Combine(package, relative, InvalidSource), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (count > MaximumPackageBytes - total) throw PluginErrors.Create(InvalidSource, "Plugin package exceeds the size limit.");
                total += count;
                hash.AppendData(buffer, 0, count);
            }
            hashes.Add(relative, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        return new ReadOnlyDictionary<string, string>(hashes);
    }

    private static bool Overlaps(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        left = Path.TrimEndingDirectorySeparator(left);
        right = Path.TrimEndingDirectorySeparator(right);
        var leftPrefix = Path.EndsInDirectorySeparator(left) ? left : left + Path.DirectorySeparatorChar;
        var rightPrefix = Path.EndsInDirectorySeparator(right) ? right : right + Path.DirectorySeparatorChar;
        return string.Equals(left, right, comparison) || left.StartsWith(rightPrefix, comparison)
            || right.StartsWith(leftPrefix, comparison);
    }
}
