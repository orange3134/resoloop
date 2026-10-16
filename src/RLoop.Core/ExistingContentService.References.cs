using System.Text.Json;

namespace RLoop.Core;

public sealed partial class ExistingContentService
{
    private sealed record ReferenceWrite(string Key, ExistingReferenceEdge Edge, ExistingReferenceTarget? Target,
        MemberValue Before, string? TargetId, bool NoOp);
    private sealed class ReferenceReadCache
    {
        public Dictionary<string, SlotInfo> Slots { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ComponentInfo> Components { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Targets { get; } = new(StringComparer.Ordinal);
    }

    private static void ValidateReferenceEdits(ApplyDocument document, ExistingSnapshot snapshot)
    {
        if (document.Existing!.ReferenceChanges is not { Count: > 0 } changes) return;
        var graph = snapshot.References ?? throw Error("IMPORT_REFERENCE_GRAPH_REQUIRED", "Re-import into a new directory to capture P1 reference bindings. P0 snapshots remain readable.");
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in changes)
        {
            if (!graph.Edges.TryGetValue(edit.Key, out var edge) || !snapshot.Components.TryGetValue(edge.ComponentKey, out var component))
                throw Error("IMPORT_REFERENCE_KEY_NOT_FOUND", $"'{edit.Key}' is not an imported reference key.");
            var member = ExistingReferences.At(component.Data.Members, edge.Path);
            if (member.Kind != "reference") throw Error("IMPORT_REFERENCE_SOURCE_INVALID", "The source must be an observed reference.");
            if (!sources.Add(edge.ComponentKey + "/" + ExistingReferences.Display(edge.Path)))
                throw Error("IMPORT_REFERENCE_EDIT_DUPLICATE", "The same reference source was requested more than once.");
            if (document.Existing.Changes.TryGetValue(edge.ComponentKey, out var fields) && fields?.ContainsKey(edge.Path[0].Member!) == true)
                throw Error("IMPORT_REFERENCE_EDIT_OVERLAP", "Field changes and referenceChanges cannot both write the same top-level member.");
            ExistingReferences.ValidateTarget(snapshot, edit.Value);
        }
    }

    private async Task<IReadOnlyList<ReferenceWrite>> PrepareReferenceWritesAsync(ApplyDocument document, ExistingSnapshot snapshot,
        ExistingEditState state, IReadOnlyList<ApplyValidationIssue> warnings, CancellationToken ct)
    {
        var result = new List<ReferenceWrite>();
        // Cache only within one read-only preflight. Apply never reuses these observations.
        var cache = new ReferenceReadCache();
        foreach (var edit in document.Existing!.ReferenceChanges ?? new Dictionary<string, ExistingReferenceTarget?>())
        {
            var edge = snapshot.References!.Edges[edit.Key];
            var source = snapshot.Components[edge.ComponentKey];
            if (warnings.Any(w => w.Path == edge.ComponentKey || w.Path == source.SlotKey))
                throw Error("IMPORT_REFERENCE_BINDING_CHANGED", $"Source binding for '{edit.Key}' is not verified.");
            var current = await ReadOwnedComponentAsync(snapshot, edge.ComponentKey, ct, cache);
            var member = ExistingReferences.At(current.Members, edge.Path, source.Data.Members);
            var baseline = state.ReferenceBaselines.GetValueOrDefault(edit.Key) ?? ExistingReferences.At(source.Data.Members, edge.Path);
            var targetId = await ResolveReferenceTargetAsync(snapshot, edit.Value, member.TargetId, ct, cache);
            if (member.TargetId == targetId) { result.Add(new(edit.Key, edge, edit.Value, member, targetId, true)); continue; }
            if (member.Kind != "reference" || member.Id != baseline.Id || member.TargetType != baseline.TargetType || member.TargetId != baseline.TargetId)
                throw new RLoopException("IMPORT_REFERENCE_CONFLICT", $"'{edit.Key}' was changed in the world. No reference writes were sent.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["reference"] = edit.Key, ["baselineTargetId"] = baseline.TargetId, ["currentTargetId"] = member.TargetId, ["desiredTarget"] = edit.Value });
            await ValidateReferenceWriteAsync(snapshot, edge, edit.Value, current, targetId, ct);
            result.Add(new(edit.Key, edge, edit.Value, member, targetId, false));
        }
        return result;
    }

    private async Task ApplyReferenceWritesAsync(Prepared prepared, CancellationToken ct)
    {
        foreach (var write in prepared.ReferenceWrites)
        {
            var snapshot = prepared.Snapshot;
            var source = snapshot.Components[write.Edge.ComponentKey];
            var current = await ReadOwnedComponentAsync(snapshot, write.Edge.ComponentKey, ct);
            var actual = ExistingReferences.At(current.Members, write.Edge.Path, source.Data.Members);
            var targetId = await ResolveReferenceTargetAsync(snapshot, write.Target, actual.TargetId, ct);
            if (targetId != write.TargetId || actual.Kind != "reference" || actual.Id != write.Before.Id ||
                actual.TargetType != write.Before.TargetType || actual.TargetId != write.Before.TargetId)
                throw Error("IMPORT_REFERENCE_CONFLICT", $"'{write.Key}' source or target changed after preflight. No further writes were sent.");
            if (!write.NoOp)
            {
                await ValidateReferenceWriteAsync(snapshot, write.Edge, write.Target, current, targetId, ct);
                // Re-observe after Reflection/target validation; use only this current list payload.
                current = await ReadOwnedComponentAsync(snapshot, write.Edge.ComponentKey, ct);
                actual = ExistingReferences.At(current.Members, write.Edge.Path, source.Data.Members);
                if (actual.TargetId != write.Before.TargetId || actual.TargetType != write.Before.TargetType)
                    throw Error("IMPORT_REFERENCE_CONFLICT", $"'{write.Key}' changed immediately before its write.");
                if (await ResolveReferenceTargetAsync(snapshot, write.Target, actual.TargetId, ct) != targetId)
                    throw Error("IMPORT_REFERENCE_BINDING_CHANGED", $"'{write.Key}' target changed immediately before its write.");
                var raw = ExistingReferences.ReferencePayload(current.Members, source.Data.Members, write.Edge.Path, targetId);
                await client.SetComponentMemberAsync(current.Id, write.Edge.Path[0].Member!, raw, ct);
                var after = await client.GetComponentAsync(current.Id, ct);
                var result = ExistingReferences.At(after.Members, write.Edge.Path, source.Data.Members);
                if (result.Kind != "reference" || result.TargetId != targetId)
                    throw Error("IMPORT_REFERENCE_NOT_CONVERGED", $"'{write.Key}' did not retain the desired target; inspect before retrying.");
                // Do not claim convergence if the server unexpectedly changed neighboring references.
                var wanted = ExistingReferences.Display(write.Edge.Path);
                foreach (var member in ExistingReferences.Walk(current.Members).Where(m => m.Value.Kind == "reference" &&
                    m.Path[0].Member == write.Edge.Path[0].Member && ExistingReferences.Display(m.Path) != wanted))
                {
                    var retained = ExistingReferences.At(after.Members, member.Path, current.Members);
                    if (retained.TargetId != member.Value.TargetId)
                        throw Error("IMPORT_REFERENCE_NEIGHBOR_CHANGED", "A neighboring reference changed during the non-atomic update; inspect before retrying.");
                }
                actual = result;
            }
            prepared.State.ReferenceBaselines[write.Key] = actual;
            CheckpointFiles.Write(prepared.StatePath, JsonSerializer.Serialize(prepared.State, Json));
        }
    }

    private async Task ValidateReferenceWriteAsync(ExistingSnapshot snapshot, ExistingReferenceEdge edge,
        ExistingReferenceTarget? target, ComponentInfo current, string? targetId, CancellationToken ct)
    {
        var source = snapshot.Components[edge.ComponentKey];
        var definition = await client.DescribeComponentMemberPathAsync(source.Data.Type, edge.Path, ct);
        if (definition.Kind != "reference" || string.IsNullOrEmpty(definition.TargetType))
            throw Error("IMPORT_REFERENCE_REFLECTION_UNAVAILABLE", "The source path is not a reflected typed reference.");
        if (target is not null)
        {
            var actualType = await ReferenceTargetTypeAsync(snapshot, target, ct);
            if (actualType is null || !await IsAssignableAsync(actualType, definition.TargetType, ct))
                throw Error("IMPORT_REFERENCE_TYPE_MISMATCH", $"Cannot prove target assignability to '{definition.TargetType}'; no mutation is allowed.");
        }
        var raw = ExistingReferences.ReferencePayload(current.Members, source.Data.Members, edge.Path, targetId);
        await client.ValidateComponentMemberAsync(source.Data.Type, edge.Path[0].Member!, raw, ct);
    }

    private async Task<string?> ResolveReferenceTargetAsync(ExistingSnapshot snapshot, ExistingReferenceTarget? target,
        string? currentTargetId, CancellationToken ct, ReferenceReadCache? cache = null)
    {
        if (target is null) return null;
        var cacheKey = JsonSerializer.Serialize(target, Json);
        // Opaque dependencies are never cached as proof; each source must already reference them.
        if (cache?.Targets.TryGetValue(cacheKey, out var cached) == true) return cached;
        switch (target.Kind)
        {
            case "component": return (await ReadOwnedComponentAsync(snapshot, target.Key, ct, cache)).Id;
            case "member":
                var component = await ReadOwnedComponentAsync(snapshot, target.Key, ct, cache);
                return RequireMemberId(ExistingReferences.At(component.Members, target.Path!, snapshot.Components[target.Key].Data.Members));
            case "slot": return (await ReadOwnedSlotAsync(snapshot, target.Key, ct, cache)).Id;
            case "slotMember":
                var slot = await ReadOwnedSlotAsync(snapshot, target.Key, ct, cache);
                return RequireMemberId(ExistingReferences.At(slot.Members ?? new Dictionary<string, MemberValue>(), target.Path!, snapshot.Slots[target.Key].Members));
            case "external":
                var dependency = snapshot.References!.External[target.Key];
                if (dependency.Status == "component")
                {
                    var external = await client.GetComponentAsync(dependency.Id, ct);
                    if (external.Id == dependency.Id && external.Type == dependency.Type)
                    { if (cache is not null) cache.Targets[cacheKey] = external.Id; return external.Id; }
                }
                else if (dependency.Status == "slot")
                {
                    var external = await client.GetSlotAsync(dependency.Id, 0, false, ct);
                    if (external.Id == dependency.Id && external.Name == dependency.Name && external.ParentId == dependency.ParentId && !external.IsReferenceOnly)
                    { if (cache is not null) cache.Targets[cacheKey] = external.Id; return external.Id; }
                }
                else if (dependency.Status == "opaque" && currentTargetId == dependency.Id) return dependency.Id;
                throw Error("IMPORT_EXTERNAL_UNVERIFIED", $"Dependency '{target.Key}' lacks matching live identity/type. It remains outside ownership; no external writes are allowed.");
            default: throw Error("IMPORT_REFERENCE_TARGET_INVALID", "Invalid reference target.");
        }
    }

    private static string RequireMemberId(MemberValue member) => !string.IsNullOrEmpty(member.Id) ? member.Id
        : throw Error("IMPORT_REFERENCE_BINDING_CHANGED", "The target member has no observable instance ID.");

    private async Task<ComponentInfo> ReadOwnedComponentAsync(ExistingSnapshot snapshot, string key, CancellationToken ct, ReferenceReadCache? cache = null)
    {
        if (cache?.Components.TryGetValue(key, out var cached) == true) return cached;
        var expected = snapshot.Components[key];
        var slot = await ReadOwnedSlotAsync(snapshot, expected.SlotKey, ct, cache);
        if (!slot.Components.Any(c => c.Id == expected.Data.Id && c.Type == expected.Data.Type))
            throw Error("IMPORT_REFERENCE_BINDING_CHANGED", $"'{key}' no longer belongs to its verified Slot.");
        var component = await client.GetComponentAsync(expected.Data.Id, ct);
        if (component.Id != expected.Data.Id || component.Type != expected.Data.Type)
            throw Error("IMPORT_REFERENCE_BINDING_CHANGED", $"'{key}' identity/type changed.");
        if (cache is not null) cache.Components[key] = component;
        return component;
    }

    private async Task<SlotInfo> ReadOwnedSlotAsync(ExistingSnapshot snapshot, string key, CancellationToken ct, ReferenceReadCache? cache = null)
    {
        if (cache?.Slots.TryGetValue(key, out var cached) == true) return cached;
        var byId = snapshot.Slots.ToDictionary(s => s.Value.Id, s => s.Key, StringComparer.Ordinal);
        var expected = snapshot.Slots[key];
        var current = await client.GetSlotAsync(expected.Id, 0, false, ct);
        if (!BindingMatches(expected, current)) throw Error("IMPORT_REFERENCE_BINDING_CHANGED", $"Slot '{key}' identity/parent changed.");
        var parent = expected;
        while (parent.Id != snapshot.Slots[snapshot.RootKey].Id)
        {
            if (parent.ParentId is null || !byId.TryGetValue(parent.ParentId, out var parentKey))
                throw Error("IMPORT_REFERENCE_BINDING_CHANGED", "Target is not below the imported root.");
            if (cache?.Slots.ContainsKey(parentKey) == true) break;
            parent = snapshot.Slots[parentKey];
            var observedParent = await client.GetSlotAsync(parent.Id, 0, false, ct);
            if (!BindingMatches(parent, observedParent))
                throw Error("IMPORT_REFERENCE_BINDING_CHANGED", "An ancestor changed identity/parent.");
            if (cache is not null) cache.Slots[parentKey] = observedParent;
        }
        if (cache is not null) cache.Slots[key] = current;
        return current;
    }

    private async Task<string?> ReferenceTargetTypeAsync(ExistingSnapshot snapshot, ExistingReferenceTarget target, CancellationToken ct) => target.Kind switch
    {
        "component" => snapshot.Components[target.Key].Data.Type,
        "external" when snapshot.References!.External[target.Key].Status == "component" => snapshot.References.External[target.Key].Type,
        "slot" => "[FrooxEngine]FrooxEngine.Slot",
        "external" when snapshot.References!.External[target.Key].Status == "slot" => "[FrooxEngine]FrooxEngine.Slot",
        "member" => (await client.DescribeComponentMemberPathAsync(snapshot.Components[target.Key].Data.Type, target.Path!, ct)).MemberType,
        // Slot member definitions are not exposed by this adapter. Existing bindings can be retained,
        // but new assignments require explicit type evidence rather than guessing Sync<T> wrappers.
        _ => null
    };

    private async Task<bool> IsAssignableAsync(string actual, string accepted, CancellationToken ct)
    {
        var expected = await client.DescribeTypeAsync(accepted, ct);
        var pending = new Queue<string>(); pending.Enqueue(actual);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var type) && seen.Count < 64)
        {
            if (!seen.Add(type)) continue;
            if (type == accepted || type == expected.FullTypeName) return true;
            var description = await client.DescribeTypeAsync(type, ct);
            if (description.FullTypeName == expected.FullTypeName) return true;
            // Match exposed names before traversing: interface definitions need not be creatable types.
            if (description.Interfaces.Any(i => i == accepted || i == expected.FullTypeName) || description.BaseType == expected.FullTypeName) return true;
            foreach (var implemented in description.Interfaces) pending.Enqueue(implemented);
            if (description.BaseType is { } baseType) pending.Enqueue(baseType);
        }
        return false;
    }
}
