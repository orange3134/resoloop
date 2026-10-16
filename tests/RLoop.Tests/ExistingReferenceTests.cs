using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ExistingReferenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-reference-" + Guid.NewGuid().ToString("N"));
    private readonly IResoniteClient client = DispatchProxy.Create<IResoniteClient, ReferenceClient>();
    private ReferenceClient Fake => (ReferenceClient)client;
    private WorldService World => new(client, "[resoloop test]");
    private ExistingSnapshot snapshot = null!;
    private async Task<ApplyDocument> Import()
    {
        var report = await new ExistingContentService(client).ImportAsync("world", directory);
        snapshot = JsonSerializer.Deserialize<ExistingSnapshot>(File.ReadAllText(report.Snapshot), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return ApplyDocument.Load(report.Manifest);
    }
    private string Edge(string top, int? index = null) => snapshot.References!.Edges.Single(e => e.Value.Path[0].Member == top &&
        (index is null || e.Value.Path[^1].Index == index)).Key;
    private ExistingReferenceTarget External(string id) => new("external", snapshot.References!.External.Single(e => e.Value.Id == id).Key);
    private static ApplyDocument Change(ApplyDocument document, string edge, ExistingReferenceTarget? target) => document with
    { Existing = document.Existing! with { ReferenceChanges = new Dictionary<string, ExistingReferenceTarget?> { [edge] = target } } };

    [Fact]
    public async Task CapturesExternalAndNestedReferencesAndLiteralPathsWithoutExpandingOwnership()
    {
        var document = await Import();
        Assert.Equal(2, snapshot.Slots.Count);
        Assert.Equal(2, snapshot.Components.Count);
        Assert.Equal(5, snapshot.References!.Edges.Count);
        Assert.Equal(3, snapshot.References.External.Count);
        Assert.Single(snapshot.References.External, e => e.Value.Status == "opaque");
        var nested = snapshot.References.Edges[Edge("Nested")];
        Assert.Equal("a.b/[x]", nested.Path[1].Member);
        Assert.Equal("member", nested.Target!.Kind);
        Assert.Equal(0, nested.Target.Path![1].Index);
        document = document with { Existing = document.Existing! with { ReferenceChanges = snapshot.References.Edges.ToDictionary(e => e.Key, e => e.Value.Target) } };
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        await World.ApplyAsync(document);
        Assert.Empty(Fake.Writes);
        Assert.Equal("asset-a", Fake.Source["Direct"].TargetId);
        Assert.True((await ApplyDocumentValidator.ValidateAsync(document)).Valid);
    }

    [Fact]
    public async Task P0BundleRemainsReadableButCannotInventReferenceKeys()
    {
        var document = await Import();
        var snapshotPath = Path.Combine(directory, "snapshot.json");
        var saved = JsonNode.Parse(File.ReadAllText(snapshotPath))!;
        saved["schemaVersion"] = 1;
        saved.AsObject().Remove("references");
        var text = saved.ToJsonString();
        File.WriteAllText(snapshotPath, text);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        document = document with { Existing = document.Existing! with { Sha256 = hash } };
        var statePath = Path.Combine(directory, "state.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        state["snapshotHash"] = hash;
        state.AsObject().Remove("referenceBaselines");
        File.WriteAllText(statePath, state.ToJsonString());
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        Assert.Equal("IMPORT_REFERENCE_GRAPH_REQUIRED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(Change(document, "r000000", null)))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task DirectReferenceCanTargetNestedMemberAndThenAnExternalDependency()
    {
        var document = await Import();
        var targetKey = snapshot.Components.Single(c => c.Value.Data.Id == "target").Key;
        document = Change(document, Edge("Direct"), new("member", targetKey, [new(Member: "Items"), new(Index: 1)]));
        Assert.Single((await World.PlanApplyAsync(document)).Changes);
        await World.ApplyAsync(document);
        Assert.Equal("item-1", Fake.Source["Direct"].TargetId);
        Assert.Single(Fake.Writes);
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        await World.ApplyAsync(Change(document, Edge("Direct"), External("asset-b")));
        Assert.Equal("asset-b", Fake.Source["Direct"].TargetId);
        Assert.Equal(2, Fake.Writes.Count);
        Assert.All(Fake.Writes, w => Assert.Equal("source", w.Id));
    }

    [Fact]
    public async Task ReferenceListChangesOneElementAndPreservesNeighborAndComponentIdentity()
    {
        var document = Change(await Import(), Edge("Materials", 0), External("asset-b"));
        await World.ApplyAsync(document);
        Assert.Equal("asset-b", Fake.Source["Materials"].Elements![0].TargetId);
        Assert.Equal("asset-b", Fake.Source["Materials"].Elements![1].TargetId);
        Assert.Equal("mat-0", Fake.Source["Materials"].Elements![0].Id);
        Assert.Equal("mat-1", Fake.Source["Materials"].Elements![1].Id);
        Assert.Single(Fake.Writes);
    }

    [Fact]
    public async Task PartialSyncObjectReferenceUsesLiteralMemberName()
    {
        var document = Change(await Import(), Edge("Nested"), External("asset-b"));
        await World.ApplyAsync(document);
        Assert.Equal("asset-b", Fake.Source["Nested"].Members!["a.b/[x]"].TargetId);
        Assert.Equal(123, Fake.Source["Nested"].Members!["Unmanaged"].Value!.GetValue<int>());
        Assert.Equal("Nested", Assert.Single(Fake.Writes).Member);
    }

    [Fact]
    public async Task ConflictingReferenceBlocksEntirePreflightIncludingFieldEdits()
    {
        var document = Change(await Import(), Edge("Direct"), null);
        document = document with { Existing = document.Existing! with { Changes = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
        { [snapshot.Components.Single(c => c.Value.Data.Id == "source").Key] = new Dictionary<string, JsonElement> { ["Number"] = JsonSerializer.SerializeToElement(2) } } } };
        Fake.Source["Direct"] = Fake.Source["Direct"] with { TargetId = "asset-b" };
        Assert.Equal("IMPORT_REFERENCE_CONFLICT", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task MultipleReferenceEditsInOneListKeepEachOtherAndThenConverge()
    {
        var document = await Import();
        document = document with { Existing = document.Existing! with { ReferenceChanges = new Dictionary<string, ExistingReferenceTarget?>
        { [Edge("Materials", 0)] = External("asset-b"), [Edge("Materials", 1)] = null } } };
        await World.ApplyAsync(document);
        Assert.Equal("asset-b", Fake.Source["Materials"].Elements![0].TargetId);
        Assert.Null(Fake.Source["Materials"].Elements![1].TargetId);
        Assert.Equal(2, Fake.Writes.Count);
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
    }

    [Fact]
    public async Task MissingExternalTargetCannotBeSilentlyReboundOrWritten()
    {
        var document = Change(await Import(), Edge("Direct"), External("asset-b"));
        Fake.MissingExternal = true;
        await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document));
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task ReorderedNestedTargetIsNotGuessedByIndex()
    {
        var document = await Import();
        var key = snapshot.Components.Single(c => c.Value.Data.Id == "target").Key;
        document = Change(document, Edge("Direct"), new("member", key, [new(Member: "Items"), new(Index: 1)]));
        Fake.Target["Items"] = Fake.Target["Items"] with { Elements = Fake.Target["Items"].Elements!.Reverse().ToArray() };
        Assert.Equal("IMPORT_REFERENCE_BINDING_CHANGED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task ChangedSourceListShapeNeverRecreatesOrShrinks()
    {
        var document = Change(await Import(), Edge("Materials", 0), null);
        Fake.Source["Materials"] = Fake.Source["Materials"] with { Elements = Fake.Source["Materials"].Elements!.Take(1).ToArray() };
        Assert.Equal("IMPORT_REFERENCE_SOURCE_UNSUPPORTED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task OpaqueDependenciesCanBeRetainedButCannotBeNewlyAssigned()
    {
        var document = Change(await Import(), Edge("Opaque"), External("opaque"));
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        Assert.Equal("IMPORT_EXTERNAL_UNVERIFIED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(Change(document, Edge("Direct"), External("opaque"))))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task IncompatibleTargetTypeFailsBeforeMutation()
    {
        var document = Change(await Import(), Edge("Direct"), External("asset-b"));
        Fake.Compatible = false;
        Assert.Equal("IMPORT_REFERENCE_TYPE_MISMATCH", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task LostReferenceWriteResponseRecoversAndAdvancesBaseline()
    {
        var document = Change(await Import(), Edge("Direct"), External("asset-b"));
        Fake.LoseResponse = true;
        await Assert.ThrowsAsync<IOException>(() => World.ApplyAsync(document));
        Fake.LoseResponse = false;
        await World.ApplyAsync(document);
        Assert.Single(Fake.Writes);
        await World.ApplyAsync(Change(document, Edge("Direct"), null));
        Assert.Null(Fake.Source["Direct"].TargetId);
        Assert.Equal(2, Fake.Writes.Count);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"index\":0}]")]
    [InlineData("[{\"member\":\"X\",\"index\":0}]")]
    [InlineData("[{\"member\":\"X\"},{\"index\":-1}]")]
    public void MalformedStructuredPathsAreRejected(string json)
    {
        var path = JsonSerializer.Deserialize<MemberPathStep[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("IMPORT_REFERENCE_PATH_INVALID", Assert.Throws<RLoopException>(() => ExistingReferences.ValidatePath(path)).Code);
    }

    [Fact]
    public async Task ExternalKeysCannotBecomeOwnedEditSourcesAndRawIdsAreNotTargets()
    {
        var document = await Import();
        Assert.Equal("IMPORT_REFERENCE_TARGET_INVALID", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(Change(document, Edge("Direct"), new("component", "asset-b"))))).Code);
        Assert.Equal("IMPORT_REFERENCE_KEY_NOT_FOUND", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(Change(document, "e000000", null)))).Code);
        Assert.Empty(Fake.Writes);
    }

    public class ReferenceClient : DispatchProxy
    {
        public bool MissingExternal, LoseResponse;
        public bool LoseSlotResponse;
        public List<SlotUpdateRequest> SlotWrites = [];
        public Dictionary<string, MemberValue> SlotFields = new()
        {
            ["Position"] = new("field", "slot-position", "float3", JsonNode.Parse("[0,0,0]")),
            ["Rotation"] = new("field", "slot-rotation", "floatQ", JsonNode.Parse("[0,0,0,1]")),
            ["Scale"] = new("field", "slot-scale", "float3", JsonNode.Parse("[1,1,1]")),
            ["IsActive"] = new("field", "slot-active", "bool", JsonValue.Create(true)),
            ["IsPersistent"] = new("field", "slot-persistent", "bool", JsonValue.Create(true)),
            ["Tag"] = new("field", "slot-tag", "string", JsonValue.Create("original")),
            ["OrderOffset"] = new("field", "slot-order", "long", JsonValue.Create(0L))
        };
        public bool Compatible = true;
        public List<(string Id, string Member, string Raw)> Writes = [];
        public Dictionary<string, MemberValue> Source = new()
        {
            ["Number"] = new("field", "number", "int", JsonValue.Create(1)),
            ["Direct"] = Ref("direct", "asset-a"),
            ["Materials"] = new("list", "materials", Elements: [Ref("mat-0", "asset-a"), Ref("mat-1", "asset-b")]),
            ["Nested"] = new("syncObject", "nested", Members: new Dictionary<string, MemberValue>
            { ["a.b/[x]"] = Ref("nested-ref", "item-0"), ["Unmanaged"] = new("field", "unmanaged", "int", JsonValue.Create(123)) }),
            ["Opaque"] = Ref("opaque-ref", "opaque")
        };
        public Dictionary<string, MemberValue> Target = new()
        { ["Items"] = new("list", "items", Elements: [new("field", "item-0", "float", JsonValue.Create(0f)), new("field", "item-1", "float", JsonValue.Create(1f))]) };
        private static MemberValue Ref(string id, string? target) => new("reference", id, TargetId: target, TargetType: "IAccepted");
        private SlotInfo Slot(string id) => id == "world"
            ? new("world", "World", "Root", null, null, null, true, true, null, false, [new("source", "Source", Source)], [Slot("child")], Members: SlotFields)
            : new("child", "Child", "world", null, null, null, true, true, null, false, [new("target", "Target", Target)], []);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            switch (method!.Name)
            {
                case nameof(IResoniteClient.GetWorldSessionIdAsync): return Task.FromResult<string?>("world-session");
                case nameof(IResoniteClient.GetSessionInfoAsync): return Task.FromResult(new SessionInfo("ws://localhost:1", true, "1", "1", "connection"));
                case nameof(IResoniteClient.GetSlotAsync):
                    if ((string)args[0]! is not ("world" or "child")) throw new RLoopException("SLOT_NOT_FOUND", "Not a slot.");
                    return Task.FromResult(Slot((string)args[0]!));
                case nameof(IResoniteClient.GetComponentAsync):
                    var id = (string)args[0]!;
                    if (id == "opaque" || id == "asset-b" && MissingExternal) throw new RLoopException("COMPONENT_NOT_FOUND", "Not found.");
                    return Task.FromResult(new ComponentInfo(id, id == "source" ? "Source" : id == "target" ? "Target" : "Asset",
                        id == "source" ? Source : id == "target" ? Target : new Dictionary<string, MemberValue>()));
                case nameof(IResoniteClient.DescribeComponentTypeAsync):
                    var type = (string)args[0]!;
                    var members = type == "Source" ? Source : Target;
                    return Task.FromResult(new ComponentTypeInfo(type, null, null, false, members.Select(m => new MemberDefinitionInfo(m.Key, m.Value.Kind, "MemberType", m.Value.Type, "IAccepted")).ToArray()));
                case nameof(IResoniteClient.DescribeComponentMemberPathAsync):
                    return Task.FromResult(new MemberDefinitionInfo("path", (string)args[0]! == "Source" ? "reference" : "field", "FieldType", null, "IAccepted"));
                case nameof(IResoniteClient.DescribeTypeAsync):
                    var name = (string)args[0]!;
                    return Task.FromResult(new RLoop.Core.TypeInfo(name, null, null, name, null, false, name == "IAccepted", false, false, name == "Asset", false, true, [], Compatible && name != "IAccepted" ? ["IAccepted"] : []));
                case nameof(IResoniteClient.ValidateComponentMemberAsync): return Task.CompletedTask;
                case nameof(IResoniteClient.UpdateSlotAsync):
                    var request = (SlotUpdateRequest)args[0]!;
                    Assert.Equal("world", request.Id);
                    Assert.Null(request.Name); Assert.Null(request.ParentId);
                    SlotWrites.Add(request);
                    void Set(string key, object? value) { if (value is not null) SlotFields[key] = SlotFields[key] with { Value = JsonSerializer.SerializeToNode(value) }; }
                    if (request.Position is { } p) Set("Position", new[] { p.X, p.Y, p.Z });
                    if (request.Rotation is { } q) Set("Rotation", new[] { q.X, q.Y, q.Z, q.W });
                    if (request.Scale is { } s) Set("Scale", new[] { s.X, s.Y, s.Z });
                    Set("IsActive", request.IsActive); Set("IsPersistent", request.IsPersistent); Set("Tag", request.Tag); Set("OrderOffset", request.OrderOffset);
                    if (LoseSlotResponse) throw new IOException("Lost Slot write response.");
                    return Task.CompletedTask;
                case nameof(IResoniteClient.SetComponentMemberAsync):
                    Assert.Equal("source", (string)args[0]!);
                    var top = (string)args[1]!; var raw = (string)args[2]!;
                    Writes.Add(("source", top, raw));
                    if (top == "Materials")
                    {
                        var array = JsonNode.Parse(raw)!.AsArray();
                        Source[top] = Source[top] with { Elements = Source[top].Elements!.Select((m, i) => m with { TargetId = array[i]?.GetValue<string>() }).ToArray() };
                    }
                    else if (top == "Nested")
                    {
                        var nestedMembers = Source[top].Members!.ToDictionary();
                        nestedMembers["a.b/[x]"] = nestedMembers["a.b/[x]"] with { TargetId = JsonNode.Parse(raw)!["a.b/[x]"]?.GetValue<string>() };
                        Source[top] = Source[top] with { Members = nestedMembers };
                    }
                    else Source[top] = Source[top] with { TargetId = raw == "null" ? null : raw };
                    if (LoseResponse) throw new IOException("Lost response.");
                    return Task.CompletedTask;
                default: throw new InvalidOperationException("Unexpected API: " + method.Name);
            }
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
