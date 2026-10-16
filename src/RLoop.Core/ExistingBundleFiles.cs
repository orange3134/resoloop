using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record ExistingSnapshotFile(string Path, string Sha256);
public sealed record ExistingSnapshotIndex(int SchemaVersion, string OwnershipKey, string WorldSessionId,
    string RootKey, DateTimeOffset ObservedAt, SessionInfo Connection, IReadOnlyList<ExistingSnapshotFile> Files);
public sealed record ExistingSnapshotPart(
    IReadOnlyDictionary<string, SlotInfo>? Slots = null,
    IReadOnlyDictionary<string, ExistingComponent>? Components = null,
    IReadOnlyDictionary<string, ExistingTypeCapability>? Types = null,
    IReadOnlyDictionary<string, ExistingReferenceEdge>? Edges = null,
    IReadOnlyDictionary<string, ExistingExternalDependency>? External = null);
public sealed record ExistingEditFile(int SchemaVersion, string OwnershipKey, string SnapshotSha256,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Changes = null,
    IReadOnlyDictionary<string, ExistingReferenceTarget?>? ReferenceChanges = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? SlotChanges = null);

/// <summary>One immutable snapshot identity and key namespace across files. No template expansion.</summary>
public static class ExistingBundleFiles
{
    private const int MaxFiles = 4096;
    private const long MaxBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 128, WriteIndented = true
    };
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    public static async Task<ExistingSnapshotIndex> WriteAsync(ExistingSnapshot snapshot, string directory, int slotsPerFile, CancellationToken ct)
    {
        if (slotsPerFile is < 1 or > 4096 || (snapshot.Slots.Count + slotsPerFile - 1) / slotsPerFile + 1 > MaxFiles)
            throw Error("IMPORT_FILE_LIMIT", "Invalid group size or too many snapshot parts; increase --split-slots.");
        var files = new List<ExistingSnapshotFile>();
        long bytes = 0;
        Directory.CreateDirectory(Path.Combine(directory, "snapshot"));
        async Task Write(string name, ExistingSnapshotPart part)
        {
            var text = JsonSerializer.Serialize(part, Json);
            bytes += System.Text.Encoding.UTF8.GetByteCount(text);
            if (bytes > MaxBytes) throw Error("IMPORT_FILE_LIMIT", "Snapshot parts exceed the 512 MiB bundle budget.");
            var path = "snapshot/" + name + ".json";
            await using var stream = new FileStream(Path.Combine(directory, path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(text.AsMemory(), ct);
            files.Add(new(path, Hash(text)));
        }
        // Group by owning Slot so each Component is defined exactly once. Edges may target any other part.
        var componentsBySlot = snapshot.Components.GroupBy(c => c.Value.SlotKey).ToDictionary(g => g.Key, g => g.ToArray());
        var edgesByComponent = snapshot.References!.Edges.GroupBy(e => e.Value.ComponentKey).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var batch in snapshot.Slots.Chunk(slotsPerFile))
        {
            var components = batch.SelectMany(s => componentsBySlot.GetValueOrDefault(s.Key) ?? []).ToDictionary();
            var edges = components.SelectMany(c => edgesByComponent.GetValueOrDefault(c.Key) ?? []).ToDictionary();
            await Write("part-" + files.Count.ToString("D4"), new(batch.ToDictionary(), components, Edges: edges));
        }
        await Write("dependencies", new(Types: snapshot.Types, External: snapshot.References.External));
        return new(3, snapshot.OwnershipKey, snapshot.WorldSessionId, snapshot.RootKey, snapshot.ObservedAt, snapshot.Connection, files);
    }

    public static ExistingSnapshot ReadSnapshot(string path, string text)
    {
        using var parsed = JsonDocument.Parse(text, new() { MaxDepth = 128 });
        if (parsed.RootElement.GetProperty("schemaVersion").GetInt32() != 3)
            return Read<ExistingSnapshot>(text);
        var index = Read<ExistingSnapshotIndex>(text);
        if (index.Files is null || index.Files.Count is < 1 or > MaxFiles) throw Error("IMPORT_FILE_LIMIT", "Invalid snapshot part count.");
        var slots = new Dictionary<string, SlotInfo>(StringComparer.Ordinal);
        var components = new Dictionary<string, ExistingComponent>(StringComparer.Ordinal);
        var types = new Dictionary<string, ExistingTypeCapability>(StringComparer.Ordinal);
        var edges = new Dictionary<string, ExistingReferenceEdge>(StringComparer.Ordinal);
        var external = new Dictionary<string, ExistingExternalDependency>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in index.Files)
        {
            var fileText = ReadFile(path, file.Path, seen, ref bytes);
            if (Hash(fileText) != file.Sha256) throw Error("IMPORT_SNAPSHOT_CHANGED", $"Snapshot part '{file.Path}' changed; restore it or re-import.");
            var part = Read<ExistingSnapshotPart>(fileText);
            Merge(slots, part.Slots); Merge(components, part.Components); Merge(types, part.Types);
            Merge(edges, part.Edges); Merge(external, part.External);
        }
        var snapshot = new ExistingSnapshot(2, index.OwnershipKey, index.WorldSessionId, index.RootKey, index.ObservedAt,
            index.Connection, slots, components, types, new(edges, external));
        // Resolve after every part is loaded, allowing forward and cyclic reference graphs (not file includes).
        foreach (var component in components.Values)
            if (!slots.TryGetValue(component.SlotKey, out var owner) || !types.ContainsKey(component.Data.Type) ||
                !owner.Components.Any(c => c.Id == component.Data.Id && c.Type == component.Data.Type))
                throw Error("IMPORT_FILE_REFERENCE_INVALID", "A Component's owner/type is not defined by this bundle.");
        foreach (var edge in edges.Values)
        {
            if (!components.TryGetValue(edge.ComponentKey, out var source) || ExistingReferences.At(source.Data.Members, edge.Path).Kind != "reference")
                throw Error("IMPORT_FILE_REFERENCE_INVALID", "A reference source is not defined by this bundle.");
            ExistingReferences.ValidateTarget(snapshot, edge.Target);
        }
        return snapshot;
    }

    public static ApplyDocument ExpandEdits(ApplyDocument document)
    {
        if (document.Existing?.EditFiles is not { Count: > 0 } files) return document;
        if (files.Count > MaxFiles || document.SourcePath is null) throw Error("IMPORT_FILE_LIMIT", "Edit files require a saved manifest and at most 4096 parts.");
        var spec = document.Existing;
        if (spec.Changes is null) throw Error("IMPORT_DOCUMENT_INVALID", "The manifest requires a changes object, even when edits are split into files.");
        var changes = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
        var slotChanges = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);
        var references = new Dictionary<string, ExistingReferenceTarget?>(StringComparer.Ordinal);
        MergeEdits(changes, spec.Changes); MergeEdits(slotChanges, spec.SlotChanges); Merge(references, spec.ReferenceChanges);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in files)
        {
            var part = Read<ExistingEditFile>(ReadFile(document.SourcePath, file, seen, ref bytes));
            if (part.SchemaVersion != 1 || part.OwnershipKey != document.Ownership?.Key || part.SnapshotSha256 != spec.Sha256)
                throw Error("IMPORT_FILE_IDENTITY_MISMATCH", $"Edit file '{file}' belongs to another snapshot/ownership boundary.");
            MergeEdits(changes, part.Changes); MergeEdits(slotChanges, part.SlotChanges); Merge(references, part.ReferenceChanges);
        }
        return document with { Existing = spec with { Changes = changes, SlotChanges = slotChanges, ReferenceChanges = references, EditFiles = null } };
    }

    private static void MergeEdits(Dictionary<string, IReadOnlyDictionary<string, JsonElement>> into,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? from)
    {
        foreach (var item in from ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>())
        {
            if (item.Value is null) throw Error("IMPORT_DOCUMENT_INVALID", "Edit values must be objects.");
            var fields = into.TryGetValue(item.Key, out var prior) ? prior.ToDictionary() : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            Merge(fields, item.Value); into[item.Key] = fields;
        }
    }
    private static void Merge<T>(Dictionary<string, T> into, IReadOnlyDictionary<string, T>? from)
    {
        foreach (var item in from ?? new Dictionary<string, T>())
            if (!into.TryAdd(item.Key, item.Value)) throw Error("IMPORT_FILE_DUPLICATE_KEY", $"Duplicate definition/edit '{item.Key}'; no file precedence is inferred.");
    }
    private static string ReadFile(string ownerPath, string relative, HashSet<string> seen, ref long bytes)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw Error("IMPORT_FILE_PATH_INVALID", "Bundle paths must be relative to their manifest/index directory.");
        var root = Path.GetDirectoryName(Path.GetFullPath(ownerPath))!;
        var path = Path.GetFullPath(relative, root);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Error("IMPORT_FILE_PATH_INVALID", "Bundle files must stay within their directory.");
        if (!seen.Add(path)) throw Error("IMPORT_FILE_DUPLICATE_KEY", "The same file is included twice.");
        if (!File.Exists(path)) throw Error("IMPORT_FILE_MISSING", $"Bundle file '{relative}' is missing.");
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Error("IMPORT_FILE_PATH_INVALID", "Bundle files cannot traverse symbolic links/junctions.");
        bytes += new FileInfo(path).Length;
        if (bytes > MaxBytes) throw Error("IMPORT_FILE_LIMIT", "Bundle parts exceed the 512 MiB read budget.");
        return File.ReadAllText(path);
    }
    private static T Read<T>(string text)
    {
        try
        {
            using var parsed = JsonDocument.Parse(text, new() { MaxDepth = 128 });
            void Check(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property: " + property.Name);
                        Check(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Check(child);
            }
            Check(parsed.RootElement);
            return JsonSerializer.Deserialize<T>(parsed.RootElement, Json) ?? throw new JsonException("Empty bundle file.");
        }
        catch (JsonException ex) { throw Error("IMPORT_DATA_INVALID", ex.Message); }
    }
    private static RLoopException Error(string code, string message) => new(code, message, ExitCodes.ValidationFailed);
}
