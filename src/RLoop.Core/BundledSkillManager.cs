using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Core.Plugins;

namespace RLoop.Core;

public sealed record SkillSyncEntry(string Name, string Path, string Status, string BundledHash,
    string? InstalledHash, string? LockedHash);

/// <summary>Plugins is null unless a plugin-aware sync found declared plugin skills or a plugin skill record.</summary>
public sealed record SkillSyncResult(string RootDirectory, string Mode, bool Synchronized,
    IReadOnlyList<string> Updated, IReadOnlyList<SkillSyncEntry> Skills, PluginSkillSyncResult? Plugins = null);

public static class BundledSkillManager
{
    public const string LockRelativePath = ".agents/skills/.resoloop-bundled.json";

    public static IReadOnlyList<string> Names { get; } =
        ["resonite-build", "resonite-debug", "resonite-flux", "resonite-inspect", "resonite-blender", "resonite-uix"];

    // Only these packaged paths are writable. Never use paths supplied by the installed lock.
    public static IReadOnlyList<string> Files { get; } = Names.Select(name => name + "/SKILL.md")
        .Concat(["resonite-uix/references/assets.md", "resonite-uix/references/layout.md",
            "resonite-uix/references/interaction-and-migration.md", "resonite-uix/references/recipes.md",
            "resonite-uix/references/control-verification.md"])
        .Concat(UixRecipes.Catalog.Select(recipe => $"resonite-uix/recipes/{recipe.Name}.json")).ToArray();

    public static SkillSyncResult Sync(string targetDirectory, bool update) => Sync(targetDirectory, update, plugins: null);

    /// <summary>
    /// Also distributes the skills of the verified plugins installed in exactly this project. Every bundled and
    /// plugin conflict is checked before anything is written, and the plugin lease is held until writing ends.
    /// </summary>
    public static SkillSyncResult Sync(string targetDirectory, bool update, string resoloopVersion, PluginNameReservations reservations)
    {
        using var plugins = PluginSkillManager.Open(targetDirectory, resoloopVersion, reservations);
        return Sync(targetDirectory, update, plugins);
    }

    private static SkillSyncResult Sync(string targetDirectory, bool update, PluginSkillPlan? plugins)
    {
        var root = Path.GetFullPath(targetDirectory);
        var lockPath = Path.Combine(root, LockRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var locked = LoadLock(lockPath);
        var entries = new List<SkillSyncEntry>();
        foreach (var file in Files)
        {
            var name = file.EndsWith("/SKILL.md", StringComparison.Ordinal) ? file[..^9] : file;
            var relativePath = $".agents/skills/{file}";
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var bundledHash = Hash(LoadBundledFile(file));
            var installedHash = File.Exists(path) ? Hash(File.ReadAllText(path)) : null;
            locked.TryGetValue(file, out var lockedHash);
            var status = installedHash is null ? "missing" : installedHash == bundledHash ?
                lockedHash == bundledHash ? "current" : "lock-update-required" :
                lockedHash is null ? "untracked-conflict" : installedHash == lockedHash ? "update-available" : "modified-conflict";
            entries.Add(new SkillSyncEntry(name, relativePath, status, bundledHash, installedHash, lockedHash));
        }

        var conflicts = entries.Where(entry => entry.Status.EndsWith("conflict", StringComparison.Ordinal)).ToArray();
        var pluginConflicts = plugins?.Conflicts(init: false) ?? [];
        if (conflicts.Length > 0 || pluginConflicts.Count > 0)
        {
            var context = new Dictionary<string, object?> { ["rootDirectory"] = root, ["conflicts"] = conflicts };
            if (pluginConflicts.Count > 0) context["pluginConflicts"] = pluginConflicts;
            throw new RLoopException("SKILL_SYNC_CONFLICT", pluginConflicts.Count == 0
                    ? "Bundled skill sync stopped because one or more installed skills are modified or have no trusted lock."
                    : "Skill sync stopped because one or more bundled or plugin skills are modified, unowned or unsafe.",
                ExitCodes.ValidationFailed, context,
                ["Reconcile or back up the listed skills. resoloop never overwrites an unverified user-edited skill."]);
        }

        var pending = entries.Where(entry => entry.Status is "missing" or "update-available").ToArray();
        if (!update)
            return new SkillSyncResult(root, "check", pending.Length == 0 && entries.All(entry => entry.Status == "current")
                && (plugins?.Synchronized ?? true), [], entries, plugins?.Result("check", [], applied: false));

        var updated = new List<string>();
        foreach (var entry in pending)
        {
            var path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, NormalizeLineEndings(LoadBundledFile(entry.Path[".agents/skills/".Length..])), new UTF8Encoding(false));
            updated.Add(entry.Path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var skillHashes = Files.ToDictionary(file => file, file => Hash(LoadBundledFile(file)), StringComparer.Ordinal);
        var serializedLock = SerializeLock(skillHashes);
        if (!File.Exists(lockPath) || !string.Equals(
                NormalizeLineEndings(File.ReadAllText(lockPath)),
                NormalizeLineEndings(serializedLock),
                StringComparison.Ordinal))
        {
            File.WriteAllText(lockPath, NormalizeLineEndings(serializedLock), new UTF8Encoding(false));
            updated.Add(LockRelativePath);
        }
        var pluginUpdated = plugins?.Apply(init: false) ?? [];
        updated.AddRange(pluginUpdated);
        var finalEntries = entries.Select(entry => entry with
        {
            Status = "current",
            InstalledHash = entry.BundledHash,
            LockedHash = entry.BundledHash
        }).ToArray();
        return new SkillSyncResult(root, "update", true, updated, finalEntries,
            plugins?.Result("update", pluginUpdated, applied: true));
    }

    internal static string LoadBundledFile(string file)
    {
        var resourceName = $"RLoop.Core.Skills.{file.Replace('/', '.')}";
        using var stream = typeof(BundledSkillManager).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled skill resource was not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static string SerializeLock(IReadOnlyDictionary<string, string> hashes) =>
        JsonSerializer.Serialize(new BundledSkillLock(2, null, hashes), JsonOptions) + "\n";

    internal static string Hash(string content) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(NormalizeLineEndings(content))));

    private static string NormalizeLineEndings(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private static Dictionary<string, string> LoadLock(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var value = JsonSerializer.Deserialize<BundledSkillLock>(File.ReadAllText(path), JsonOptions);
            if (value is null || value.SchemaVersion is not (1 or 2))
                throw new JsonException("Unsupported or empty bundled skill lock.");
            var hashes = value.SchemaVersion == 1
                ? value.Skills?.ToDictionary(pair => pair.Key + "/SKILL.md", pair => pair.Value, StringComparer.Ordinal)
                : value.Files;
            if (hashes is null || hashes.Any(pair => string.IsNullOrWhiteSpace(pair.Value)))
                throw new JsonException("Missing or invalid file hashes.");
            return new Dictionary<string, string>(hashes, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new RLoopException("SKILL_LOCK_INVALID", $"Cannot read bundled skill lock '{path}': {ex.Message}",
                ExitCodes.ValidationFailed, innerException: ex);
        }
    }

    private sealed record BundledSkillLock(int SchemaVersion, IReadOnlyDictionary<string, string>? Skills,
        IReadOnlyDictionary<string, string>? Files = null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}
