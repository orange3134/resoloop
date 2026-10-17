using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Cli;

namespace RLoop.Tests;

public sealed class BundleAnalysisTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-analysis-" + Guid.NewGuid().ToString("N"));
    private readonly IResoniteClient client = DispatchProxy.Create<IResoniteClient, AnalysisClient>();
    private AnalysisClient Fake => (AnalysisClient)client;
    private async Task<BundleAnalysis> Import(int split = 1)
    {
        Fake.Source["Enabled"] = new("field", "enabled", "bool", JsonValue.Create(false));
        Fake.Source["Zero"] = new("field", "zero", "int", JsonValue.Create(0));
        Fake.Source["Null"] = new("field", "null", "string");
        Fake.Source["Name"] = new("field", "name", "string", JsonValue.Create("${0}:EnemySpacing"));
        await new ExistingContentService(client).ImportAsync("world", directory, splitSlots: split);
        Fake.Calls.Clear();
        return BundleAnalysis.Load(directory);
    }
    private static string Key(BundleAnalysis bundle, string id) => bundle.Entities.Values.Single(e => e.Id == id).Key;

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task OfflineViewsPreserveLiteralValuesAndPointBackToExactMembers(int split)
    {
        var bundle = await Import(split);
        var key = Key(bundle, "source");
        var hits = bundle.Find(text: "EnemySpacing").ToArray();
        Assert.Single(hits);
        Assert.Equal("${0}:EnemySpacing", hits[0].GetProperty("value").GetString());
        foreach (var (name, kind) in new[] { ("Enabled", JsonValueKind.False), ("Zero", JsonValueKind.Number), ("Null", JsonValueKind.Null) })
        {
            var row = bundle.Inspect(key, [new(Member: name)]).Single();
            Assert.Equal(kind, row.GetProperty("value").GetProperty("value").ValueKind);
        }
        var nested = bundle.Inspect(key, [new(Member: "Nested"), new(Member: "a.b/[x]")]).Single();
        Assert.EndsWith("/members/Nested/members/a.b~1[x]", nested.GetProperty("source").GetProperty("pointer").GetString());
        Assert.Equal(new[] { "World", "Child" }, bundle.PathOf(Key(bundle, "target")));
        Assert.Single(bundle.Tree(null, 0));
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task MemberIncomingReferencesResolveAcrossFilesAndPaginationIsLossless()
    {
        var bundle = await Import();
        var target = Key(bundle, "target");
        var refs = bundle.References(target, "in", 2, [new(Member: "Items"), new(Index: 0)]).ToArray();
        Assert.Contains(refs, r => r.GetProperty("edge").GetProperty("source").GetProperty("id").GetString() == "nested-ref");
        var all = bundle.Find().ToArray(); var collected = new List<JsonElement>(); var offset = 0;
        do
        {
            var page = bundle.Page(bundle.Find(), offset, 1, 4096);
            collected.AddRange(page.Items);
            if (page.NextOffset is null) break;
            Assert.True(page.NextOffset > offset); offset = page.NextOffset.Value;
        } while (true);
        Assert.Equal(all.Select(e => e.GetRawText()), collected.Select(e => e.GetRawText()));
        Assert.Throws<RLoopException>(() => bundle.Page(bundle.Find(), -1));
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task CaptureTraversesNestedReferencesOnceWithoutOwningOrMutatingDependencies()
    {
        var bundle = await Import();
        var before = File.ReadAllBytes(Path.Combine(directory, "snapshot.json"));
        var capture = await DependencyCollector.CollectAsync(bundle, client, Path.Combine(directory, "dependencies.capture.json"), new());
        Assert.Equal(1, Fake.Calls.Count(c => c == "component:texture"));
        Assert.Contains(capture.Records, r => r.Id == "texture" && r.Status == "captured");
        Assert.Contains(capture.Records, r => r.Id == "opaque" && r.Status == "opaque");
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "snapshot.json")));
        var loaded = BundleAnalysis.Load(directory); var texture = Key(loaded, "texture");
        Assert.Equal("external", loaded.Get(texture).Kind);
        Assert.Contains(loaded.Find(text: "resdb:///example"), r => r.GetProperty("entity").GetProperty("key").GetString() == texture);
        var url = loaded.Inspect(texture, [new(Member: "URL")]).Single();
        var source = url.GetProperty("source");
        Assert.EndsWith("/component/members/URL", source.GetProperty("pointer").GetString());
        Assert.Empty(Fake.Writes); Assert.Empty(Fake.SlotWrites);
        Assert.Empty((await new WorldService(client, "[test]").PlanApplyAsync(ApplyDocument.Load(Path.Combine(directory, "manifest.json")))).Changes);
    }

    [Fact]
    public async Task DependencyCollectionRejectsNewSessionsBeforeReadingInstances()
    {
        var bundle = await Import(); Fake.Session = "restarted";
        var error = await Assert.ThrowsAsync<RLoopException>(() => DependencyCollector.CollectAsync(bundle, client, Path.Combine(directory, "new.json"), new()));
        Assert.Equal("IMPORT_SESSION_CHANGED", error.Code); Assert.Empty(Fake.Calls);
        Assert.False(File.Exists(Path.Combine(directory, "new.json")));
    }

    [Theory]
    [InlineData(1, 2000, "depth")]
    [InlineData(3, 1, "objects")]
    public async Task CollectionLimitsAreExplicitAndCannotExpandTheWorld(int depth, int objects, string reason)
    {
        var bundle = await Import();
        var capture = await DependencyCollector.CollectAsync(bundle, client, Path.Combine(directory, "limited.json"), new(depth, objects));
        Assert.Equal(reason, capture.StopReason);
        Assert.True(capture.Records.Count <= objects);
        Assert.DoesNotContain("slot:Root", Fake.Calls);
        if (depth == 1) { Assert.DoesNotContain("component:texture", Fake.Calls); Assert.Contains(capture.Records, r => r.Status == "depth-limit"); }
    }

    [Fact]
    public async Task ByteAndTimeBudgetsProduceBoundedPartialObservations()
    {
        var bundle = await Import(); Fake.Large = true;
        var output = Path.Combine(directory, "small.json");
        var bytes = await DependencyCollector.CollectAsync(bundle, client, output, new(MaxBytes: 4096));
        Assert.Equal("bytes", bytes.StopReason); Assert.True(new FileInfo(output).Length <= 4096);
        Assert.Contains(bytes.Records, r => r.Status == "byte-limit");
        Fake.Large = false; Fake.Delay = true;
        var time = await DependencyCollector.CollectAsync(bundle, client, Path.Combine(directory, "time.json"), new(Seconds: 1));
        Assert.Equal("time", time.StopReason); Assert.Contains(time.Records, r => r.Status == "time-limit");
    }

    [Fact]
    public async Task SidecarCannotBeAttachedToAnotherSnapshotOrOverwriteOwnedData()
    {
        var bundle = await Import(); var path = Path.Combine(directory, "dependencies.capture.json");
        var capture = await DependencyCollector.CollectAsync(bundle, client, path, new());
        File.WriteAllText(path, JsonSerializer.Serialize(capture with { SnapshotSha256 = "wrong" }, BundleAnalysis.Json));
        Assert.Equal("BUNDLE_DEPENDENCY_INVALID", Assert.Throws<RLoopException>(() => BundleAnalysis.Load(directory)).Code);
        capture.Records.Add(new("s000000", "world", "opaque", DateTimeOffset.UtcNow, 1));
        File.WriteAllText(path, JsonSerializer.Serialize(capture, BundleAnalysis.Json));
        Assert.Equal("BUNDLE_DEPENDENCY_INVALID", Assert.Throws<RLoopException>(() => BundleAnalysis.Load(directory)).Code);
    }

    [Fact]
    public async Task ExtractKeepsAncestorsStatesAndUnresolvedBoundariesWithoutBecomingAnApplyManifest()
    {
        var bundle = await Import(); var output = Path.Combine(directory, "excerpt.json");
        bundle.Extract(bundle.Snapshot.RootKey, true, 1, 100, 100000, output);
        using var excerpt = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("analysis-excerpt", excerpt.RootElement.GetProperty("kind").GetString());
        Assert.NotEmpty(excerpt.RootElement.GetProperty("unresolved").EnumerateArray());
        Assert.Contains(excerpt.RootElement.GetProperty("entities").EnumerateArray(), e => e.GetProperty("component").ValueKind != JsonValueKind.Null &&
            e.GetProperty("component").GetProperty("members").TryGetProperty("Enabled", out var enabled) && enabled.GetProperty("value").ValueKind == JsonValueKind.False);
        Assert.Throws<IOException>(() => bundle.Extract(bundle.Snapshot.RootKey, false, 0, 100, 100000, output));
        Assert.Throws<RLoopException>(() => bundle.Extract(bundle.Snapshot.RootKey, true, 1, 1, 100000, output + ".small"));
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task CliQueriesNeverConnectAndBooleanFlagsDoNotConsumeBundleArguments()
    {
        var bundle = await Import();
        Assert.Equal(0, await Program.Main(["bundle", "summary", directory, "--url", "ws://127.0.0.1:1", "--json"]));
        Assert.Equal(0, await Program.Main(["bundle", "inspect", directory, "--key", Key(bundle, "source"), "--member", "Enabled", "--url", "ws://127.0.0.1:1", "--json"]));
        var parsed = ParsedArguments.Parse(["bundle", "extract", "--include-dependencies", directory]);
        Assert.Equal(directory, parsed.Positionals[2]);
        Assert.Empty(Fake.Calls);
    }

    [Fact]
    public async Task OversizedRowsAndMissingExplicitSidecarsFailWithoutSkippingEvidence()
    {
        var bundle = await Import();
        Assert.Equal("BUNDLE_ROW_TOO_LARGE", Assert.Throws<RLoopException>(() =>
            bundle.Page([JsonSerializer.SerializeToElement(new string('x', 5000))], maxBytes: 1024)).Code);
        Assert.Equal("BUNDLE_DEPENDENCY_MISSING", Assert.Throws<RLoopException>(() =>
            BundleAnalysis.Load(directory, Path.Combine(directory, "dependencies.capture.json"))).Code);
        var part = Path.Combine(directory, "snapshot", "part-0000.json");
        File.AppendAllText(part, " ");
        Assert.Equal("IMPORT_SNAPSHOT_CHANGED", Assert.Throws<RLoopException>(() => BundleAnalysis.Load(directory)).Code);
    }

    [Fact]
    public async Task LegacySchemaOneIsReadableOfflineAndDuplicateNamesRemainSeparate()
    {
        var other = DispatchProxy.Create<IResoniteClient, ExistingContentTests.ImportClient>();
        // A separate client fixture includes same-name siblings and an unavailable Component type.
        await new ExistingContentService(other).ImportAsync("world", directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifest = ApplyDocument.Load(manifestPath);
        var snapshotPath = Path.Combine(directory, "snapshot.json");
        var snapshot = ExistingBundleFiles.ReadSnapshot(snapshotPath, File.ReadAllText(snapshotPath)) with { SchemaVersion = 1, References = null };
        var text = JsonSerializer.Serialize(snapshot, BundleAnalysis.Json);
        File.WriteAllText(snapshotPath, text);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with { Existing = manifest.Existing! with { Sha256 = ExistingBundleFiles.Hash(text) } }, BundleAnalysis.Json));
        var bundle = BundleAnalysis.Load(directory);
        var duplicates = bundle.Find(name: "Duplicate").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.NotEqual(duplicates[0].GetProperty("key").GetString(), duplicates[1].GetProperty("key").GetString());
        Assert.Equal(duplicates[0].GetProperty("path").GetRawText(), duplicates[1].GetProperty("path").GetRawText());
        Assert.Contains(bundle.Snapshot.Types, t => t.Value.Definition is null);
    }

    public class AnalysisClient : ExistingReferenceTests.ReferenceClient
    {
        public string Session = "world-session";
        public List<string> Calls = [];
        public bool Large, Delay;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            if (method!.Name == nameof(IResoniteClient.GetWorldSessionIdAsync)) return Task.FromResult<string?>(Session);
            if (method.Name == nameof(IResoniteClient.GetSlotAsync)) Calls.Add("slot:" + args[0]);
            if (method.Name == nameof(IResoniteClient.GetComponentAsync))
            {
                var id = (string)args[0]!; Calls.Add("component:" + id);
                if (id is "asset-a" or "asset-b" or "texture")
                {
                    if (Delay) return Wait((CancellationToken)args[1]!);
                    var members = id == "texture" ? new Dictionary<string, MemberValue>
                    { ["URL"] = new("field", "texture-url", "string", JsonValue.Create("resdb:///example")), ["Back"] = new("reference", "back", TargetId: "asset-a") }
                    : new Dictionary<string, MemberValue>
                    { ["Nested"] = new("syncObject", id + "-nested", Members: new Dictionary<string, MemberValue>
                        { ["Textures"] = new("list", id + "-list", Elements: [new("reference", id + "-ref", TargetId: "texture")]) }) };
                    if (Large) members["Huge"] = new("field", id + "-huge", "string", JsonValue.Create(new string('x', 10000)));
                    return Task.FromResult(new ComponentInfo(id, "Asset", members));
                }
            }
            return base.Invoke(method, args);
        }
        private static async Task<ComponentInfo> Wait(CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
