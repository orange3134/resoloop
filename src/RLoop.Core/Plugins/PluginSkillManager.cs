using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

/// <summary>One distributed file. Path is relative to .agents/skills/ and uses '/'.</summary>
public sealed record PluginSkillFileStatus(string Plugin, string Skill, string Path, string Status,
    string PackageHash, string? InstalledHash, string? RecordedHash, string? RecordedPlugin);
/// <summary>A recorded file no installed plugin declares any more. It is kept with its protection record.</summary>
public sealed record PluginSkillRetainedFile(string Plugin, string Path, string RecordedHash);
public sealed record PluginSkillSyncResult(string Mode, bool Synchronized, IReadOnlyList<string> Updated,
    IReadOnlyList<PluginSkillFileStatus> Files, IReadOnlyList<PluginSkillRetainedFile> Retained);

public static class PluginSkillManager
{
    public const string RecordRelativePath = ".agents/skills/.resoloop-plugins.json";

    /// <summary>
    /// Opens the verified plugins of exactly this directory (never an ancestor project) and plans their skills.
    /// Returns null when the directory has no project marker or no plugin area; the record is not read then.
    /// </summary>
    internal static PluginSkillPlan? Open(string targetDirectory, string resoloopVersion, PluginNameReservations reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        var root = Path.GetFullPath(targetDirectory);
        if (!File.Exists(Path.Combine(root, ".resoloop.json")) || !Directory.Exists(Path.Combine(root, ".resoloop", "plugins")))
            return null;
        // Bundled names are reserved here so a caller's catalog cannot leave them open to plugins.
        var registry = new PluginRegistry(root, resoloopVersion, new PluginNameReservations(reservations.Commands,
            reservations.Skills.Concat(BundledSkillManager.Names), reservations.Recipes));
        var lease = registry.OpenVerified();
        try { return new PluginSkillPlan(registry.ProjectRoot, lease); }
        catch { lease.Dispose(); throw; }
    }
}

/// <summary>Plans plugin skill distribution under a verified read lease that is held until Dispose.</summary>
internal sealed class PluginSkillPlan : IDisposable
{
    private const string RecordInvalid = "PLUGIN_SKILL_RECORD_INVALID";
    private const string Unsafe = "PLUGIN_PATH_UNSAFE";
    private const int MaximumRecordBytes = 8 * 1024 * 1024;
    private static readonly Regex HashPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions RecordJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n"
    };

    private readonly PluginReadLease _lease;
    private readonly string _skillsRoot;
    private readonly string _recordPath;
    private readonly bool _recordExists;
    private readonly Dictionary<string, (string Path, RecordEntry Entry)> _record;
    private readonly List<Planned> _planned = [];

    internal PluginSkillPlan(string projectRoot, PluginReadLease lease)
    {
        _lease = lease;
        _skillsRoot = Path.Combine(projectRoot, ".agents", "skills");
        _recordPath = Path.Combine(_skillsRoot, ".resoloop-plugins.json");
        (_record, _recordExists) = ReadRecord(_recordPath);
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in lease.Plugins.OrderBy(plugin => plugin.Name, StringComparer.Ordinal))
            foreach (var skill in plugin.Manifest.Skills)
            {
                if (skill.Name.StartsWith("resonite-", StringComparison.OrdinalIgnoreCase)
                    || BundledSkillManager.Names.Contains(skill.Name, StringComparer.OrdinalIgnoreCase))
                    throw PluginErrors.Create("PLUGIN_NAME_CONFLICT", "A plugin skill uses a reserved bundled skill name.",
                        new Dictionary<string, object?> { ["plugin"] = plugin.Name, ["skill"] = skill.Name });
                // The manifest path may differ in case from the recorded spelling on Windows; the record has no case collisions.
                var prefix = skill.Path + "/";
                foreach (var (packageRelative, hash) in plugin.Record.Files)
                {
                    if (!packageRelative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = skill.Name + "/" + packageRelative[prefix.Length..];
                    declared.Add(target);
                    _planned.Add(new(Plan(plugin.Name, skill.Name, target, hash), plugin.PackageDirectory, packageRelative));
                }
            }
        Retained = _record.Values.Where(item => !declared.Contains(item.Path)).OrderBy(item => item.Path, StringComparer.Ordinal)
            .Select(item => new PluginSkillRetainedFile(item.Entry.Plugin, item.Path, item.Entry.Sha256)).ToArray();
    }

    public IReadOnlyList<PluginSkillRetainedFile> Retained { get; }
    public bool Synchronized => _planned.All(item => item.Status.Status == "current");

    /// <summary>Init never overwrites an existing file, so an available update is a conflict there.</summary>
    public IReadOnlyList<PluginSkillFileStatus> Conflicts(bool init) => _planned.Select(item => item.Status)
        .Where(status => status.Status.EndsWith("conflict", StringComparison.Ordinal) || (init && status.Status == "update-available"))
        .ToArray();

    /// <summary>
    /// Writes pending files, then the record last. A failure leaves the record unchanged, so a file that was
    /// not written is never recorded as distributed; files already written are adopted by the next sync.
    /// </summary>
    public IReadOnlyList<string> Apply(bool init)
    {
        if (Conflicts(init).Count != 0) throw new InvalidOperationException("Conflicts must be checked before applying.");
        var updated = new List<string>();
        var record = _record.Values.ToDictionary(item => item.Path, item => item.Entry, StringComparer.OrdinalIgnoreCase);
        foreach (var (status, packageDirectory, packageRelative) in _planned)
        {
            if (status.Status is "missing" or "update-available")
            {
                var bytes = File.ReadAllBytes(PluginPathPolicy.Combine(packageDirectory, packageRelative, "PLUGIN_MODIFIED"));
                if (Hash(bytes) != status.PackageHash)
                    throw PluginErrors.Create("PLUGIN_MODIFIED", "Plugin package content changed after verification.");
                var path = Destination(status.Path);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new RLoopException("PLUGIN_SKILL_WRITE_FAILED",
                        "Could not write a plugin skill file. It was not recorded as distributed.", ExitCodes.OperationFailed,
                        new Dictionary<string, object?> { ["path"] = ".agents/skills/" + status.Path, ["updated"] = updated.ToArray() },
                        ["Fix the file or directory permission, then run resoloop skills sync --update again."], ex);
                }
                updated.Add(".agents/skills/" + status.Path);
            }
            else if (status.Status != "record-update-required") continue;
            record.Remove(status.Path);
            record[status.Path] = new RecordEntry(status.Plugin, status.PackageHash);
        }
        if (WriteRecord(record)) updated.Add(PluginSkillManager.RecordRelativePath);
        return updated;
    }

    /// <summary>Null when nothing is declared and no record exists, which keeps plugin-free output unchanged.</summary>
    public PluginSkillSyncResult? Result(string mode, IReadOnlyList<string> updated, bool applied)
    {
        if (_planned.Count == 0 && !_recordExists) return null;
        var files = _planned.Select(item => applied ? item.Status with
        {
            Status = "current", InstalledHash = item.Status.PackageHash,
            RecordedHash = item.Status.PackageHash, RecordedPlugin = item.Status.Plugin
        } : item.Status).ToArray();
        return new PluginSkillSyncResult(mode, files.All(file => file.Status == "current"), updated, files, Retained);
    }

    public void Dispose() => _lease.Dispose();

    private PluginSkillFileStatus Plan(string plugin, string skill, string target, string packageHash)
    {
        _record.TryGetValue(target, out var recorded);
        var status = new PluginSkillFileStatus(plugin, skill, target, "", packageHash, null, recorded.Entry?.Sha256, recorded.Entry?.Plugin);
        var path = Destination(target);
        if (!IsSafeDestination(path)) return status with { Status = "unsafe-path-conflict" };
        if (!File.Exists(path)) return status with { Status = "missing" };
        string installed;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            installed = Convert.ToHexStringLower(SHA256.HashData(stream));
        status = status with { InstalledHash = installed };
        var owned = recorded.Entry?.Plugin == plugin;
        if (installed == packageHash)
            return status with { Status = owned && recorded.Entry!.Sha256 == packageHash ? "current" : "record-update-required" };
        if (recorded.Entry is null) return status with { Status = "untracked-conflict" };
        if (!owned) return status with { Status = "owner-conflict" };
        return status with { Status = installed == recorded.Entry.Sha256 ? "update-available" : "modified-conflict" };
    }

    private string Destination(string target) => Path.Combine(_skillsRoot, target.Replace('/', Path.DirectorySeparatorChar));

    // No link or special file from the project root down, no file where a directory is needed, no directory at the file.
    private bool IsSafeDestination(string path)
    {
        try { PluginPathPolicy.EnsureNoLinks(path, Unsafe, allowMissing: true); }
        catch (RLoopException) { return false; }
        if (Directory.Exists(path)) return false;
        var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(_skillsRoot))!;
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length > projectRoot.Length; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) return false;
        return true;
    }

    private bool WriteRecord(Dictionary<string, RecordEntry> record)
    {
        if (record.Count == 0) return false;
        var document = new RecordDocument(1, new SortedDictionary<string, RecordEntry>(record, StringComparer.Ordinal));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, RecordJson).Concat("\n"u8.ToArray()).ToArray();
        PluginPathPolicy.EnsureNoLinks(_recordPath, Unsafe, allowMissing: true);
        if (_recordExists && File.ReadAllBytes(_recordPath).AsSpan().SequenceEqual(bytes)) return false;
        try
        {
            Directory.CreateDirectory(_skillsRoot);
            File.WriteAllBytes(_recordPath, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RLoopException("PLUGIN_SKILL_WRITE_FAILED", "Could not write the plugin skill record.", ExitCodes.OperationFailed,
                new Dictionary<string, object?> { ["path"] = PluginSkillManager.RecordRelativePath }, innerException: ex);
        }
        return true;
    }

    private static (Dictionary<string, (string Path, RecordEntry Entry)> Entries, bool Exists) ReadRecord(string path)
    {
        var entries = new Dictionary<string, (string Path, RecordEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        PluginPathPolicy.EnsureNoLinks(path, Unsafe, allowMissing: true);
        if (Directory.Exists(path)) throw Invalid("The plugin skill record is not a file.");
        if (!File.Exists(path)) return (entries, false);
        try
        {
            using var document = PluginJson.Read(path, MaximumRecordBytes, RecordInvalid);
            var duplicates = new List<string>();
            PluginJson.FindDuplicateProperties(document.RootElement, "$", duplicates);
            if (duplicates.Count != 0) throw Invalid("The plugin skill record has duplicate properties.");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasOnly(root, "schemaVersion", "files")
                || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                throw Invalid("Unsupported plugin skill record.");
            foreach (var file in files.EnumerateObject())
            {
                var value = file.Value;
                if (value.ValueKind != JsonValueKind.Object || !HasOnly(value, "plugin", "sha256")
                    || !value.TryGetProperty("plugin", out var plugin) || plugin.ValueKind != JsonValueKind.String
                    || !value.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String)
                    throw Invalid("Invalid plugin skill record entry.");
                PluginPathPolicy.ValidateRelativePath(file.Name, RecordInvalid);
                var parts = file.Name.Split('/');
                if (parts.Length < 2 || !PluginNames.IsValid(parts[0]) || !PluginNames.IsValid(plugin.GetString())
                    || !HashPattern.IsMatch(sha.GetString()!) || entries.ContainsKey(file.Name))
                    throw Invalid("Invalid plugin skill record entry.");
                entries.Add(file.Name, (file.Name, new RecordEntry(plugin.GetString()!, sha.GetString()!)));
            }
            return (entries, true);
        }
        catch (RLoopException ex) when (ex.Code == RecordInvalid && ex.ExitCode != ExitCodes.ValidationFailed)
        {
            throw new RLoopException(RecordInvalid, ex.Message, ExitCodes.ValidationFailed, ex.Context, innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RLoopException(RecordInvalid, "Could not read the plugin skill record.", ExitCodes.ValidationFailed, innerException: ex);
        }

        static bool HasOnly(JsonElement element, params string[] names) =>
            element.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal));
    }

    private static RLoopException Invalid(string message) => new(RecordInvalid, message, ExitCodes.ValidationFailed,
        suggestions: ["Restore .agents/skills/.resoloop-plugins.json from version control. resoloop never treats a broken record as empty."]);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record RecordEntry(string Plugin, string Sha256);
    private sealed record RecordDocument(int SchemaVersion, IReadOnlyDictionary<string, RecordEntry> Files);
    private sealed record Planned(PluginSkillFileStatus Status, string PackageDirectory, string PackageRelative);
}
