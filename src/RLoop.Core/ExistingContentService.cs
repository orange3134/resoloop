using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RLoop.Core;

/// <summary>Literal, explicit edits to an observed existing graph. Empty edit sets preserve live values.</summary>
public sealed record ExistingContentSpec(string Snapshot, string Sha256,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Changes,
    IReadOnlyDictionary<string, ExistingReferenceTarget?>? ReferenceChanges = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? SlotChanges = null,
    IReadOnlyList<string>? EditFiles = null);
public sealed record ExistingComponent(string SlotKey, ComponentInfo Data);
public sealed record ExistingTypeCapability(ComponentTypeInfo? Definition, string? ErrorCode, string? Error);
public sealed record ExistingSnapshot(int SchemaVersion, string OwnershipKey, string WorldSessionId,
    string RootKey, DateTimeOffset ObservedAt, SessionInfo Connection,
    IReadOnlyDictionary<string, SlotInfo> Slots,
    IReadOnlyDictionary<string, ExistingComponent> Components,
    IReadOnlyDictionary<string, ExistingTypeCapability> Types,
    ExistingReferenceGraph? References = null);
public sealed record ExistingImportReport(string Manifest, string Snapshot, string StateFile,
    int Slots, int Components, int DuplicateSiblingGroups, int Types, int ReflectionUnavailableTypes,
    IReadOnlyDictionary<string, int> ObservationOnlyReasons, int ExternalReferences,
    int WorldMutations, string IdentityPolicy, ClientMetrics? Metrics,
    ExistingReferenceSummary? References = null);

internal sealed class ExistingEditState
{
    public int SchemaVersion { get; set; } = 1;
    public string SnapshotHash { get; set; } = "";
    public Dictionary<string, Dictionary<string, MemberValue>> Baselines { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MemberValue> ReferenceBaselines { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, MemberValue>> SlotBaselines { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Import never calls mutation APIs. Existing graphs never enter create/recreate/prune planning.
/// Edits are explicit fields/references/Slot attributes; unsupported members remain observed only.
/// </summary>
public sealed partial class ExistingContentService(IResoniteClient client)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 128, WriteIndented = true
    };

    public async Task<ExistingImportReport> ImportAsync(string selector, string outputDirectory,
        int maxSlots = 50_000, Action<ApplyProgress>? progress = null, CancellationToken ct = default, int splitSlots = 0)
    {
        if (maxSlots is < 1 or > 250_000) throw Error("IMPORT_LIMIT_INVALID", "maxSlots must be 1..250000.");
        if (splitSlots is < 0 or > 4096) throw Error("IMPORT_LIMIT_INVALID", "splitSlots must be 0..4096 (0 means a single snapshot file).");
        var directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw Error("IMPORT_OUTPUT_EXISTS", "Import requires a new output directory; re-import into a new directory to preserve previous observations and pending edits.");
        if (client is IResoniteClientDiagnostics diagnostics) diagnostics.ResetMetrics();
        var session = await client.GetSessionInfoAsync(ct);
        var worldSession = await RequireWorldSessionAsync(ct);
        var rootId = await new WorldService(client).ResolveSlotIdAsync(selector, ct);
        if (rootId == "Root") throw Error("IMPORT_ROOT_FORBIDDEN", "Choose an exact content Slot below Root.");
        var observed = await ObserveAsync(rootId, true, maxSlots, progress, ct);
        var slots = new Dictionary<string, SlotInfo>(StringComparer.Ordinal);
        var components = new Dictionary<string, ExistingComponent>(StringComparer.Ordinal);
        foreach (var slot in observed.Values)
        {
            var key = "s" + slots.Count.ToString("D6");
            slots.Add(key, slot with { Children = [], Components = slot.Components.Select(c => c with { Members = null }).ToArray() });
            foreach (var component in slot.Components)
            {
                var data = component.Members is null ? await client.GetComponentAsync(component.Id, ct)
                    : new ComponentInfo(component.Id, component.Type, component.Members);
                components.Add("c" + components.Count.ToString("D6"), new(key, data));
            }
        }
        var types = new Dictionary<string, ExistingTypeCapability>(StringComparer.Ordinal);
        var allTypes = components.Values.Select(c => c.Data.Type).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var type in allTypes)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(new("import-reflection", types.Count, allTypes.Length, type, "Reading type capability; unavailable types remain observed."));
            try { types.Add(type, new(await client.DescribeComponentTypeAsync(type, ct), null, null)); }
            catch (RLoopException ex) when (ex.ExitCode != ExitCodes.ConnectionFailed && ex.ExitCode != ExitCodes.Timeout)
            { types.Add(type, new(null, ex.Code, ex.Message)); }
        }
        var references = await ExistingReferences.CaptureAsync(client, slots, components, ct);
        var snapshot = new ExistingSnapshot(2, "existing-" + Guid.NewGuid().ToString("N"), worldSession,
            slots.Single(s => s.Value.Id == rootId).Key, DateTimeOffset.UtcNow, session, slots, components, types, references);
        // A second structural observation detects additions/removals during the non-atomic capture.
        var after = await ObserveAsync(rootId, false, maxSlots, progress, ct);
        if (!SameStructure(observed, after) || await RequireWorldSessionAsync(ct) != worldSession)
            throw Error("IMPORT_CHANGED_DURING_READ", "Hierarchy or world session changed during import. Retry into a new directory.");
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var ownedIds = new HashSet<string>(StringComparer.Ordinal);
        void Collect(MemberValue member)
        {
            if (member.Id is not null) ownedIds.Add(member.Id);
            foreach (var child in member.Members?.Values ?? []) Collect(child);
            foreach (var child in member.Elements ?? []) Collect(child);
        }
        foreach (var slot in slots.Values) { ownedIds.Add(slot.Id); foreach (var m in slot.Members?.Values ?? []) Collect(m); }
        foreach (var component in components.Values) { ownedIds.Add(component.Data.Id); foreach (var m in component.Data.Members.Values) Collect(m); }
        int External(MemberValue member) => (member.Kind == "reference" && !string.IsNullOrEmpty(member.TargetId) && !ownedIds.Contains(member.TargetId) ? 1 : 0)
            + (member.Members?.Values.Sum(External) ?? 0) + (member.Elements?.Sum(External) ?? 0);
        foreach (var component in components.Values)
            foreach (var member in component.Data.Members)
            {
                var reason = ObservationReason(types[component.Data.Type].Definition, member.Key, member.Value);
                if (reason is not null) reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            }
        Directory.CreateDirectory(directory);
        var snapshotText = splitSlots == 0 ? JsonSerializer.Serialize(snapshot, Json)
            : JsonSerializer.Serialize(await ExistingBundleFiles.WriteAsync(snapshot, directory, splitSlots, ct), Json);
        var hash = Hash(snapshotText);
        var manifest = new ApplyDocument("1", new(snapshot.OwnershipKey), null, null,
            Origin: "existing", Existing: new("snapshot.json", hash, new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(),
                new Dictionary<string, ExistingReferenceTarget?>(), new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(), []));
        var report = new ExistingImportReport(Path.Combine(directory, "manifest.json"), Path.Combine(directory, "snapshot.json"),
            Path.Combine(directory, "state.json"), slots.Count, components.Count,
            slots.Values.GroupBy(s => (s.ParentId, s.Name)).Count(g => g.Count() > 1), types.Count,
            types.Count(t => t.Value.Definition is null), reasons,
            components.Values.Sum(c => c.Data.Members.Values.Sum(External)), 0,
            "Same announced world session plus freshly observed IDs, parents and types. Never match duplicate names or ordinals; re-import after session change.",
            (client as IResoniteClientDiagnostics)?.SnapshotMetrics(), ExistingReferences.Summarize(references));
        // Publish the manifest last: cancellation leaves no usable partial import.
        Directory.CreateDirectory(directory);
        await WriteNewAsync(report.Snapshot, snapshotText, ct);
        await WriteNewAsync(report.StateFile, JsonSerializer.Serialize(new ExistingEditState { SnapshotHash = hash }, Json), ct);
        await WriteNewAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, Json), ct);
        await WriteNewAsync(report.Manifest, JsonSerializer.Serialize(manifest, Json), ct);
        return report;
    }

    public static async Task<ApplyValidationResult> ValidateAsync(ApplyDocument document, IResoniteClient? client = null,
        CancellationToken ct = default)
    {
        document = ExistingBundleFiles.ExpandEdits(document);
        var snapshot = LoadSnapshot(document);
        ValidateReferenceEdits(document, snapshot);
        ValidateSlotEdits(document, snapshot);
        foreach (var change in document.Existing!.Changes)
        {
            if (change.Value is null) throw Error("IMPORT_DOCUMENT_INVALID", $"Changes for '{change.Key}' must be an object of member values.");
            if (!snapshot.Components.TryGetValue(change.Key, out var component))
                throw Error("IMPORT_KEY_NOT_FOUND", $"Unknown imported Component key '{change.Key}'.");
            ComponentTypeInfo? definition = snapshot.Types.GetValueOrDefault(component.Data.Type)?.Definition;
            if (client is not null && change.Value.Count > 0)
            {
                try { definition = await client.DescribeComponentTypeAsync(component.Data.Type, ct); }
                catch (RLoopException ex) when (ex.ExitCode != ExitCodes.ConnectionFailed && ex.ExitCode != ExitCodes.Timeout)
                { throw new RLoopException("IMPORT_MEMBER_OBSERVATION_ONLY", $"Reflection is unavailable for '{change.Key}': {ex.Message}", ExitCodes.ValidationFailed, innerException: ex); }
            }
            foreach (var field in change.Value)
            {
                if (!component.Data.Members.TryGetValue(field.Key, out var member) || ObservationReason(definition, field.Key, member) is { })
                    throw Error("IMPORT_MEMBER_OBSERVATION_ONLY", $"'{change.Key}.{field.Key}' is not a verified direct field. Use referenceChanges for supported references; other containers/unknown members remain observation-only.");
                if (client is not null) await client.ValidateComponentMemberAsync(component.Data.Type, field.Key, Raw(field.Value), ct);
            }
        }
        return new(true, document.SchemaVersion, snapshot.Slots.Count, snapshot.Components.Count, 0, client is not null, []);
    }

    public async Task<ApplyPlanResult> PlanAsync(ApplyDocument document, ApplyOptions options, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(document, options, ct);
        var changed = prepared.Writes.Select(w => w.Key).Concat(prepared.ReferenceWrites.Where(w => !w.NoOp).Select(w => w.Edge.ComponentKey)).ToHashSet(StringComparer.Ordinal);
        var changedSlots = prepared.SlotWrites.Where(w => !w.NoOp).Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        var operations = prepared.Snapshot.Slots.Select(s => new ApplyPlanEntry(changedSlots.Contains(s.Key) ? "update" : "no-op", "slot", s.Value.Name, s.Key,
            Members: prepared.SlotWrites.Where(w => !w.NoOp && w.Key == s.Key).Select(w => w.Attribute).ToArray(),
            Reason: changedSlots.Contains(s.Key) ? "Explicit Slot attribute edits; baseline matches current value." : "No requested Slot change; live attributes preserved."))
            .Concat(prepared.Snapshot.Components.Select(c => new ApplyPlanEntry(changed.Contains(c.Key) ? "update" : "no-op",
                "component", c.Key, c.Key, c.Value.Data.Type,
                prepared.Writes.Where(w => w.Key == c.Key).Select(w => w.Member)
                    .Concat(prepared.ReferenceWrites.Where(w => !w.NoOp && w.Edge.ComponentKey == c.Key).Select(w => ExistingReferences.Display(w.Edge.Path))).ToArray(),
                changed.Contains(c.Key) ? "Explicit field/reference edits; baseline matches current value." : "No requested change; live values preserved."))).ToArray();
        return new(true, "1", document.Ownership!.Key, prepared.StatePath, prepared.Session.UniqueSessionId,
            operations, 0, changed.Count + changedSlots.Count, operations.Length - changed.Count - changedSlots.Count,
            Recovery: "Non-atomic. Re-run the same edits after interruption. Conflicts require inspection; no recreation or pruning.")
        { Warnings = prepared.Warnings };
    }

    public async Task<ApplyResult> ApplyAsync(ApplyDocument document, ApplyOptions options, CancellationToken ct = default)
    {
        var statePath = StatePath(document, options);
        using var writer = CheckpointFiles.AcquireWriter(statePath);
        var prepared = await PrepareAsync(document, options, ct);
        var ancestors = prepared.Snapshot.Slots.Values.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var write in prepared.Writes)
        {
            // Recheck ownership and the edited value immediately before the mutation.
            var ancestor = write.Slot;
            while (ancestor.ParentId is { } parentId && ancestors.TryGetValue(parentId, out var parent))
            {
                if (!BindingMatches(parent, await client.GetSlotAsync(parent.Id, 0, false, ct)))
                    throw Error("IMPORT_BINDING_UNVERIFIED", $"Ancestor identity changed before writing '{write.Key}.{write.Member}'.");
                ancestor = parent;
            }
            var slot = await client.GetSlotAsync(write.Slot.Id, 0, false, ct);
            if (!BindingMatches(write.Slot, slot) || !slot.Components.Any(c => c.Id == write.Component.Id && c.Type == write.Component.Type))
                throw Error("IMPORT_BINDING_UNVERIFIED", $"Binding changed before writing '{write.Key}.{write.Member}'.");
            var current = await client.GetComponentAsync(write.Component.Id, ct);
            if (!current.Members.TryGetValue(write.Member, out var actual) || !SameValue(actual, write.Before))
                throw Error("IMPORT_EDIT_CONFLICT", $"'{write.Key}.{write.Member}' changed after preflight. No further fields were written.");
            await client.SetComponentMemberAsync(write.Component.Id, write.Member, write.Raw, ct);
            var observed = await client.GetComponentAsync(write.Component.Id, ct);
            if (!observed.Members.TryGetValue(write.Member, out var result) || !Matches(result, write.Raw))
                throw Error("IMPORT_WRITE_NOT_CONVERGED", $"'{write.Key}.{write.Member}' did not retain the requested value; inspect before retrying.");
            SetBaseline(prepared.State, write.Key, write.Member, result);
            CheckpointFiles.Write(statePath, JsonSerializer.Serialize(prepared.State, Json));
        }
        // Also checkpoint already-converged edits (including retries after a lost write response).
        foreach (var edit in prepared.Converged) SetBaseline(prepared.State, edit.Key, edit.Member, edit.Value);
        CheckpointFiles.Write(statePath, JsonSerializer.Serialize(prepared.State, Json));
        await ApplyReferenceWritesAsync(prepared, ct);
        await ApplySlotWritesAsync(prepared, ct);
        var slotsUpdated = prepared.SlotWrites.Where(w => !w.NoOp).Select(w => w.Key).Distinct().Count();
        var updated = prepared.Writes.Select(w => w.Key).Concat(prepared.ReferenceWrites.Where(w => !w.NoOp).Select(w => w.Edge.ComponentKey)).Distinct().Count();
        return new(prepared.Snapshot.Slots[prepared.Snapshot.RootKey].Id, false, 0, updated,
            SlotsUpdated: slotsUpdated, SlotsUnchanged: prepared.Snapshot.Slots.Count - slotsUpdated, ComponentsUnchanged: prepared.Snapshot.Components.Count - updated,
            StateFile: statePath, SessionId: prepared.Session.UniqueSessionId,
            Recovery: "Only explicit fields/references/Slot attributes are written. Preserve snapshot and state; re-run the same edits after interruption.");
    }

    private async Task<Prepared> PrepareAsync(ApplyDocument document, ApplyOptions options, CancellationToken ct)
    {
        document = ExistingBundleFiles.ExpandEdits(document);
        if (options.Adopt || options.Prune) throw Error("IMPORT_LIFECYCLE_UNSUPPORTED", "Imported content cannot be adopted, pruned, created or recreated. Use explicit field/reference edits only.");
        var snapshot = LoadSnapshot(document);
        var statePath = StatePath(document, options);
        var state = File.Exists(statePath) ? Deserialize<ExistingEditState>(CheckpointFiles.Read(statePath))
            : throw Error("IMPORT_STATE_MISSING", "Import state is missing; restore it or explicitly re-import into a new directory.");
        if (state.SchemaVersion != 1 || state.SnapshotHash != document.Existing!.Sha256 || state.Baselines is null || state.ReferenceBaselines is null || state.SlotBaselines is null)
            throw Error("IMPORT_STATE_MISMATCH", "State does not belong to this immutable snapshot.");
        if (await RequireWorldSessionAsync(ct) != snapshot.WorldSessionId)
            throw Error("IMPORT_SESSION_CHANGED", "World session changed. Imported IDs cannot be reused; inspect and re-import into a new directory. Names/ordinals are never used to guess identity.");
        var session = await client.GetSessionInfoAsync(ct);
        var live = await ObserveAsync(snapshot.Slots[snapshot.RootKey].Id, false, Math.Max(50_000, snapshot.Slots.Count * 2), options.Progress, ct);
        var warnings = new List<ApplyValidationIssue>();
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var byId = snapshot.Slots.ToDictionary(s => s.Value.Id, s => s.Key, StringComparer.Ordinal);
        bool Verify(string key)
        {
            if (verified.Contains(key)) return true;
            var slot = snapshot.Slots[key];
            if (!live.TryGetValue(slot.Id, out var current) || !BindingMatches(slot, current)) return false;
            if (key != snapshot.RootKey && (slot.ParentId is null || !byId.TryGetValue(slot.ParentId, out var parent) || !Verify(parent))) return false;
            verified.Add(key); return true;
        }
        foreach (var slot in snapshot.Slots)
            if (!Verify(slot.Key)) warnings.Add(new("IMPORT_BINDING_UNVERIFIED", "Slot identity/parent changed or is missing; edits to it are blocked, with no recreation.", slot.Key, "warning"));
        foreach (var component in snapshot.Components)
            if (!verified.Contains(component.Value.SlotKey) || !live[snapshot.Slots[component.Value.SlotKey].Id].Components.Any(c => c.Id == component.Value.Data.Id && c.Type == component.Value.Data.Type))
                warnings.Add(new("IMPORT_BINDING_UNVERIFIED", "Component is missing or changed; edits are blocked, with no recreation.", component.Key, "warning"));
        var unavailable = snapshot.Types.Count(t => t.Value.Definition is null);
        if (unavailable > 0) warnings.Add(new("IMPORT_OBSERVATION_ONLY_TYPES", $"{unavailable} types lack Reflection; their observed data is retained in the snapshot. See report.json.", "existing", "warning"));
        if (snapshot.References is { } referenceGraph && referenceGraph.External.Any(e => e.Value.Status == "opaque"))
            warnings.Add(new("IMPORT_OPAQUE_DEPENDENCIES", $"{referenceGraph.External.Count(e => e.Value.Status == "opaque")} external targets lack readable owner/type evidence; retained without ownership or reassignment.", "existing.references", "warning"));
        await ValidateAsync(document, client, ct);
        var writes = new List<Write>();
        var converged = new List<Converged>();
        foreach (var edit in document.Existing!.Changes)
        {
            if (edit.Value.Count == 0) continue;
            var component = snapshot.Components[edit.Key];
            if (warnings.Any(w => w.Path == edit.Key || w.Path == component.SlotKey))
                throw Error("IMPORT_BINDING_UNVERIFIED", $"'{edit.Key}' cannot be identified safely. Inspect/re-import; never guess by name or ordinal.");
            var current = await client.GetComponentAsync(component.Data.Id, ct);
            foreach (var field in edit.Value)
            {
                var baseline = state.Baselines.GetValueOrDefault(edit.Key)?.GetValueOrDefault(field.Key) ?? component.Data.Members[field.Key];
                if (!current.Members.TryGetValue(field.Key, out var actual) || actual.Kind != "field" || actual.Id != baseline.Id || actual.Type != baseline.Type)
                    throw Error("IMPORT_BINDING_UNVERIFIED", $"Member identity changed for '{edit.Key}.{field.Key}'.");
                var raw = Raw(field.Value);
                if (Matches(actual, raw)) { converged.Add(new(edit.Key, field.Key, actual)); continue; }
                if (!SameValue(actual, baseline))
                    throw new RLoopException("IMPORT_EDIT_CONFLICT", $"'{edit.Key}.{field.Key}' changed in the world since its baseline. Re-import after inspecting both versions.", ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["key"] = edit.Key, ["member"] = field.Key, ["baseline"] = baseline.Value, ["current"] = actual.Value, ["desired"] = field.Value });
                writes.Add(new(edit.Key, field.Key, component.Data, snapshot.Slots[component.SlotKey], actual, raw));
            }
        }
        var references = await PrepareReferenceWritesAsync(document, snapshot, state, warnings, ct);
        var slotWrites = await PrepareSlotWritesAsync(document, snapshot, state, ct);
        return new(snapshot, statePath, state, session, writes, converged, warnings, references, slotWrites);
    }

    private async Task<Dictionary<string, SlotInfo>> ObserveAsync(string rootId, bool members, int maxSlots,
        Action<ApplyProgress>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, SlotInfo>(StringComparer.Ordinal);
        var pending = new Queue<string>(); pending.Enqueue(rootId);
        while (pending.TryDequeue(out var id))
        {
            ct.ThrowIfCancellationRequested();
            var batch = await client.GetSlotAsync(id, 4, members, ct);
            if (batch.Id != id || batch.IsReferenceOnly) throw Error("IMPORT_INCOMPLETE", $"Slot '{id}' could not be fully observed.");
            void Visit(SlotInfo slot)
            {
                if (slot.IsReferenceOnly) { pending.Enqueue(slot.Id); return; }
                if (!result.TryAdd(slot.Id, slot)) throw Error("IMPORT_INCONSISTENT", "Hierarchy contains a repeated Slot ID; retry observation.");
                if (result.Count > maxSlots) throw Error("IMPORT_SLOT_LIMIT", $"Observation exceeded {maxSlots} Slots; inspect scope before increasing --max-slots.");
                foreach (var child in slot.Children) Visit(child);
            }
            Visit(batch);
            progress?.Invoke(new("import-observe", result.Count, maxSlots, rootId, "Reading the exact subtree in bounded depth batches."));
        }
        return result;
    }

    private static ExistingSnapshot LoadSnapshot(ApplyDocument document)
    {
        if (document.SchemaVersion != "1" || document.Origin != "existing" || document.Existing is null ||
            string.IsNullOrWhiteSpace(document.Ownership?.Key) || document.SourcePath is null ||
            string.IsNullOrWhiteSpace(document.Existing.Snapshot) || string.IsNullOrWhiteSpace(document.Existing.Sha256) ||
            document.Slot is not null || document.Components is not null || document.Children is not null ||
            document.Assets is not null || document.Tests is not null || document.Cameras is not null || document.Existing.Changes is null)
            throw Error("IMPORT_DOCUMENT_INVALID", "Existing documents require schemaVersion 1, origin existing, ownership and existing; generated Slot/Component declarations cannot be mixed in.");
        var path = Path.GetFullPath(document.Existing.Snapshot, Path.GetDirectoryName(document.SourcePath)!);
        if (!File.Exists(path)) throw Error("IMPORT_SNAPSHOT_MISSING", $"Snapshot '{path}' was not found.");
        var text = File.ReadAllText(path);
        if (Hash(text) != document.Existing.Sha256) throw Error("IMPORT_SNAPSHOT_CHANGED", "Observation snapshot changed. Restore the original or re-import; put edits only in changes/referenceChanges/slotChanges or editFiles.");
        var snapshot = ExistingBundleFiles.ReadSnapshot(path, text);
        if (snapshot.SchemaVersion is not (1 or 2) || snapshot.SchemaVersion == 2 && snapshot.References is null || snapshot.OwnershipKey != document.Ownership.Key ||
            !snapshot.Slots.TryGetValue(snapshot.RootKey, out var root) || root.Id == "Root" || string.IsNullOrWhiteSpace(snapshot.WorldSessionId))
            throw Error("IMPORT_SNAPSHOT_INVALID", "Snapshot identity or schema is invalid.");
        return snapshot;
    }

    private async Task<string> RequireWorldSessionAsync(CancellationToken ct) =>
        await client.GetWorldSessionIdAsync(ct) is { Length: > 0 } id ? id :
            throw Error("IMPORT_WORLD_IDENTITY_UNAVAILABLE", "A unique world-session announcement matching the connected endpoint is required. Connection IDs are not world identity; check UDP discovery.");
    internal static string StatePath(ApplyDocument document, ApplyOptions options) => options.StateFile is { } path
        ? Path.GetFullPath(path) : Path.Combine(Path.GetDirectoryName(document.SourcePath ?? throw Error("IMPORT_DOCUMENT_INVALID", "Save the manifest first."))!, "state.json");
    private static string? ObservationReason(ComponentTypeInfo? type, string name, MemberValue member) => type is null ? "reflection-unavailable"
        : type.Members.All(m => m.Name != name) ? "member-not-reflected"
        : member.Kind != "field" ? "member-kind-" + member.Kind
        : type.Members.Single(m => m.Name == name).Kind != "field" ? "member-kind-mismatch" : null;
    private static bool BindingMatches(SlotInfo expected, SlotInfo actual) => expected.Id == actual.Id && expected.ParentId == actual.ParentId && expected.Name == actual.Name && !actual.IsReferenceOnly;
    private static bool SameStructure(IReadOnlyDictionary<string, SlotInfo> before, IReadOnlyDictionary<string, SlotInfo> after) =>
        before.Count == after.Count && before.All(s => after.TryGetValue(s.Key, out var current) && BindingMatches(s.Value, current) &&
            s.Value.Components.Select(c => (c.Id, c.Type)).ToHashSet().SetEquals(current.Components.Select(c => (c.Id, c.Type))));
    private static bool SameValue(MemberValue a, MemberValue b) => a.Kind == b.Kind && a.Type == b.Type && a.Id == b.Id && JsonNode.DeepEquals(a.Value, b.Value);
    private static bool Matches(MemberValue member, string raw)
    {
        if (member.Kind != "field") return false;
        if (member.Value is JsonValue value && value.TryGetValue<string>(out var text)) return text == raw;
        if (MemberValueSyntax.IsStructuredTuple(member.Type, out _))
            return JsonNode.DeepEquals(MemberValueSyntax.ParseTuple(member.Type, member.Value!.ToJsonString()), MemberValueSyntax.ParseTuple(member.Type, raw));
        return JsonNode.DeepEquals(member.Value, MemberValueSyntax.NormalizeTupleOrJson(member.Type, raw));
    }
    private static string Raw(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    private static void SetBaseline(ExistingEditState state, string key, string member, MemberValue value)
    {
        if (!state.Baselines.TryGetValue(key, out var fields)) state.Baselines[key] = fields = new(StringComparer.Ordinal);
        fields[member] = value;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static T Deserialize<T>(string text)
    {
        try { return JsonSerializer.Deserialize<T>(text, Json) ?? throw new JsonException("Empty document."); }
        catch (JsonException ex) { throw new RLoopException("IMPORT_DATA_INVALID", ex.Message, ExitCodes.ValidationFailed, innerException: ex); }
    }
    private static async Task WriteNewAsync(string path, string content, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), ct);
    }
    private static RLoopException Error(string code, string message) => new(code, message, ExitCodes.ValidationFailed);
    private sealed record Write(string Key, string Member, ComponentInfo Component, SlotInfo Slot, MemberValue Before, string Raw);
    private sealed record Converged(string Key, string Member, MemberValue Value);
    private sealed record Prepared(ExistingSnapshot Snapshot, string StatePath, ExistingEditState State, SessionInfo Session,
        IReadOnlyList<Write> Writes, IReadOnlyList<Converged> Converged, IReadOnlyList<ApplyValidationIssue> Warnings,
        IReadOnlyList<ReferenceWrite> ReferenceWrites, IReadOnlyList<SlotWrite> SlotWrites);
}
