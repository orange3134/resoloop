using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class ExistingBundleSlotIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SplitFilesResolveNestedReferencesAndAllSlotAttributesWithoutRecreation()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(30), ct);
        const string muxType = "[FrooxEngine]FrooxEngine.ValueMultiplexer<float>";
        const string copyType = "[FrooxEngine]FrooxEngine.ValueCopy<float>";
        Assert.Contains((await client.DescribeComponentTypeAsync(muxType, ct)).Members, m => m.Name == "Values" && m.Kind == "list");
        Assert.Contains((await client.DescribeComponentTypeAsync(copyType, ct)).Members, m => m.Name == "Source" && m.Kind == "reference");
        var name = "ResoLoop_Test_SplitSlots_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), name);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string? root = null;
        try
        {
            root = await client.CreateSlotAsync(new("Root", name), ct);
            var owned = await client.CreateSlotAsync(new(root, "Owned"), ct);
            var child = await client.CreateSlotAsync(new(owned, "NestedTarget"), ct);
            var mux = await client.AddComponentAsync(child, muxType, new Dictionary<string, string> { ["Values"] = "[1,2]", ["Enabled"] = "false" }, ct);
            var values = (await client.GetComponentAsync(mux.Id, ct)).Members["Values"].Elements!;
            var copy = await client.AddComponentAsync(owned, copyType, new Dictionary<string, string> { ["Source"] = values[0].Id!, ["Enabled"] = "false" }, ct);
            var before = await client.GetSlotAsync(owned, 0, false, ct);
            // Slot fields are described by the pinned Slot API; verify actual typed member IDs before editing.
            foreach (var attribute in new[] { "Position", "Rotation", "Scale", "IsActive", "IsPersistent", "Tag", "OrderOffset" })
                Assert.True(before.Members!.TryGetValue(attribute, out var member) && member.Kind == "field" && member.Id is not null);
            var report = await new ExistingContentService(client).ImportAsync(owned, directory, ct: ct, splitSlots: 1);
            Assert.Equal(2, report.Slots); Assert.Equal(2, report.Components);
            AssertReadOnly(report.Metrics!);
            var snapshot = ExistingBundleFiles.ReadSnapshot(report.Snapshot, await File.ReadAllTextAsync(report.Snapshot, ct));
            var document = ApplyDocument.Load(report.Manifest);
            var world = new WorldService(client, "[test]");
            Assert.Empty((await world.PlanApplyAsync(document, cancellationToken: ct)).Changes);
            var muxKey = snapshot.Components.Single(c => c.Value.Data.Id == mux.Id).Key;
            var edge = snapshot.References!.Edges.Single(e => e.Value.Path[0].Member == "Source").Key;
            var referencePart = new ExistingEditFile(1, document.Ownership!.Key, document.Existing!.Sha256,
                ReferenceChanges: new Dictionary<string, ExistingReferenceTarget?> { [edge] = new("member", muxKey, [new(Member: "Values"), new(Index: 1)]) });
            static JsonElement V<T>(T value) => JsonSerializer.SerializeToElement(value);
            var attributes = new Dictionary<string, JsonElement>
            {
                ["Position"] = V(new[] { 1f, 2f, 3f }), ["Rotation"] = V(new[] { 0f, 1f, 0f, 0f }), ["Scale"] = V(new[] { 2f, 3f, 4f }),
                ["IsActive"] = V(false), ["IsPersistent"] = V(false), ["Tag"] = V("${0}:literal"), ["OrderOffset"] = V(42L)
            };
            var slotPart = new ExistingEditFile(1, document.Ownership.Key, document.Existing.Sha256,
                SlotChanges: new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { [snapshot.RootKey] = attributes });
            await File.WriteAllTextAsync(Path.Combine(directory, "wiring.json"), JsonSerializer.Serialize(referencePart, json), ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "attributes.json"), JsonSerializer.Serialize(slotPart, json), ct);
            document = document with { Existing = document.Existing with { EditFiles = ["attributes.json", "wiring.json"] } };
            await File.WriteAllTextAsync(report.Manifest, JsonSerializer.Serialize(document, json), ct);
            document = ApplyDocument.Load(report.Manifest);
            Assert.Equal(2, (await world.PlanApplyAsync(document, cancellationToken: ct)).Updates);
            client.ResetMetrics();
            var applied = await world.ApplyAsync(document, cancellationToken: ct);
            Assert.Equal(1, applied.SlotsUpdated); Assert.Equal(1, applied.ComponentsUpdated);
            var mutations = client.SnapshotMetrics().Operations.Where(o => !IsRead(o.Operation)).ToArray();
            Assert.Equal(2, mutations.Length);
            Assert.Equal(7, Assert.Single(mutations, o => o.Operation == "slot.update").Requests);
            Assert.Equal(1, Assert.Single(mutations, o => o.Operation == "component.update").Requests);
            var after = await client.GetSlotAsync(owned, 1, false, ct);
            Assert.Equal(before.Id, after.Id); Assert.Equal(before.Name, after.Name); Assert.Equal(before.ParentId, after.ParentId);
            Assert.Equal(new Vector3Value(1, 2, 3), after.Position); Assert.Equal(new QuaternionValue(0, 1, 0, 0), after.Rotation);
            Assert.Equal(new Vector3Value(2, 3, 4), after.Scale); Assert.False(after.IsActive); Assert.False(after.IsPersistent);
            Assert.Equal("${0}:literal", after.Tag); Assert.Equal(42L, after.Members!["OrderOffset"].Value!.GetValue<long>());
            Assert.Equal(child, Assert.Single(after.Children).Id); Assert.Equal(copy.Id, Assert.Single(after.Components).Id);
            Assert.Equal(values[1].Id, (await client.GetComponentAsync(copy.Id, ct)).Members["Source"].TargetId);
            Assert.Equal(values.Select(m => m.Id), (await client.GetComponentAsync(mux.Id, ct)).Members["Values"].Elements!.Select(m => m.Id));
            client.ResetMetrics();
            Assert.Empty((await world.PlanApplyAsync(document, cancellationToken: ct)).Changes);
            var repeat = await world.ApplyAsync(document, cancellationToken: ct);
            Assert.Equal(0, repeat.SlotsUpdated); Assert.Equal(0, repeat.ComponentsUpdated);
            AssertReadOnly(client.SnapshotMetrics());
        }
        finally
        {
            if (root is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var current = await client.GetSlotAsync(root, 0, false, cleanup.Token);
                Assert.Equal(root, current.Id); Assert.Equal(name, current.Name); Assert.Equal("Root", current.ParentId); Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root, cleanup.Token);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static bool IsRead(string operation) => operation.EndsWith(".get", StringComparison.Ordinal) || operation.StartsWith("component-types.get-", StringComparison.Ordinal);
    private static void AssertReadOnly(ClientMetrics metrics) => Assert.All(metrics.Operations, operation => Assert.True(IsRead(operation.Operation), operation.Operation));
}
