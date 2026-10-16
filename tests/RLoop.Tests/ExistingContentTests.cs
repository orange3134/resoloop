using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ExistingContentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-import-test-" + Guid.NewGuid().ToString("N"));
    private readonly IResoniteClient client = DispatchProxy.Create<IResoniteClient, ImportClient>();
    private ImportClient Fake => (ImportClient)client;
    private WorldService World => new(client, "[resoloop test]");
    private async Task<ApplyDocument> Import(string name = "first") => ApplyDocument.Load((await new ExistingContentService(client)
        .ImportAsync("world", Path.Combine(directory, name))).Manifest);

    private static ApplyDocument Edit(ApplyDocument document, string key = "c000000", string field = "Value", object? value = null)
        => document with { Existing = document.Existing! with { Changes = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
        { [key] = new Dictionary<string, JsonElement> { [field] = JsonSerializer.SerializeToElement(value ?? 2) } } } };

    [Fact]
    public async Task ImportReimportAndDiffPreserveDuplicatesUnknownsRuntimeAndExternalReferences()
    {
        var document = await Import();
        Fake.Value = 99; // Runtime-owned value changed after capture; no change was requested.
        var plan = await World.PlanApplyAsync(document);
        Assert.Empty(plan.Changes);
        Assert.Equal(6, plan.NoOps);
        Assert.Contains(plan.Warnings, w => w.Code == "IMPORT_OBSERVATION_ONLY_TYPES");
        var applied = await World.ApplyAsync(document);
        Assert.False(applied.Created);
        Assert.Equal(0, applied.ComponentsUpdated);
        Assert.Empty(Fake.Writes);
        var second = await Import("second");
        Assert.Empty((await World.PlanApplyAsync(second)).Changes);
        using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "first", "snapshot.json")));
        var components = snapshot.RootElement.GetProperty("components");
        Assert.Equal("outside", components.GetProperty("c000000").GetProperty("data").GetProperty("members").GetProperty("Reference").GetProperty("targetId").GetString());
        Assert.Contains("${0}:", File.ReadAllText(Path.Combine(directory, "first", "snapshot.json")));
        Assert.Equal(99, Fake.Value);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task ExplicitFieldEditWritesOnlyOneFieldAndCheckpointsNextBaseline()
    {
        var document = Edit(await Import());
        var plan = await World.PlanApplyAsync(document);
        Assert.Equal("update", Assert.Single(plan.Changes).Action);
        Assert.Equal("Value", Assert.Single(plan.Changes[0].Members!));
        Assert.Empty(Fake.Writes);
        await World.ApplyAsync(document);
        Assert.Equal(("known", "Value", "2"), Assert.Single(Fake.Writes));
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        await World.ApplyAsync(Edit(document, value: 3));
        Assert.Equal(3, Fake.Value);
        Assert.Equal(2, Fake.Writes.Count);
    }

    [Fact]
    public async Task WorldSideChangeConflictsBeforeAnyWrites()
    {
        var document = Edit(await Import());
        Fake.Value = 7;
        var error = await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document));
        Assert.Equal("IMPORT_EDIT_CONFLICT", error.Code);
        Assert.Equal(7, Fake.Value);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task ReconnectRequiresWorldIdentityAndNeverGuessesDuplicateNames()
    {
        var document = Edit(await Import());
        Fake.Connection = "different-connection";
        Assert.Single((await World.PlanApplyAsync(document)).Changes);
        Fake.WorldSession = "new-world-session";
        Assert.Equal("IMPORT_SESSION_CHANGED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Fake.WorldSession = null;
        Assert.Equal("IMPORT_WORLD_IDENTITY_UNAVAILABLE", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task MissingImportedComponentCannotBeReplacedBySameTypeCandidate()
    {
        var document = await Import();
        Fake.ComponentId = "replacement";
        var plan = await World.PlanApplyAsync(document);
        Assert.Empty(plan.Changes);
        Assert.Contains(plan.Warnings, w => w.Path == "c000000" && w.Code == "IMPORT_BINDING_UNVERIFIED");
        Assert.Equal("IMPORT_BINDING_UNVERIFIED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(Edit(document)))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Theory]
    [InlineData("c000001", "Value")]
    [InlineData("c000000", "Reference")]
    [InlineData("c000000", "List")]
    [InlineData("c000000", "Missing")]
    public async Task ObservationOnlyMembersCannotBeWrittenOrRecreated(string key, string field)
    {
        var document = Edit(await Import(), key, field);
        Assert.Equal("IMPORT_MEMBER_OBSERVATION_ONLY", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task RefusesLifecycleFlagsAndChangedSnapshot()
    {
        var document = await Import();
        Assert.Equal("IMPORT_LIFECYCLE_UNSUPPORTED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document, new(Prune: true, ConfirmDeletes: true)))).Code);
        File.AppendAllText(Path.Combine(directory, "first", "snapshot.json"), " ");
        Assert.Equal("IMPORT_SNAPSHOT_CHANGED", (await Assert.ThrowsAsync<RLoopException>(() => World.PlanApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task LiteralEditsSurviveLoadAndUnknownPropertiesAreRejected()
    {
        var document = Edit(await Import(), field: "Text", value: "${0}:");
        var node = JsonNode.Parse(File.ReadAllText(document.SourcePath!))!;
        node["existing"]!["changes"] = JsonSerializer.SerializeToNode(document.Existing!.Changes);
        File.WriteAllText(document.SourcePath!, node.ToJsonString());
        var loaded = ApplyDocument.Load(document.SourcePath!);
        Assert.Equal("${0}:", loaded.Existing!.Changes["c000000"]["Text"].GetString());
        await World.ApplyAsync(loaded);
        Assert.Equal("${0}:", Fake.Text);
        node["unexpected"] = true;
        File.WriteAllText(document.SourcePath!, node.ToJsonString());
        Assert.Equal("APPLY_DOCUMENT_INVALID", Assert.Throws<RLoopException>(() => ApplyDocument.Load(document.SourcePath!)).Code);
    }

    [Fact]
    public async Task ImportRequiresExactSubtreeNewDirectoryAndCompleteBoundedObservation()
    {
        var service = new ExistingContentService(client);
        Assert.Equal("IMPORT_ROOT_FORBIDDEN", (await Assert.ThrowsAsync<RLoopException>(() => service.ImportAsync("Root", Path.Combine(directory, "root")))).Code);
        Assert.Equal("IMPORT_SLOT_LIMIT", (await Assert.ThrowsAsync<RLoopException>(() => service.ImportAsync("world", Path.Combine(directory, "small"), 1))).Code);
        await Import();
        Assert.Equal("IMPORT_OUTPUT_EXISTS", (await Assert.ThrowsAsync<RLoopException>(() => Import())).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task InterruptedWriteCanConvergeWithoutRepeatingMutation()
    {
        var document = Edit(await Import());
        Fake.LoseWriteResponse = true;
        await Assert.ThrowsAsync<IOException>(() => World.ApplyAsync(document));
        Fake.LoseWriteResponse = false;
        await World.ApplyAsync(document);
        Assert.Single(Fake.Writes);
        await World.ApplyAsync(Edit(document, value: 3));
        Assert.Equal(3, Fake.Value);
    }

    [Fact]
    public async Task RechecksAncestorOwnershipImmediatelyBeforeFieldWrite()
    {
        var document = Edit(await Import(), key: "c000002");
        Fake.ChangeAncestorAfterPreflight = true;
        Assert.Equal("IMPORT_BINDING_UNVERIFIED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task IncompleteEditDocumentAndMixedLifecycleCannotFallThroughToGeneratedApply()
    {
        var document = await Import();
        Assert.Equal(Path.Combine(directory, "first", "state.json"), document.ResolveStatePath());
        var missing = document with { Existing = document.Existing! with { Snapshot = null! } };
        Assert.Equal("IMPORT_DOCUMENT_INVALID", (await Assert.ThrowsAsync<RLoopException>(() => World.PlanApplyAsync(missing))).Code);
        var mixed = document with { Slot = new("New", "Root", null, null, null, "new") };
        Assert.Equal("IMPORT_DOCUMENT_INVALID", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(mixed))).Code);
        var nullEdits = document with { Existing = document.Existing! with { Changes = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { ["c000000"] = null! } } };
        Assert.Equal("IMPORT_DOCUMENT_INVALID", (await Assert.ThrowsAsync<RLoopException>(() => World.PlanApplyAsync(nullEdits))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public void ParsesImportBoundaryAndOutputOptions()
    {
        var args = ParsedArguments.Parse(["import", "Root/WorldRoot", "--output", "new-import", "--max-slots", "5000", "--json"]);
        Assert.Equal(["import", "Root/WorldRoot"], args.Positionals);
        Assert.Equal("new-import", args.RequireOption("output"));
        Assert.Equal(5000, args.IntOption("max-slots", 50000, 1, 250000));
    }

    public class ImportClient : DispatchProxy
    {
        public int Value = 1;
        public string Text = "original";
        public string ComponentId = "known";
        public string Connection = "connection";
        public string? WorldSession = "world-session";
        public bool LoseWriteResponse;
        public bool ChangeAncestorAfterPreflight;
        public List<(string Id, string Member, string Raw)> Writes = [];
        private Dictionary<string, MemberValue> Members => new()
        {
            ["Value"] = new("field", "value-id", "System.Int32", JsonValue.Create(Value)),
            ["Text"] = new("field", "text-id", "System.String", JsonValue.Create(Text)),
            ["Reference"] = new("reference", "ref-id", TargetId: "outside"),
            ["List"] = new("list", "list-id", Elements: [new("field", Value: JsonValue.Create(1))])
        };
        private Dictionary<string, MemberValue> MembersFor(string id) => id == "other"
            ? Members.ToDictionary(p => p.Key, p => p.Value with { Id = "other-" + p.Value.Id }) : Members;
        private SlotInfo Slot(string id, string name, string? parent, IReadOnlyList<ComponentSummary>? components = null, IReadOnlyList<SlotInfo>? children = null)
            => new(id, name, parent, new(0, 0, 0), new(0, 0, 0, 1), new(1, 1, 1), true, true, "${0}:", false, components ?? [], children ?? []);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            if (args.LastOrDefault() is CancellationToken ct) ct.ThrowIfCancellationRequested();
            switch (method!.Name)
            {
                case nameof(IResoniteClient.GetSessionInfoAsync): return Task.FromResult(new SessionInfo("ws://localhost:1", true, "1", "1", Connection));
                case nameof(IResoniteClient.GetWorldSessionIdAsync): return Task.FromResult(WorldSession);
                case nameof(IResoniteClient.GetSlotAsync):
                    var component = new ComponentSummary(ComponentId, "Known", Members);
                    var first = Slot("a", "Duplicate", "world", [new("unknown", "Unknown", new Dictionary<string, MemberValue> { ["Value"] = new("empty", "unknown-value") })]);
                    var second = Slot("b", "Duplicate", "world", [new("other", "Known", MembersFor("other"))]);
                    return Task.FromResult((string)args[0]! == "world" ? Slot("world", "WorldRoot", ChangeAncestorAfterPreflight && (int)args[1]! == 0 ? "outside" : "Root", [component], [first, second])
                        : (string)args[0]! == "a" ? first : second);
                case nameof(IResoniteClient.GetComponentAsync): return Task.FromResult(new ComponentInfo((string)args[0]!, "Known", MembersFor((string)args[0]!)));
                case nameof(IResoniteClient.DescribeComponentTypeAsync):
                    if ((string)args[0]! == "Unknown") throw new RLoopException("COMPONENT_TYPE_NOT_FOUND", "Legacy Reflection unavailable.");
                    return Task.FromResult(new ComponentTypeInfo("Known", null, null, false,
                        [new("Value", "field", null, "int", null), new("Text", "field", null, "string", null), new("Reference", "reference", null, null, "object"), new("List", "list", null, null, null)]));
                case nameof(IResoniteClient.ValidateComponentMemberAsync): return Task.CompletedTask;
                case nameof(IResoniteClient.SetComponentMemberAsync):
                    Writes.Add(((string)args[0]!, (string)args[1]!, (string)args[2]!));
                    if ((string)args[1]! == "Value") Value = int.Parse((string)args[2]!);
                    else Text = (string)args[2]!;
                    if (LoseWriteResponse) throw new IOException("Lost response after successful write.");
                    return Task.CompletedTask;
                default: throw new InvalidOperationException("Unexpected API (including any create/delete/replace): " + method.Name);
            }
        }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
