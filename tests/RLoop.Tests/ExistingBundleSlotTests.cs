using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Cli;

namespace RLoop.Tests;

public sealed class ExistingBundleSlotTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-split-" + Guid.NewGuid().ToString("N"));
    private readonly IResoniteClient client = DispatchProxy.Create<IResoniteClient, ExistingReferenceTests.ReferenceClient>();
    private ExistingReferenceTests.ReferenceClient Fake => (ExistingReferenceTests.ReferenceClient)client;
    private WorldService World => new(client, "[test]");
    private ExistingSnapshot snapshot = null!;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private async Task<ApplyDocument> Import()
    {
        var report = await new ExistingContentService(client).ImportAsync("world", directory, splitSlots: 1);
        snapshot = ExistingBundleFiles.ReadSnapshot(report.Snapshot, File.ReadAllText(report.Snapshot));
        return ApplyDocument.Load(report.Manifest);
    }
    private string Root => snapshot.RootKey;
    private string Source => snapshot.Components.Single(c => c.Value.Data.Id == "source").Key;
    private string Edge => snapshot.References!.Edges.Single(e => e.Value.Path.Count == 1 && e.Value.Path[0].Member == "Direct").Key;
    private static JsonElement Value(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private ApplyDocument Slots(ApplyDocument document, string attribute, string value) => document with
    { Existing = document.Existing! with { SlotChanges = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
        { [Root] = new Dictionary<string, JsonElement> { [attribute] = Value(value) } } } };
    private void Part(string file, ApplyDocument document, IReadOnlyDictionary<string, ExistingReferenceTarget?>? refs = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? slots = null)
        => File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(new ExistingEditFile(1, document.Ownership!.Key, document.Existing!.Sha256, ReferenceChanges: refs, SlotChanges: slots), Json));

    [Fact]
    public async Task SplitSnapshotResolvesForwardReferencesAndEditFilesShareOneState()
    {
        var document = await Import();
        var index = JsonSerializer.Deserialize<ExistingSnapshotIndex>(File.ReadAllText(Path.Combine(directory, "snapshot.json")), Json)!;
        Assert.Equal(3, index.SchemaVersion); Assert.Equal(3, index.Files.Count);
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        var target = snapshot.Components.Single(c => c.Value.Data.Id == "target").Key;
        Part("refs.json", document, new Dictionary<string, ExistingReferenceTarget?> { [Edge] = new("member", target, [new(Member: "Items"), new(Index: 1)]) });
        Part("slots.json", document, slots: new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { [Root] = new Dictionary<string, JsonElement> { ["Tag"] = Value("\"${0}:literal\"") } });
        document = document with { Existing = document.Existing! with { EditFiles = ["refs.json", "slots.json"] } };
        File.WriteAllText(document.SourcePath!, JsonSerializer.Serialize(document, Json));
        document = ApplyDocument.Load(document.SourcePath!);
        Assert.Equal(2, (await World.PlanApplyAsync(document)).Updates);
        var applied = await World.ApplyAsync(document);
        Assert.Equal(1, applied.SlotsUpdated); Assert.Equal(1, applied.ComponentsUpdated);
        Assert.Equal("item-1", Fake.Source["Direct"].TargetId);
        Assert.Equal("${0}:literal", Fake.SlotFields["Tag"].Value!.GetValue<string>());
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        await World.ApplyAsync(document);
        Assert.Single(Fake.SlotWrites); Assert.Single(Fake.Writes);
    }

    [Theory]
    [InlineData("Position", "[1,2,3]")]
    [InlineData("Rotation", "[0,1,0,0]")]
    [InlineData("Scale", "[2,3,4]")]
    [InlineData("IsActive", "false")]
    [InlineData("IsPersistent", "false")]
    [InlineData("Tag", "\"\"")]
    [InlineData("OrderOffset", "9223372036854775807")]
    public async Task EachSlotAttributeWritesOnlyIntentAndConverges(string attribute, string value)
    {
        var document = Slots(await Import(), attribute, value);
        var before = Fake.SlotFields.ToDictionary();
        Assert.Equal(attribute, Assert.Single(Assert.Single((await World.PlanApplyAsync(document)).Changes).Members!));
        await World.ApplyAsync(document);
        Assert.Single(Fake.SlotWrites); Assert.Empty(Fake.Writes);
        foreach (var item in before.Where(p => p.Key != attribute)) Assert.Equal(item.Value, Fake.SlotFields[item.Key]);
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
    }

    [Fact]
    public async Task NullObservedTagCanBeSetAndEmptyTagIntentIsInitiallyNoOp()
    {
        Fake.SlotFields["Tag"] = Fake.SlotFields["Tag"] with { Value = null };
        var document = Slots(await Import(), "Tag", "\"\"");
        Assert.Empty((await World.PlanApplyAsync(document)).Changes);
        await World.ApplyAsync(Slots(document, "Tag", "\"set\""));
        Assert.Equal("set", Fake.SlotFields["Tag"].Value!.GetValue<string>());
        Assert.Single(Fake.SlotWrites);
    }

    [Fact]
    public async Task RuntimeSlotConflictBlocksAlsoComponentEdits()
    {
        var document = Slots(await Import(), "Tag", "\"desired\"");
        document = document with { Existing = document.Existing! with { ReferenceChanges = new Dictionary<string, ExistingReferenceTarget?> { [Edge] = null } } };
        Fake.SlotFields["Tag"] = Fake.SlotFields["Tag"] with { Value = JsonValue.Create("concurrent") };
        Assert.Equal("IMPORT_SLOT_CONFLICT", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.SlotWrites); Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task UntouchedRuntimeAttributesAreNotRestoredAndLostResponseIsRecoverable()
    {
        var document = Slots(await Import(), "Tag", "\"desired\"");
        Fake.SlotFields["IsActive"] = Fake.SlotFields["IsActive"] with { Value = JsonValue.Create(false) };
        Fake.LoseSlotResponse = true;
        await Assert.ThrowsAsync<IOException>(() => World.ApplyAsync(document));
        Fake.LoseSlotResponse = false;
        await World.ApplyAsync(document);
        Assert.Single(Fake.SlotWrites);
        Assert.False(Fake.SlotFields["IsActive"].Value!.GetValue<bool>());
        await World.ApplyAsync(Slots(document, "Tag", "\"next\""));
        Assert.Equal(2, Fake.SlotWrites.Count);
    }

    [Theory]
    [InlineData("Name", "\"new\"")]
    [InlineData("Parent", "\"Root\"")]
    [InlineData("IsActive", "null")]
    [InlineData("IsActive", "1")]
    [InlineData("OrderOffset", "1.5")]
    [InlineData("Rotation", "[0,0,0,0]")]
    [InlineData("Scale", "[1,2]")]
    public async Task InvalidOrStructuralSlotEditsCannotWrite(string attribute, string value)
    {
        var document = Slots(await Import(), attribute, value);
        await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document));
        Assert.Empty(Fake.SlotWrites); Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task ReplacedSlotMemberAndForeignKeysAreRejected()
    {
        var document = Slots(await Import(), "Tag", "\"new\"");
        Fake.SlotFields["Tag"] = Fake.SlotFields["Tag"] with { Id = "replacement" };
        Assert.Equal("IMPORT_BINDING_UNVERIFIED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        document = document with { Existing = document.Existing! with { SlotChanges = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { ["Root"] = new Dictionary<string, JsonElement> { ["Tag"] = Value("\"x\"") } } } };
        Assert.Equal("IMPORT_SLOT_KEY_NOT_FOUND", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.SlotWrites);
    }

    [Theory]
    [InlineData("../outside.json", "IMPORT_FILE_PATH_INVALID")]
    [InlineData("missing.json", "IMPORT_FILE_MISSING")]
    public async Task InvalidEditFilePathsFailBeforeWorldWrites(string path, string code)
    {
        var document = await Import();
        document = document with { Existing = document.Existing! with { EditFiles = [path] } };
        Assert.Equal(code, (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.SlotWrites); Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task DuplicateEditsAndWrongBundleIdentityAreRejected()
    {
        var document = await Import();
        Part("a.json", document, new Dictionary<string, ExistingReferenceTarget?> { [Edge] = null });
        Part("b.json", document, new Dictionary<string, ExistingReferenceTarget?> { [Edge] = null });
        var split = document with { Existing = document.Existing! with { EditFiles = ["a.json", "b.json"] } };
        Assert.Equal("IMPORT_FILE_DUPLICATE_KEY", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(split))).Code);
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "b.json")))!;
        node["snapshotSha256"] = "other"; File.WriteAllText(Path.Combine(directory, "b.json"), node.ToJsonString());
        Assert.Equal("IMPORT_FILE_IDENTITY_MISMATCH", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(split))).Code);
        Assert.Empty(Fake.Writes);
    }

    [Fact]
    public async Task MissingOrTamperedSnapshotPartsFailWithoutMutations()
    {
        var document = await Import();
        var path = Path.Combine(directory, "snapshot", "part-0001.json");
        File.AppendAllText(path, " ");
        Assert.Equal("IMPORT_SNAPSHOT_CHANGED", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        File.Delete(path);
        Assert.Equal("IMPORT_FILE_MISSING", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.Writes); Assert.Empty(Fake.SlotWrites);
    }

    [Fact]
    public async Task DuplicateSlotAttributesAndDuplicateJsonPropertiesAreRejected()
    {
        var document = await Import();
        var values = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { [Root] = new Dictionary<string, JsonElement> { ["Tag"] = Value("\"x\"") } };
        Part("a.json", document, slots: values); Part("b.json", document, slots: values);
        document = document with { Existing = document.Existing! with { EditFiles = ["a.json", "b.json"] } };
        Assert.Equal("IMPORT_FILE_DUPLICATE_KEY", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        File.WriteAllText(Path.Combine(directory, "a.json"), "{\"schemaVersion\":1,\"schemaVersion\":2}");
        Assert.Equal("IMPORT_DATA_INVALID", (await Assert.ThrowsAsync<RLoopException>(() => World.ApplyAsync(document))).Code);
        Assert.Empty(Fake.SlotWrites);
    }

    [Fact]
    public async Task RepeatingSnapshotPartIsRejectedEvenWithValidHashes()
    {
        var document = await Import();
        var path = Path.Combine(directory, "snapshot.json");
        var index = JsonNode.Parse(File.ReadAllText(path))!;
        index["files"]!.AsArray().Add(index["files"]![0]!.DeepClone());
        var text = index.ToJsonString(); File.WriteAllText(path, text);
        document = document with { Existing = document.Existing! with { Sha256 = ExistingBundleFiles.Hash(text) } };
        Assert.Equal("IMPORT_FILE_DUPLICATE_KEY", (await Assert.ThrowsAsync<RLoopException>(() => ExistingContentService.ValidateAsync(document))).Code);
        Assert.Empty(Fake.SlotWrites); Assert.Empty(Fake.Writes);
    }

    [Fact]
    public void SplitSlotArgumentIsParsed()
    {
        var args = ParsedArguments.Parse(["import", "world", "--split-slots", "256", "--output", "new"]);
        Assert.Equal(256, args.IntOption("split-slots", 0, 0, 4096));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
