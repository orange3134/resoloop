using System.Security.Cryptography;

namespace RLoop.Core.Plugins;

public sealed record PluginProblem(string Code, string Message, IReadOnlyDictionary<string, object?> Details, IReadOnlyList<string> Suggestions);
public sealed record PluginStatus(string Name, string InstallDirectory, PluginInstallRecord? Record, PluginManifest? Manifest, PluginProblem? Problem)
{
    public bool Ok => Problem is null;
}
public sealed record InstalledPlugin(string Name, string InstallDirectory, string PackageDirectory, string DataDirectory, PluginManifest Manifest, PluginInstallRecord Record);

/// <summary>Retain this lease until all reads, execution or distribution of Plugins have finished.</summary>
public sealed class PluginReadLease : IDisposable
{
    private IDisposable? _projectLock;
    internal PluginReadLease(IReadOnlyList<InstalledPlugin> plugins, IDisposable? projectLock) =>
        (Plugins, _projectLock) = (plugins, projectLock);
    public IReadOnlyList<InstalledPlugin> Plugins { get; }
    public void Dispose() => Interlocked.Exchange(ref _projectLock, null)?.Dispose();
}

public sealed class PluginRegistry
{
    private const long MaximumPackageBytes = 200L * 1024 * 1024;

    public PluginRegistry(string projectRoot, string resoloopVersion, PluginNameReservations reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        ProjectRoot = FindProjectRoot(projectRoot, Directory.GetCurrentDirectory());
        try { SemanticVersion.Parse(resoloopVersion); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw PluginErrors.Create("PLUGIN_INCOMPATIBLE_VERSION", "Invalid current ResoLoop version.", innerException: ex);
        }
        PluginsDirectory = Path.Combine(ProjectRoot, ".resoloop", "plugins");
        ResoloopVersion = resoloopVersion;
        Reservations = reservations;
    }

    public string ProjectRoot { get; }
    public string PluginsDirectory { get; }
    public string ResoloopVersion { get; }
    internal PluginNameReservations Reservations { get; }

    public static string FindProjectRoot(string? explicitProject, string startDirectory)
    {
        try
        {
            var start = Path.GetFullPath(startDirectory);
            if (explicitProject is not null)
            {
                if (string.IsNullOrWhiteSpace(explicitProject)) throw ProjectNotFound();
                var exact = PluginPathPolicy.ResolveDirectory(Path.GetFullPath(explicitProject, start), "PLUGIN_PROJECT_NOT_FOUND");
                if (!HasMarker(exact)) throw ProjectNotFound();
                return exact;
            }
            var current = new DirectoryInfo(PluginPathPolicy.ResolveDirectory(start, "PLUGIN_PROJECT_NOT_FOUND"));
            while (current is not null)
            {
                if (HasMarker(current.FullName)) return current.FullName;
                current = current.Parent;
            }
            throw ProjectNotFound();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw PluginErrors.Create("PLUGIN_PROJECT_NOT_FOUND", "Could not locate the requested ResoLoop project.", innerException: ex);
        }

        static bool HasMarker(string directory)
        {
            var marker = Path.Combine(directory, ".resoloop.json");
            PluginPathPolicy.EnsureNoLinks(marker, "PLUGIN_PATH_UNSAFE", allowMissing: true);
            return File.Exists(marker);
        }
    }

    public IReadOnlyList<PluginStatus> List()
    {
        using var projectLock = AcquireIfPresent();
        return projectLock is null ? Array.Empty<PluginStatus>() : ListUnderLock();
    }

    /// <summary>A verified snapshot only. Use OpenVerified when paths will be used afterwards.</summary>
    public IReadOnlyList<InstalledPlugin> LoadAll()
    {
        using var projectLock = AcquireIfPresent();
        return projectLock is null ? Array.Empty<InstalledPlugin>() : LoadAllUnderLock();
    }

    public InstalledPlugin Load(string name)
    {
        if (!PluginNames.IsValid(name)) throw PluginErrors.Create("PLUGIN_NAME_INVALID", "Invalid plugin name.");
        using var projectLock = AcquireIfPresent();
        var plugins = projectLock is null ? Array.Empty<InstalledPlugin>() : LoadAllUnderLock();
        return plugins.FirstOrDefault(plugin => plugin.Name == name)
            ?? throw PluginErrors.Create("PLUGIN_NOT_INSTALLED", "The requested plugin is not installed.");
    }

    public PluginReadLease OpenVerified()
    {
        var projectLock = AcquireIfPresent();
        // An absent area is an empty snapshot, not permission to inspect an area created later.
        if (projectLock is null) return new PluginReadLease(Array.Empty<InstalledPlugin>(), null);
        try { return new PluginReadLease(LoadAllUnderLock(), projectLock); }
        catch { projectLock?.Dispose(); throw; }
    }

    // Installer callers must already hold PluginProjectLock.Acquire(PluginsDirectory).
    // These methods never acquire/reenter a lock or change any installation.
    internal IReadOnlyList<PluginStatus> ListUnderLock()
    {
        if (!ManagementAreaExists()) return Array.Empty<PluginStatus>();
        try
        {
            var statuses = Directory.EnumerateFileSystemEntries(PluginsDirectory)
                .Where(path => !IsInternalName(Path.GetFileName(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => InspectInstallation(Path.GetFileName(path), path)).ToArray();
            var aliases = statuses.GroupBy(status => status.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < statuses.Length; i++)
            {
                var status = statuses[i];
                if (aliases.Contains(status.Name))
                    statuses[i] = status with { Problem = Problem(PluginErrors.Create("PLUGIN_NAME_CONFLICT", "Published plugin names collide ignoring case.")) };
                else if (status.Ok && status.Manifest is { } manifest)
                {
                    var others = statuses.Where((_, index) => index != i).Where(other => other.Manifest is not null).Select(other => other.Manifest!);
                    var conflicts = PluginNames.FindConflicts(manifest, others, Reservations);
                    if (conflicts.Count != 0)
                        statuses[i] = status with { Problem = Problem(PluginErrors.Create("PLUGIN_NAME_CONFLICT", "Plugin names conflict.",
                            new Dictionary<string, object?> { ["conflicts"] = conflicts })) };
                }
            }
            return Array.AsReadOnly(statuses);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Could not inspect the project plugin area.", innerException: ex);
        }
    }

    internal IReadOnlyList<InstalledPlugin> LoadAllUnderLock()
    {
        var statuses = ListUnderLock();
        var problems = statuses.Where(status => !status.Ok).Select(status => status.Problem!).ToArray();
        if (problems.Length != 0)
            throw PluginErrors.Create(problems[0].Code, "Installed plugins failed verification.",
                new Dictionary<string, object?> { ["problems"] = Array.AsReadOnly(problems) },
                Array.AsReadOnly(problems.SelectMany(problem => problem.Suggestions).Distinct(StringComparer.Ordinal).ToArray()));
        return Array.AsReadOnly(statuses.Select(Installed).ToArray());
    }

    /// <summary>Verify one staged/published direct child; the caller checks global name conflicts separately.</summary>
    internal InstalledPlugin VerifyInstallationUnderLock(string name, string installDirectory)
    {
        try
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(directory), PluginsDirectory, comparison))
                throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Installation is outside this project's plugin directory.");
            var status = InspectInstallation(name, directory);
            if (status.Problem is { } problem)
                throw PluginErrors.Create(problem.Code, problem.Message, problem.Details, problem.Suggestions);
            return Installed(status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Could not verify the selected installation path.", innerException: ex);
        }
    }

    private PluginStatus InspectInstallation(string name, string directory)
    {
        PluginInstallRecord? record = null;
        PluginManifest? manifest = null;
        try
        {
            if (!PluginNames.IsValid(name)) throw PluginErrors.Create("PLUGIN_NAME_INVALID", "Invalid published plugin directory name.");
            PluginPathPolicy.EnsureNoLinks(directory, "PLUGIN_PATH_UNSAFE");
            record = PluginInstallRecord.Read(Path.Combine(directory, "install.json"));
            if (record.Name != name) throw PluginErrors.Create("PLUGIN_RECORD_INVALID", "Record name differs from the installation name.");
            var package = Path.Combine(directory, "package");
            VerifyPackage(package, record);
            manifest = PluginManifest.Read(package);
            if (manifest.Name != record.Name || manifest.Version != record.Version)
                throw PluginErrors.Create("PLUGIN_RECORD_INVALID", "Record identity differs from the package manifest.");
            manifest.EnsureCompatible(ResoloopVersion);
            PluginPathPolicy.EnsureNoLinks(Path.Combine(directory, "data"), "PLUGIN_PATH_UNSAFE", allowMissing: true);
            return new(name, directory, record, manifest, null);
        }
        catch (RLoopException ex) { return new(name, directory, record, manifest, Problem(ex)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(name, directory, record, manifest, Problem(PluginErrors.Create("PLUGIN_RECORD_INVALID", "Could not inspect the plugin installation.", innerException: ex)));
        }
    }

    private static void VerifyPackage(string package, PluginInstallRecord record)
    {
        const string modified = "PLUGIN_MODIFIED";
        try
        {
            var actual = PluginPathPolicy.EnumerateFiles(package, modified);
            if (actual.Count > 10000 || actual.Count != record.Files.Count || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(record.Files.Keys))
                throw PluginErrors.Create(modified, "Package file set differs from the install record.");
            long total = 0;
            foreach (var relative in actual)
            {
                var length = new FileInfo(PluginPathPolicy.Combine(package, relative, modified)).Length;
                if (length > MaximumPackageBytes - total) throw PluginErrors.Create(modified, "Package exceeds the size limit.");
                total += length;
            }
            total = 0;
            var buffer = new byte[81920];
            foreach (var relative in actual)
            {
                using var stream = new FileStream(PluginPathPolicy.Combine(package, relative, modified), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count;
                while ((count = stream.Read(buffer)) != 0)
                {
                    if (count > MaximumPackageBytes - total) throw PluginErrors.Create(modified, "Package exceeds the size limit.");
                    total += count;
                    hash.AppendData(buffer, 0, count);
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != record.Files[relative])
                    throw PluginErrors.Create(modified, "Package content differs from the install record.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PluginErrors.Create(modified, "Could not verify the entire plugin package.", innerException: ex);
        }
    }

    private IDisposable? AcquireIfPresent() => ManagementAreaExists() ? PluginProjectLock.Acquire(PluginsDirectory) : null;

    private bool ManagementAreaExists()
    {
        // Check every public management boundary before either treating it as absent or opening a lock.
        PluginPathPolicy.EnsureNoLinks(ProjectRoot, "PLUGIN_PATH_UNSAFE");
        var management = Path.Combine(ProjectRoot, ".resoloop");
        RequireDirectoryOrMissing(management);
        PluginPathPolicy.EnsureNoLinks(Path.Combine(management, ".gitignore"), "PLUGIN_PATH_UNSAFE", allowMissing: true);
        RequireDirectoryOrMissing(PluginsDirectory);
        PluginPathPolicy.EnsureNoLinks(Path.Combine(PluginsDirectory, ".gitignore"), "PLUGIN_PATH_UNSAFE", allowMissing: true);
        PluginPathPolicy.EnsureNoLinks(Path.Combine(PluginsDirectory, ".lock"), "PLUGIN_PATH_UNSAFE", allowMissing: true);
        return Directory.Exists(PluginsDirectory);
    }

    private static void RequireDirectoryOrMissing(string path)
    {
        PluginPathPolicy.EnsureNoLinks(path, "PLUGIN_PATH_UNSAFE", allowMissing: true);
        if (File.Exists(path)) throw PluginErrors.Create("PLUGIN_PATH_UNSAFE", "Plugin management path must be a directory.");
    }

    private static bool IsInternalName(string name) => name is ".lock" or ".gitignore" || name.StartsWith(".staging-", StringComparison.Ordinal);
    private static RLoopException ProjectNotFound() => PluginErrors.Create("PLUGIN_PROJECT_NOT_FOUND", "The requested ResoLoop project has no .resoloop.json marker.");
    private static PluginProblem Problem(RLoopException error) => new(error.Code, error.Message, error.Context, error.Suggestions);
    private static InstalledPlugin Installed(PluginStatus status) => new(status.Name, status.InstallDirectory,
        Path.Combine(status.InstallDirectory, "package"), Path.Combine(status.InstallDirectory, "data"), status.Manifest!, status.Record!);
}
