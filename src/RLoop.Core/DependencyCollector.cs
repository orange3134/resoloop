using System.Text.Json;

namespace RLoop.Core;

public sealed record DependencyLimits(int Depth = 3, int MaxObjects = 2000, int MaxBytes = 16 * 1024 * 1024, int Seconds = 120);
public sealed record DependencyRecord(string Key, string Id, string Status, DateTimeOffset ObservedAt, int Depth,
    ComponentInfo? Component = null, SlotInfo? Slot = null, string? Diagnostic = null);
public sealed record DependencyCapture(int SchemaVersion, string SnapshotSha256, string WorldSessionId,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, DependencyLimits Limits, string? StopReason,
    List<DependencyRecord> Records, Dictionary<string, ExistingTypeCapability> Types, ClientMetrics? Metrics);

/// <summary>Read-only reference traversal. Sidecars never enroll dependencies into edit ownership.</summary>
public static class DependencyCollector
{
    public static DependencyCapture Read(string path, BundleAnalysis bundle)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw BundleAnalysis.Error("BUNDLE_DEPENDENCY_INVALID", "Dependency file exceeds 256 MiB.");
        var readOptions = new JsonSerializerOptions(BundleAnalysis.Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
        var capture = JsonSerializer.Deserialize<DependencyCapture>(File.ReadAllText(path), readOptions)
            ?? throw BundleAnalysis.Error("BUNDLE_DEPENDENCY_INVALID", "Empty dependency capture.");
        if (capture.SchemaVersion != 1 || capture.SnapshotSha256 != bundle.Hash || capture.WorldSessionId != bundle.Snapshot.WorldSessionId ||
            capture.Records is null || capture.Types is null || capture.Limits is null || capture.Records.Count > 10000 ||
            capture.Records.Any(r => r is null) ||
            capture.Records.Select(r => r.Key).Distinct().Count() != capture.Records.Count ||
            capture.Records.Select(r => r.Id).Distinct().Count() != capture.Records.Count ||
            capture.Records.Any(r => r is null || string.IsNullOrWhiteSpace(r.Key) || string.IsNullOrWhiteSpace(r.Id) ||
                r.Component is not null && (r.Component.Id != r.Id || r.Slot is not null) || r.Slot is not null && r.Slot.Id != r.Id ||
                r.Status == "captured" && r.Component is null && r.Slot is null))
            throw BundleAnalysis.Error("BUNDLE_DEPENDENCY_INVALID", "Dependency file identity/schema/records do not match this snapshot.");
        return capture;
    }

    public static async Task<DependencyCapture> CollectAsync(BundleAnalysis bundle, IResoniteClient client,
        string output, DependencyLimits limits, CancellationToken ct = default)
    {
        if (limits.Depth is < 1 or > 16 || limits.MaxObjects is < 1 or > 10000 ||
            limits.MaxBytes is < 4096 or > 256 * 1024 * 1024 || limits.Seconds is < 1 or > 3600)
            throw BundleAnalysis.Error("BUNDLE_LIMIT_INVALID", "depth 1..16, max-objects 1..10000, max-bytes 4096..268435456, seconds 1..3600 required.");
        if (File.Exists(output) || Directory.Exists(output)) throw BundleAnalysis.Error("BUNDLE_OUTPUT_EXISTS", "Choose a new dependency capture filename.");
        if (client is IResoniteClientDiagnostics diagnostics) diagnostics.ResetMetrics();
        async Task CheckSession()
        {
            if (await client.GetWorldSessionIdAsync(ct) != bundle.Snapshot.WorldSessionId)
                throw BundleAnalysis.Error("IMPORT_SESSION_CHANGED", "Dependency collection requires the captured world session. Re-import after restarting; never reuse old IDs.");
        }
        await CheckSession();
        var baseline = bundle.Snapshot.Slots[bundle.Snapshot.RootKey];
        var root = await client.GetSlotAsync(baseline.Id, 0, false, ct);
        if (root.Id != baseline.Id || root.Name != baseline.Name || root.ParentId != baseline.ParentId || root.IsReferenceOnly)
            throw BundleAnalysis.Error("IMPORT_BINDING_UNVERIFIED", "Imported root identity no longer matches.");
        var started = DateTimeOffset.UtcNow;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(limits.Seconds));
        var token = budget.Token;
        var records = new List<DependencyRecord>(); var types = new Dictionary<string, ExistingTypeCapability>();
        var queue = new Queue<(string Id, string Key, int Depth)>(); var seen = new HashSet<string>();
        var baseExternal = bundle.Snapshot.References?.External ?? new Dictionary<string, ExistingExternalDependency>();
        var keys = baseExternal.ToDictionary(e => e.Value.Id, e => e.Key);
        var newKey = 0; var admitted = 0; string? stop = null; var bytes = 2048;
        void Enqueue(string? id, int depth)
        {
            if (string.IsNullOrEmpty(id) || bundle.Resolve(id) is { Key: { } key } && bundle.Get(key).Kind != "external" || !seen.Add(id)) return;
            if (admitted >= limits.MaxObjects) { stop ??= "objects"; return; }
            if (!keys.TryGetValue(id, out var externalKey)) keys[id] = externalKey = "x" + (newKey++).ToString("D6");
            queue.Enqueue((id, externalKey, depth));
            admitted++;
        }
        foreach (var edge in bundle.Edges.Where(e => bundle.Get(e.Source.Key!).Kind != "external" && !bundle.IsParentEdge(e))) Enqueue(edge.Target?.Id, 1);
        // Include opaque targets from legacy captures, even when they have no symbolic edge.
        foreach (var external in baseExternal.Values) Enqueue(external.Id, 1);
        while (queue.TryDequeue(out var candidate))
        {
            ct.ThrowIfCancellationRequested();
            var record = new DependencyRecord(candidate.Key, candidate.Id, "pending", DateTimeOffset.UtcNow, candidate.Depth);
            if (candidate.Id == "Root") record = record with { Status = "excluded-root", Diagnostic = "Root is never traversed." };
            else if (candidate.Depth > limits.Depth) { record = record with { Status = "depth-limit" }; stop ??= "depth"; }
            else if (budget.IsCancellationRequested) { record = record with { Status = "time-limit" }; stop ??= "time"; }
            else if (bytes >= limits.MaxBytes) { record = record with { Status = "byte-limit" }; stop ??= "bytes"; }
            else
            {
                try
                {
                    ComponentInfo? component = null;
                    try { component = await client.GetComponentAsync(candidate.Id, token); }
                    catch (RLoopException ex) when (ex.Code == "COMPONENT_NOT_FOUND") { }
                    if (component is not null)
                    {
                        if (component.Id != candidate.Id) throw BundleAnalysis.Error("BUNDLE_ID_MISMATCH", "Component read returned a different ID.");
                        record = record with { Status = "captured", Component = component };
                    }
                    else
                    {
                        try
                        {
                            var slot = await client.GetSlotAsync(candidate.Id, 0, false, token);
                            if (slot.Id != candidate.Id || slot.IsReferenceOnly) throw BundleAnalysis.Error("BUNDLE_ID_MISMATCH", "Slot read did not return the requested instance.");
                            record = record with { Status = "captured", Slot = slot with { Children = [], Components = [] } };
                        }
                        catch (RLoopException ex) when (ex.Code == "SLOT_NOT_FOUND")
                        { record = record with { Status = "opaque", Diagnostic = "Not readable as Component or Slot; not evidence of a dangling reference." }; }
                    }
                    var size = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(record, BundleAnalysis.Json));
                    if (bytes + size > limits.MaxBytes)
                    { record = record with { Status = "byte-limit", Component = null, Slot = null }; bytes = limits.MaxBytes; stop ??= "bytes"; }
                    else
                    {
                        bytes += size;
                        if (record.Component is { } data && !bundle.Snapshot.Types.ContainsKey(data.Type) && !types.ContainsKey(data.Type))
                        {
                            ExistingTypeCapability capability;
                            try { capability = new(await client.DescribeComponentTypeAsync(data.Type, token), null, null); }
                            catch (RLoopException ex) { capability = new(null, ex.Code, ex.Message); }
                            var typeBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(capability, BundleAnalysis.Json));
                            if (bytes + typeBytes <= limits.MaxBytes) { types.Add(data.Type, capability); bytes += typeBytes; }
                            else { types.Add(data.Type, new(null, "BUNDLE_BYTE_LIMIT", "Type definition omitted by byte budget.")); stop ??= "bytes"; }
                        }
                        var members = record.Component?.Members ?? record.Slot?.Members ?? new Dictionary<string, MemberValue>();
                        foreach (var (path, value) in ExistingReferences.Walk(members))
                            if (value.Kind == "reference" && !(record.Slot is not null && path is [{ Member: "Parent" }]))
                                Enqueue(value.TargetId, candidate.Depth + 1);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { record = record with { Status = "time-limit", Component = null, Slot = null }; stop ??= "time"; }
                catch (RLoopException ex)
                { record = record with { Status = "read-error", Component = null, Slot = null, Diagnostic = ex.Code + ": " + ex.Message }; }
            }
            records.Add(record);
        }
        await CheckSession();
        var result = new DependencyCapture(1, bundle.Hash, bundle.Snapshot.WorldSessionId, started, DateTimeOffset.UtcNow,
            limits, stop, records, types, (client as IResoniteClientDiagnostics)?.SnapshotMetrics());
        // Bound the actual published file too, including metadata and placeholders.
        var text = JsonSerializer.Serialize(result, BundleAnalysis.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > limits.MaxBytes)
        {
            result = result with { StopReason = "bytes" };
            types.Clear();
            text = JsonSerializer.Serialize(result, BundleAnalysis.Json);
            for (var i = records.Count - 1; i >= 0 && System.Text.Encoding.UTF8.GetByteCount(text) > limits.MaxBytes; i--)
            {
                records[i] = records[i] with { Component = null, Slot = null, Status = "byte-limit", Diagnostic = null };
                text = JsonSerializer.Serialize(result, BundleAnalysis.Json);
            }
            while (records.Count > 0 && System.Text.Encoding.UTF8.GetByteCount(text) > limits.MaxBytes)
            { records.RemoveAt(records.Count - 1); text = JsonSerializer.Serialize(result, BundleAnalysis.Json); }
            if (System.Text.Encoding.UTF8.GetByteCount(text) > limits.MaxBytes)
                throw BundleAnalysis.Error("BUNDLE_CAPTURE_TOO_LARGE", "Capture metadata exceeds byte budget. No file was published.");
        }
        BundleAnalysis.WriteNew(output, text);
        return result;
    }
}
