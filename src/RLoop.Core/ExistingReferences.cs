using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

/// <summary>Exactly one discriminator per step. Names/keys are literal; no dotted-path parsing.</summary>
public sealed record MemberPathStep(string? Member = null, int? Index = null, string? Key = null);
public sealed record ExistingReferenceTarget(string Kind, string Key, IReadOnlyList<MemberPathStep>? Path = null);
public sealed record ExistingReferenceEdge(string ComponentKey, IReadOnlyList<MemberPathStep> Path, ExistingReferenceTarget? Target);
public sealed record ExistingExternalDependency(string Id, string Status, string? Type = null,
    string? Name = null, string? ParentId = null, string? Diagnostic = null);
public sealed record ExistingReferenceGraph(
    IReadOnlyDictionary<string, ExistingReferenceEdge> Edges,
    IReadOnlyDictionary<string, ExistingExternalDependency> External);
public sealed record ExistingReferenceSummary(int References, int ExternalReferences, int ExternalTargets,
    int VerifiedExternalTargets, int OpaqueExternalTargets, int NestedMemberTargets);

public static class ExistingReferences
{
    public static void ValidatePath(IReadOnlyList<MemberPathStep>? path)
    {
        if (path is null || path.Count is < 1 or > 64 || path[0]?.Member is null || path.Any(p => p is null ||
            (p.Member is null ? 0 : 1) + (p.Index is null ? 0 : 1) + (p.Key is null ? 0 : 1) != 1 || p.Index < 0))
            throw Error("IMPORT_REFERENCE_PATH_INVALID", "A member path requires 1..64 literal member/index/key steps, beginning with a member.");
    }

    public static MemberValue At(IReadOnlyDictionary<string, MemberValue> members, IReadOnlyList<MemberPathStep> path,
        IReadOnlyDictionary<string, MemberValue>? baseline = null)
    {
        ValidatePath(path);
        MemberValue? current = null, expected = null;
        for (var i = 0; i < path.Count; i++)
        {
            current = Step(members, current, path[i], i == 0);
            if (baseline is null) continue;
            expected = Step(baseline, expected, path[i], i == 0);
            if (current.Id != expected.Id || current.Kind != expected.Kind || current.Type != expected.Type)
                throw Error("IMPORT_REFERENCE_BINDING_CHANGED", "A member/container identity changed at " + Display(path.Take(i + 1).ToArray()) + ". Re-import; never retarget by list position.");
        }
        return current!;
    }

    private static MemberValue Step(IReadOnlyDictionary<string, MemberValue> root, MemberValue? parent, MemberPathStep step, bool first)
    {
        MemberValue? value = null;
        if (first && step.Member is { } rootName) root.TryGetValue(rootName, out value);
        else if (step.Member is { } name && parent?.Kind == "syncObject") parent.Members?.TryGetValue(name, out value);
        else if (step.Key is { } key && parent?.Kind == "dictionary") parent.Members?.TryGetValue(key, out value);
        else if (step.Index is { } index && parent?.Kind == "list" && parent.Elements is { } items && index < items.Count) value = items[index];
        return value ?? throw Error("IMPORT_REFERENCE_PATH_MISSING", "The structured member path no longer exists or has a different container kind.");
    }

    public static IEnumerable<(IReadOnlyList<MemberPathStep> Path, MemberValue Value)> Walk(IReadOnlyDictionary<string, MemberValue> members)
    {
        foreach (var pair in members)
            foreach (var value in WalkMember(pair.Value, [new(Member: pair.Key)])) yield return value;
    }

    private static IEnumerable<(IReadOnlyList<MemberPathStep> Path, MemberValue Value)> WalkMember(MemberValue member, IReadOnlyList<MemberPathStep> path)
    {
        yield return (path, member);
        foreach (var child in member.Members ?? new Dictionary<string, MemberValue>())
            foreach (var nested in WalkMember(child.Value, path.Append(member.Kind == "dictionary" ? new(Key: child.Key) : new MemberPathStep(Member: child.Key)).ToArray())) yield return nested;
        for (var i = 0; i < (member.Elements?.Count ?? 0); i++)
            foreach (var nested in WalkMember(member.Elements![i], path.Append(new MemberPathStep(Index: i)).ToArray())) yield return nested;
    }

    public static async Task<ExistingReferenceGraph> CaptureAsync(IResoniteClient client,
        IReadOnlyDictionary<string, SlotInfo> slots, IReadOnlyDictionary<string, ExistingComponent> components,
        CancellationToken ct)
    {
        var index = new Dictionary<string, ExistingReferenceTarget>(StringComparer.Ordinal);
        void Add(string? id, ExistingReferenceTarget target)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!index.TryAdd(id, target)) throw Error("IMPORT_REFERENCE_ID_AMBIGUOUS", $"Observed element ID '{id}' has more than one owner/path.");
        }
        foreach (var slot in slots)
        {
            Add(slot.Value.Id, new("slot", slot.Key));
            foreach (var member in Walk(slot.Value.Members ?? new Dictionary<string, MemberValue>()))
                Add(member.Value.Id, new("slotMember", slot.Key, member.Path));
        }
        foreach (var component in components)
        {
            Add(component.Value.Data.Id, new("component", component.Key));
            foreach (var member in Walk(component.Value.Data.Members)) Add(member.Value.Id, new("member", component.Key, member.Path));
        }
        var edges = new Dictionary<string, ExistingReferenceEdge>(StringComparer.Ordinal);
        var external = new Dictionary<string, ExistingExternalDependency>(StringComparer.Ordinal);
        var externalKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in components)
            foreach (var member in Walk(component.Value.Data.Members).Where(m => m.Value.Kind == "reference"))
            {
                ExistingReferenceTarget? target = null;
                if (!string.IsNullOrEmpty(member.Value.TargetId))
                {
                    if (!index.TryGetValue(member.Value.TargetId, out target))
                    {
                        if (!externalKeys.TryGetValue(member.Value.TargetId, out var key))
                        {
                            key = "e" + external.Count.ToString("D6");
                            externalKeys.Add(member.Value.TargetId, key);
                            external.Add(key, await ObserveExternalAsync(client, member.Value.TargetId, ct));
                        }
                        target = new("external", key);
                    }
                }
                edges.Add("r" + edges.Count.ToString("D6"), new(component.Key, member.Path, target));
            }
        return new(edges, external);
    }

    private static async Task<ExistingExternalDependency> ObserveExternalAsync(IResoniteClient client, string id, CancellationToken ct)
    {
        try
        {
            var component = await client.GetComponentAsync(id, ct);
            if (component.Id == id && !string.IsNullOrWhiteSpace(component.Type)) return new(id, "component", component.Type);
        }
        catch (RLoopException ex) when (ex.Code == "COMPONENT_NOT_FOUND") { }
        try
        {
            var slot = await client.GetSlotAsync(id, 0, false, ct);
            if (slot.Id == id && !slot.IsReferenceOnly) return new(id, "slot", Name: slot.Name, ParentId: slot.ParentId);
        }
        catch (RLoopException ex) when (ex.Code == "SLOT_NOT_FOUND") { }
        return new(id, "opaque", Diagnostic: "Referenced outside ownership, but not readable as a Component/Slot. Kept as a dependency; cannot be assigned to a changed reference without owner/type evidence.");
    }

    public static ExistingReferenceSummary Summarize(ExistingReferenceGraph graph) => new(graph.Edges.Count,
        graph.Edges.Count(e => e.Value.Target?.Kind == "external"), graph.External.Count,
        graph.External.Count(e => e.Value.Status is "component" or "slot"), graph.External.Count(e => e.Value.Status == "opaque"),
        graph.Edges.Count(e => e.Value.Target?.Path?.Count > 1));

    public static void ValidateTarget(ExistingSnapshot snapshot, ExistingReferenceTarget? target)
    {
        if (target is null) return;
        if (string.IsNullOrEmpty(target.Key)) throw Error("IMPORT_REFERENCE_TARGET_INVALID", "A target requires a bundle-local key.");
        switch (target.Kind)
        {
            case "component" when snapshot.Components.ContainsKey(target.Key): break;
            case "slot" when snapshot.Slots.ContainsKey(target.Key): break;
            case "external" when snapshot.References?.External.ContainsKey(target.Key) == true: break;
            case "member" when snapshot.Components.TryGetValue(target.Key, out var component):
                ValidatePath(target.Path); _ = At(component.Data.Members, target.Path!); return;
            case "slotMember" when snapshot.Slots.TryGetValue(target.Key, out var slot):
                ValidatePath(target.Path); _ = At(slot.Members ?? new Dictionary<string, MemberValue>(), target.Path!); return;
            default: throw Error("IMPORT_REFERENCE_TARGET_INVALID", "Unknown target kind/key. Raw IDs and arbitrary external objects are not accepted.");
        }
        if (target.Path is not null) throw Error("IMPORT_REFERENCE_TARGET_INVALID", "Only member/slotMember targets accept a structured path.");
    }

    /// <summary>Build a partial top-level payload. Reference lists retain every observed element and ID order.</summary>
    public static string ReferencePayload(IReadOnlyDictionary<string, MemberValue> current, IReadOnlyDictionary<string, MemberValue> baseline,
        IReadOnlyList<MemberPathStep> path, string? targetId)
    {
        var member = At(current, path, baseline);
        if (member.Kind != "reference") throw Error("IMPORT_REFERENCE_SOURCE_INVALID", "Only an observed reference may be changed.");
        if (path.Count == 1) return targetId ?? "null";
        JsonNode? Build(MemberValue value, MemberValue original, int offset)
        {
            if (offset == path.Count) return JsonValue.Create(targetId);
            var step = path[offset];
            if (step.Member is { } name && value.Kind == "syncObject")
                return new JsonObject { [name] = Build(value.Members![name], original.Members![name], offset + 1) };
            if (step.Index is { } index && value.Kind == "list" && offset == path.Count - 1 &&
                value.Elements is { } items && original.Elements is { } saved && items.Count == saved.Count &&
                items.Zip(saved).All(pair => pair.First.Kind == "reference" && pair.Second.Kind == "reference" && pair.First.Id == pair.Second.Id))
                return new JsonArray(items.Select((item, i) => (JsonNode?)JsonValue.Create(i == index ? targetId : item.TargetId)).ToArray());
            throw Error("IMPORT_REFERENCE_SOURCE_UNSUPPORTED", "Updates support direct references, partial SyncObjects and lists of references with unchanged element identities. Other containers remain observed; no recreation or list resize is allowed.");
        }
        return Build(current[path[0].Member!], baseline[path[0].Member!], 1)!.ToJsonString();
    }

    public static string Display(IReadOnlyList<MemberPathStep> path) => JsonSerializer.Serialize(path,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    internal static RLoopException Error(string code, string message) => new(code, message, ExitCodes.ValidationFailed);
}
