using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RLoop.Core;

public sealed record SkillSyncEntry(string Name, string Path, string Status, string BundledHash,
    string? InstalledHash, string? LockedHash);

public sealed record SkillSyncResult(string RootDirectory, string Mode, bool Synchronized,
    IReadOnlyList<string> Updated, IReadOnlyList<SkillSyncEntry> Skills);

public static class BundledSkillManager
{
    public const string LockRelativePath = ".agents/skills/.resoloop-bundled.json";

    public static IReadOnlyList<string> Names { get; } =
        ["resonite-build", "resonite-debug", "resonite-flux", "resonite-inspect"];

    public static SkillSyncResult Sync(string targetDirectory, bool update)
    {
        var root = Path.GetFullPath(targetDirectory);
        var lockPath = Path.Combine(root, LockRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var locked = LoadLock(lockPath);
        var entries = new List<SkillSyncEntry>();
        foreach (var name in Names)
        {
            var relativePath = $".agents/skills/{name}/SKILL.md";
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var bundledHash = Hash(LoadBundledSkill(name));
            var installedHash = File.Exists(path) ? Hash(File.ReadAllText(path)) : null;
            locked.TryGetValue(name, out var lockedHash);
            var status = installedHash is null ? "missing" : installedHash == bundledHash ?
                lockedHash == bundledHash ? "current" : "lock-update-required" :
                lockedHash is null ? "untracked-conflict" : installedHash == lockedHash ? "update-available" : "modified-conflict";
            entries.Add(new SkillSyncEntry(name, relativePath, status, bundledHash, installedHash, lockedHash));
        }

        var conflicts = entries.Where(entry => entry.Status.EndsWith("conflict", StringComparison.Ordinal)).ToArray();
        if (conflicts.Length > 0)
            throw new RLoopException("SKILL_SYNC_CONFLICT",
                "Bundled skill sync stopped because one or more installed skills are modified or have no trusted lock.",
                ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["rootDirectory"] = root, ["conflicts"] = conflicts },
                ["Reconcile or back up the listed skills. resoloop never overwrites an unverified user-edited skill."]);

        var pending = entries.Where(entry => entry.Status is "missing" or "update-available").ToArray();
        if (!update)
            return new SkillSyncResult(root, "check", pending.Length == 0 && entries.All(entry => entry.Status == "current"),
                [], entries);

        var updated = new List<string>();
        foreach (var entry in pending)
        {
            var path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, LoadBundledSkill(entry.Name), new UTF8Encoding(false));
            updated.Add(entry.Path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var skillHashes = Names.ToDictionary(name => name, name => Hash(LoadBundledSkill(name)), StringComparer.Ordinal);
        var serializedLock = SerializeLock(skillHashes);
        if (!File.Exists(lockPath) || !string.Equals(File.ReadAllText(lockPath), serializedLock, StringComparison.Ordinal))
        {
            File.WriteAllText(lockPath, serializedLock, new UTF8Encoding(false));
            updated.Add(LockRelativePath);
        }
        var finalEntries = entries.Select(entry => entry with
        {
            Status = "current",
            InstalledHash = entry.BundledHash,
            LockedHash = entry.BundledHash
        }).ToArray();
        return new SkillSyncResult(root, "update", true, updated, finalEntries);
    }

    internal static string LoadBundledSkill(string skillName)
    {
        var resourceName = $"RLoop.Core.Skills.{skillName}.SKILL.md";
        using var stream = typeof(BundledSkillManager).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled skill resource was not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static string SerializeLock(IReadOnlyDictionary<string, string> hashes) =>
        JsonSerializer.Serialize(new BundledSkillLock(1, hashes), JsonOptions) + "\n";

    internal static string Hash(string content) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static Dictionary<string, string> LoadLock(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var value = JsonSerializer.Deserialize<BundledSkillLock>(File.ReadAllText(path), JsonOptions);
            if (value is null || value.SchemaVersion != 1)
                throw new JsonException("Unsupported or empty bundled skill lock.");
            return new Dictionary<string, string>(value.Skills, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new RLoopException("SKILL_LOCK_INVALID", $"Cannot read bundled skill lock '{path}': {ex.Message}",
                ExitCodes.ValidationFailed, innerException: ex);
        }
    }

    private sealed record BundledSkillLock(int SchemaVersion, IReadOnlyDictionary<string, string> Skills);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
