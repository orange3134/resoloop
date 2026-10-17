using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record BundleSource(string File, string Pointer, string SnapshotSha256, DateTimeOffset ObservedAt, string Origin);
public sealed record BundleAddress(string? Key, string Id, IReadOnlyList<MemberPathStep>? Path = null);
public sealed record BundleEdge(BundleAddress Source, BundleAddress? Target, string Status, BundleSource Evidence);
public sealed record BundlePage(string SnapshotSha256, int Total, int Offset, int? NextOffset, int Omitted,
    string? LimitReason, IReadOnlyList<JsonElement> Items);
public sealed record BundleEntity(string Key, string Kind, string Id, string? Name, string? Type, string? SlotKey,
    string Status, BundleSource Source, SlotInfo? Slot = null, ComponentInfo? Component = null)
{
    public IReadOnlyDictionary<string, MemberValue> Members => Component?.Members ?? Slot?.Members ?? new Dictionary<string, MemberValue>();
}

/// <summary>Offline, immutable observations. No client is constructed by this reader.</summary>
public sealed class BundleAnalysis
{
    // Explicit nulls in projected JSON distinguish null/unknown from absent or false.
    public static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 128, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public ApplyDocument Document { get; }
    public ExistingSnapshot Snapshot { get; }
    public string Hash => Document.Existing!.Sha256;
    public string DirectoryPath { get; }
    public Dictionary<string, BundleEntity> Entities { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<BundleEdge> Edges => edges;
    public DependencyCapture? Dependencies { get; }
    private readonly Dictionary<string, BundleAddress> addresses = new(StringComparer.Ordinal);
    private readonly List<BundleEdge> edges = [];
    private readonly Dictionary<string, List<BundleEdge>> incoming = new(StringComparer.Ordinal), outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> slotKeys = new(StringComparer.Ordinal);

    public static BundleAnalysis Load(string path, string? dependencies = null) => new(path, dependencies);
    private BundleAnalysis(string path, string? dependencies)
    {
        if (Directory.Exists(path)) path = Path.Combine(path, "manifest.json");
        Document = ApplyDocument.Load(path);
        Snapshot = ExistingContentService.LoadSnapshot(Document);
        DirectoryPath = Path.GetDirectoryName(Document.SourcePath!)!;
        var snapshotPath = Path.GetFullPath(Document.Existing!.Snapshot, DirectoryPath);
        var sources = new Dictionary<string, (string File, string Pointer)>();
        void Index(string file)
        {
            using var data = JsonDocument.Parse(File.ReadAllText(file), new() { MaxDepth = 128 });
            foreach (var category in new[] { "slots", "components", "external" })
                if (data.RootElement.TryGetProperty(category, out var values))
                    foreach (var entry in values.EnumerateObject()) sources[entry.Name] = (file, "/" + category + "/" + Escape(entry.Name));
        }
        using (var data = JsonDocument.Parse(File.ReadAllText(snapshotPath)))
        {
            if (data.RootElement.GetProperty("schemaVersion").GetInt32() == 3)
                foreach (var file in data.RootElement.GetProperty("files").EnumerateArray())
                    Index(Path.GetFullPath(file.GetProperty("path").GetString()!, Path.GetDirectoryName(snapshotPath)!));
            else Index(snapshotPath);
        }
        BundleSource Source(string key, string category)
        {
            var value = sources.GetValueOrDefault(key, (snapshotPath, "/references/" + category + "/" + Escape(key)));
            return new(value.Item1, value.Item2, Hash, Snapshot.ObservedAt, "snapshot");
        }
        foreach (var (key, slot) in Snapshot.Slots)
        {
            if (!slotKeys.TryAdd(slot.Id, key)) throw Error("BUNDLE_ID_AMBIGUOUS", "Duplicate Slot ID.");
            Entities.Add(key, new(key, "slot", slot.Id, slot.Name, null, null, "captured", Source(key, "slots"), Slot: slot));
        }
        foreach (var (key, component) in Snapshot.Components)
            Entities.Add(key, new(key, "component", component.Data.Id, null, component.Data.Type, component.SlotKey,
                "captured", Source(key, "components"), Component: component.Data));
        foreach (var (key, dependency) in Snapshot.References?.External ?? new Dictionary<string, ExistingExternalDependency>())
            Entities.Add(key, new(key, "external", dependency.Id, dependency.Name, dependency.Type, null,
                "identity-only", Source(key, "external")));
        var explicitDependencies = dependencies is not null;
        dependencies ??= Path.Combine(DirectoryPath, "dependencies.capture.json");
        if (File.Exists(dependencies))
        {
            Dependencies = DependencyCollector.Read(dependencies, this);
            foreach (var record in Dependencies.Records)
            {
                var prior = Entities.Values.SingleOrDefault(e => e.Id == record.Id);
                var key = prior?.Key ?? record.Key;
                if (prior is not null && prior.Kind != "external" || prior is null && Entities.ContainsKey(key))
                    throw Error("BUNDLE_DEPENDENCY_INVALID", "Dependency cannot redefine an owned entity or another key.");
                Entities[key] = new(key, "external", record.Id, record.Slot?.Name, record.Component?.Type, null, record.Status,
                    new(Path.GetFullPath(dependencies), "/records/" + Dependencies.Records.IndexOf(record), Hash, record.ObservedAt, "dependency"), record.Slot, record.Component);
            }
        }
        else if (explicitDependencies)
            throw Error("BUNDLE_DEPENDENCY_MISSING", "The explicit dependency file was not found.");
        foreach (var entity in Entities.Values)
        {
            AddAddress(entity.Id, new(entity.Key, entity.Id));
            foreach (var member in ExistingReferences.Walk(entity.Members))
                if (member.Value.Id is { Length: > 0 } id) AddAddress(id, new(entity.Key, id, member.Path));
        }
        foreach (var entity in Entities.Values.OrderBy(e => e.Key, StringComparer.Ordinal))
            foreach (var (memberPath, value) in ExistingReferences.Walk(entity.Members).Where(v => v.Value.Kind == "reference"))
            {
                BundleAddress? target = string.IsNullOrEmpty(value.TargetId) ? null :
                    addresses.GetValueOrDefault(value.TargetId) ?? new(null, value.TargetId);
                var status = target is null ? "null" : target.Key is null ? "unresolved" : Entities[target.Key].Status;
                var edge = new BundleEdge(new(entity.Key, value.Id ?? entity.Id, memberPath), target, status, MemberSource(entity, memberPath));
                edges.Add(edge);
                AddEdge(outgoing, entity.Key, edge);
                if (target?.Key is { } targetKey) AddEdge(incoming, targetKey, edge);
            }
    }

    private void AddAddress(string id, BundleAddress address)
    {
        if (addresses.TryGetValue(id, out var prior) && prior.Path is null && address.Path is not null && Get(prior.Key!).Kind == "external" && Get(prior.Key!).Component is null && Get(prior.Key!).Slot is null)
        { addresses[id] = address; return; }
        if (addresses.TryGetValue(id, out prior) && prior.Path is not null && address.Path is null && Get(address.Key!).Kind == "external" && Get(address.Key!).Component is null && Get(address.Key!).Slot is null) return;
        if (!addresses.TryAdd(id, address)) throw Error("BUNDLE_ID_AMBIGUOUS", $"Observed ID '{id}' has multiple owners.");
    }
    private static void AddEdge(Dictionary<string, List<BundleEdge>> index, string key, BundleEdge edge)
    { if (!index.TryGetValue(key, out var list)) index[key] = list = []; list.Add(edge); }
    public BundleEntity Get(string key) => Entities.GetValueOrDefault(key) ?? throw Error("BUNDLE_KEY_NOT_FOUND", $"Unknown bundle key '{key}'.");
    public BundleAddress? Resolve(string id) => addresses.GetValueOrDefault(id);
    public IReadOnlyList<string> PathOf(string key)
    {
        var entity = Get(key);
        var slotKey = entity.SlotKey ?? (entity.Kind == "slot" ? entity.Key : null);
        var path = new List<string>(); var seen = new HashSet<string>();
        while (slotKey is not null)
        {
            if (!seen.Add(slotKey)) throw Error("BUNDLE_HIERARCHY_CYCLE", "Slot ancestors contain a cycle.");
            var slot = Get(slotKey).Slot!; path.Add(slot.Name);
            slotKey = slot.ParentId is { } parent ? slotKeys.GetValueOrDefault(parent) : null;
        }
        path.Reverse(); return path;
    }
    public string? ParentKey(BundleEntity entity) => entity.Slot?.ParentId is { } id ? slotKeys.GetValueOrDefault(id) : entity.SlotKey;
    public bool IsUnder(BundleEntity entity, string key)
    {
        var current = entity.Kind == "component" ? entity.SlotKey : entity.Key; var seen = new HashSet<string>();
        while (current is not null)
        {
            if (!seen.Add(current)) throw Error("BUNDLE_HIERARCHY_CYCLE", "Slot ancestors contain a cycle.");
            if (current == key) return true;
            current = ParentKey(Get(current));
        }
        return false;
    }
    public JsonElement Describe(BundleEntity e) => Element(new
    {
        e.Key, e.Kind, e.Id, e.Name, e.Type, e.Status, parentKey = ParentKey(e), path = PathOf(e.Key),
        isActive = e.Slot?.IsActive, enabled = e.Members.GetValueOrDefault("Enabled"), e.Source
    });
    public object Summary() => new
    {
        snapshotSha256 = Hash, Snapshot.ObservedAt, Snapshot.WorldSessionId, root = Describe(Get(Snapshot.RootKey)),
        slots = Snapshot.Slots.Count, components = Snapshot.Components.Count, references = edges.Count,
        external = Entities.Values.Count(e => e.Kind == "external"),
        dependencyStatuses = Entities.Values.Where(e => e.Kind == "external").GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()),
        unresolvedReferences = edges.Count(e => e.Status == "unresolved"),
        unavailableReferenceTargets = edges.Count(e => e.Target is not null && e.Status != "captured"),
        reflectionUnavailableTypes = Snapshot.Types.Count(t => t.Value.Definition is null),
        dependencies = Dependencies is null ? null : new { Dependencies.StartedAt, Dependencies.CompletedAt, Dependencies.StopReason, Dependencies.Limits },
        note = "Captured observations, not live state or an atomic snapshot. Edits in the manifest are not overlaid. Name matches are not semantic bindings."
    };
    public IEnumerable<JsonElement> Find(string? name = null, string? type = null, string? text = null, string? under = null)
    {
        if (under is not null) _ = Get(under);
        foreach (var e in Entities.Values.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (under is not null && !IsUnder(e, under) || name is not null && !(e.Name ?? "").Contains(name, StringComparison.OrdinalIgnoreCase)
                || type is not null && !(e.Type ?? "").Contains(type, StringComparison.OrdinalIgnoreCase)) continue;
            if (text is null) { yield return Describe(e); continue; }
            foreach (var (path, member) in ExistingReferences.Walk(e.Members))
                if (path.Any(p => (p.Member ?? p.Key ?? "").Contains(text, StringComparison.OrdinalIgnoreCase)) ||
                    (member.Kind == "field" || member.Value is not null) && (member.Value?.ToJsonString() ?? "null").Contains(text, StringComparison.OrdinalIgnoreCase))
                    yield return Element(new { entity = Describe(e), memberPath = path, member.Kind, member.Type, member.Value, source = MemberSource(e, path) });
        }
    }
    public IEnumerable<JsonElement> Tree(string? under, int depth)
    {
        var root = Get(under ?? Snapshot.RootKey);
        if (root.Kind != "slot") throw Error("BUNDLE_SLOT_REQUIRED", "Tree root must be an owned Slot key.");
        var baseDepth = PathOf(root.Key).Count;
        return Entities.Values.Where(e => e.Kind == "slot" && IsUnder(e, root.Key) && PathOf(e.Key).Count - baseDepth <= depth)
            .OrderBy(e => e.Key, StringComparer.Ordinal).Select(Describe);
    }
    public IEnumerable<JsonElement> Inspect(string key, IReadOnlyList<MemberPathStep>? path = null)
    {
        var e = Get(key);
        if (path is not null)
        {
            var value = ExistingReferences.At(e.Members, path);
            yield return Element(new { entity = Describe(e), memberPath = path, value, source = MemberSource(e, path) });
            yield break;
        }
        yield return Element(new { entity = Describe(e), slot = e.Slot is null ? null : e.Slot with { Members = null, Children = [], Components = [] },
            memberCount = e.Members.Count, note = "Following rows contain every top-level member; use --member/--member-path for large containers." });
        foreach (var (name, value) in e.Members)
            yield return Element(new { key, memberPath = new[] { new MemberPathStep(Member: name) }, value, source = MemberSource(e, [new(Member: name)]) });
        var capability = e.Type is null ? null : Snapshot.Types.GetValueOrDefault(e.Type) ?? Dependencies?.Types.GetValueOrDefault(e.Type);
        if (capability is not null) yield return Element(new { key, typeCapability = capability });
    }
    public IEnumerable<JsonElement> References(string key, string direction, int depth, IReadOnlyList<MemberPathStep>? path = null)
    {
        _ = Get(key);
        if (direction is not ("in" or "out" or "both")) throw Error("BUNDLE_DIRECTION_INVALID", "direction must be in, out or both.");
        string? memberId = path is null ? null : ExistingReferences.At(Get(key).Members, path).Id;
        var queue = new Queue<(string Key, int Depth)>(); queue.Enqueue((key, 0));
        var visited = new HashSet<string> { key }; var emitted = new HashSet<BundleEdge>();
        while (queue.TryDequeue(out var current))
        {
            if (current.Depth >= depth) continue;
            var adjacent = (direction != "in" ? outgoing.GetValueOrDefault(current.Key) ?? [] : Enumerable.Empty<BundleEdge>())
                .Concat(direction != "out" ? incoming.GetValueOrDefault(current.Key) ?? [] : []);
            foreach (var edge in adjacent)
            {
                if (current.Depth == 0 && path is not null && !(edge.Source.Key == key && edge.Source.Id == memberId || edge.Target?.Key == key && edge.Target.Id == memberId)) continue;
                if (emitted.Add(edge)) yield return Element(new { depth = current.Depth + 1, edge, source = Describe(Get(edge.Source.Key!)),
                    target = edge.Target?.Key is { } target ? Describe(Get(target)) : (JsonElement?)null });
                var next = edge.Source.Key == current.Key ? edge.Target?.Key : edge.Source.Key;
                if (next is not null && visited.Add(next)) queue.Enqueue((next, current.Depth + 1));
            }
        }
    }
    public BundlePage Page(IEnumerable<JsonElement> rows, int offset = 0, int limit = 50, int maxBytes = 65536)
    {
        if (offset < 0 || limit is < 1 or > 2000 || maxBytes is < 1024 or > 16 * 1024 * 1024)
            throw Error("BUNDLE_LIMIT_INVALID", "offset >= 0, limit 1..2000, max-bytes 1024..16777216 required.");
        var items = new List<JsonElement>(); var count = 0; var bytes = 0; string? reason = null;
        foreach (var row in rows)
        {
            var index = count++;
            if (index < offset || reason is not null) continue;
            if (items.Count == limit) { reason = "items"; continue; }
            var size = System.Text.Encoding.UTF8.GetByteCount(row.GetRawText());
            if (bytes + size > maxBytes)
            {
                if (items.Count == 0) throw Error("BUNDLE_ROW_TOO_LARGE", $"Row at offset {offset} requires {size} bytes. Select --member/--member-path or increase --max-bytes.");
                reason = "bytes"; continue;
            }
            items.Add(row); bytes += size;
        }
        return new(Hash, count, offset, offset + items.Count < count ? offset + items.Count : null,
            Math.Max(0, count - offset - items.Count), reason, items);
    }
    public object Extract(string under, bool includeDependencies, int depth, int maxObjects, int maxBytes, string output)
    {
        if (depth is < 0 or > 16 || maxObjects is < 1 or > 10000 || maxBytes is < 1024 or > 256 * 1024 * 1024)
            throw Error("BUNDLE_LIMIT_INVALID", "Invalid extraction depth/object/byte budget.");
        var root = Get(under);
        if (root.Kind != "slot") throw Error("BUNDLE_SLOT_REQUIRED", "Extract requires an owned Slot key.");
        var selected = Entities.Values.Where(e => e.Kind != "external" && IsUnder(e, under)).Select(e => e.Key).ToHashSet();
        var ancestors = new HashSet<string>(); var parent = ParentKey(root);
        while (parent is not null && ancestors.Add(parent)) parent = ParentKey(Get(parent));
        selected.UnionWith(ancestors);
        if (selected.Count > maxObjects) throw Error("BUNDLE_EXTRACT_LIMIT", "Selection exceeds --max-objects; narrow --under.");
        if (includeDependencies)
        {
            var frontier = selected.ToArray();
            for (var level = 0; level < depth && frontier.Length > 0; level++)
            {
                var next = new List<string>();
                foreach (var key in frontier)
                    foreach (var edge in outgoing.GetValueOrDefault(key) ?? [])
                        if (edge.Target?.Key is { } target && !IsParentEdge(edge) && selected.Add(target)) next.Add(target);
                frontier = next.ToArray();
                if (selected.Count > maxObjects) throw Error("BUNDLE_EXTRACT_LIMIT", "Selection exceeds --max-objects; narrow --under/depth or raise the limit.");
            }
        }
        // Preserve owner ancestry of referenced Components, without importing ancestor descendants.
        foreach (var key in selected.ToArray())
        {
            var seen = new HashSet<string>(); parent = ParentKey(Get(key));
            while (parent is not null && seen.Add(parent)) { if (selected.Add(parent)) ancestors.Add(parent); parent = ParentKey(Get(parent)); }
        }
        if (selected.Count > maxObjects) throw Error("BUNDLE_EXTRACT_LIMIT", "Selection exceeds --max-objects.");
        var links = edges.Where(e => selected.Contains(e.Source.Key!)).ToArray();
        var usedTypes = selected.Select(k => Get(k).Type).Where(t => t is not null).Distinct(StringComparer.Ordinal)
            .ToDictionary(t => t!, t => Snapshot.Types.GetValueOrDefault(t!) ?? Dependencies?.Types.GetValueOrDefault(t!));
        var result = new { schemaVersion = 1, kind = "analysis-excerpt", snapshotSha256 = Hash, Snapshot.ObservedAt,
            rootKey = under, dependencyDepth = includeDependencies ? depth : 0,
            entities = selected.Order(StringComparer.Ordinal).Select(k => new { entity = Describe(Get(k)), role = ancestors.Contains(k) ? "ancestor" : "selected-or-dependency",
                slot = Get(k).Slot is { } slot ? slot with { Children = [], Components = slot.Components.Where(c => addresses.TryGetValue(c.Id, out var a) && a.Key is not null && selected.Contains(a.Key)).ToArray() } : null,
                component = Get(k).Component }).ToArray(), types = usedTypes, references = links,
            unresolved = links.Where(e => e.Target is not null && (e.Target.Key is null || !selected.Contains(e.Target.Key) || e.Status != "captured")).ToArray(),
            note = "Analysis data only, not an apply manifest. External observations have independent timestamps. Asset bytes and inferred behavior are not included." };
        var text = JsonSerializer.Serialize(result, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > maxBytes) throw Error("BUNDLE_EXTRACT_LIMIT", "Excerpt exceeds --max-bytes; narrow selection or raise the limit.");
        WriteNew(output, text);
        return new { output = Path.GetFullPath(output), entities = selected.Count, references = links.Length, unresolved = result.unresolved.Length, snapshotSha256 = Hash };
    }
    internal bool IsParentEdge(BundleEdge edge) => Get(edge.Source.Key!).Slot is not null && edge.Source.Path is [{ Member: "Parent" }];
    internal static BundleSource MemberSource(BundleEntity entity, IReadOnlyList<MemberPathStep> path) => entity.Source with
    { Pointer = entity.Source.Pointer + (entity.Source.Origin == "dependency" ? entity.Component is not null ? "/component" : "/slot" : entity.Component is not null ? "/data" : "") + MemberPointer(path) };
    internal static string MemberPointer(IReadOnlyList<MemberPathStep> path) => string.Concat(path.Select(p =>
        p.Index is { } i ? "/elements/" + i : "/members/" + Escape(p.Member ?? p.Key!)));
    internal static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
    internal static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
    internal static void WriteNew(string path, string text)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)); writer.Write(text);
    }
    internal static RLoopException Error(string code, string message) => new(code, message, ExitCodes.ValidationFailed);
}
