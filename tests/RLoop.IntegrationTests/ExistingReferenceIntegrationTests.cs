using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class ExistingReferenceIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExternalAndNestedReferenceEditsPreserveOwnershipAndConverge()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(30), ct);
        const string muxType = "[FrooxEngine]FrooxEngine.ValueMultiplexer<float>";
        const string copyType = "[FrooxEngine]FrooxEngine.ValueCopy<float>";
        const string rendererType = "[FrooxEngine]FrooxEngine.MeshRenderer";
        const string materialType = "[FrooxEngine]FrooxEngine.UI_UnlitMaterial";
        Assert.Contains((await client.DescribeComponentTypeAsync(muxType, ct)).Members, m => m.Name == "Values" && m.Kind == "list");
        Assert.Contains((await client.DescribeComponentTypeAsync(copyType, ct)).Members, m => m.Name == "Source" && m.Kind == "reference");
        Assert.Contains((await client.DescribeComponentTypeAsync(rendererType, ct)).Members, m => m.Name == "Materials" && m.Kind == "list");
        _ = await client.DescribeComponentTypeAsync(materialType, ct);
        var name = "ResoLoop_Test_ExistingReferences_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), name);
        string? root = null;
        try
        {
            root = await client.CreateSlotAsync(new("Root", name), ct);
            var owned = await client.CreateSlotAsync(new(root, "Owned"), ct);
            var external = await client.CreateSlotAsync(new(root, "ExternalDependencies"), ct);
            var first = await client.AddComponentAsync(external, materialType, new Dictionary<string, string>(), ct);
            var second = await client.AddComponentAsync(external, materialType, new Dictionary<string, string>(), ct);
            var mux = await client.AddComponentAsync(owned, muxType, new Dictionary<string, string> { ["Values"] = "[1,2]", ["Enabled"] = "false" }, ct);
            var values = (await client.GetComponentAsync(mux.Id, ct)).Members["Values"].Elements!;
            Assert.Equal(2, values.Count);
            var copy = await client.AddComponentAsync(owned, copyType, new Dictionary<string, string> { ["Source"] = values[0].Id!, ["Enabled"] = "false" }, ct);
            var renderer = await client.AddComponentAsync(owned, rendererType, new Dictionary<string, string> { ["Materials"] = JsonSerializer.Serialize(new[] { first.Id, second.Id }) }, ct);
            var report = await new ExistingContentService(client).ImportAsync(owned, directory, ct: ct);
            Assert.Equal(1, report.Slots);
            Assert.Equal(3, report.Components);
            Assert.Equal(2, report.References!.ExternalTargets);
            Assert.Equal(1, report.References.NestedMemberTargets);
            AssertReadOnly(report.Metrics!);
            var snapshot = JsonSerializer.Deserialize<ExistingSnapshot>(await File.ReadAllTextAsync(report.Snapshot, ct), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var document = ApplyDocument.Load(report.Manifest);
            var world = new WorldService(client, "[resoloop test]");
            Assert.Empty((await world.PlanApplyAsync(document, cancellationToken: ct)).Changes);
            string Component(string id) => snapshot.Components.Single(c => c.Value.Data.Id == id).Key;
            string Edge(string id, string member, int? index = null) => snapshot.References!.Edges.Single(e => e.Value.ComponentKey == Component(id) && e.Value.Path[0].Member == member && (index is null || e.Value.Path[^1].Index == index)).Key;
            document = document with { Existing = document.Existing! with { ReferenceChanges = new Dictionary<string, ExistingReferenceTarget?>
            {
                [Edge(copy.Id, "Source")] = new("member", Component(mux.Id), [new(Member: "Values"), new(Index: 1)]),
                [Edge(renderer.Id, "Materials", 0)] = new("external", snapshot.References!.External.Single(e => e.Value.Id == second.Id).Key)
            } } };
            Assert.Equal(2, (await world.PlanApplyAsync(document, cancellationToken: ct)).Changes.Count);
            client.ResetMetrics();
            Assert.Equal(2, (await world.ApplyAsync(document, cancellationToken: ct)).ComponentsUpdated);
            var mutations = client.SnapshotMetrics().Operations.Where(o => !IsRead(o.Operation)).ToArray();
            Assert.Equal("component.update", Assert.Single(mutations).Operation);
            Assert.Equal(2, mutations[0].Requests);
            Assert.Equal(values[1].Id, (await client.GetComponentAsync(copy.Id, ct)).Members["Source"].TargetId);
            var afterMaterials = (await client.GetComponentAsync(renderer.Id, ct)).Members["Materials"].Elements!;
            Assert.Equal(2, afterMaterials.Count);
            Assert.All(afterMaterials, m => Assert.Equal(second.Id, m.TargetId));
            var originalMaterials = snapshot.Components[Component(renderer.Id)].Data.Members["Materials"].Elements!;
            Assert.Equal(originalMaterials.Select(m => m.Id), afterMaterials.Select(m => m.Id));
            var afterValues = (await client.GetComponentAsync(mux.Id, ct)).Members["Values"].Elements!;
            Assert.Equal(values.Select(m => m.Id), afterValues.Select(m => m.Id));
            Assert.Equal(new[] { 1f, 2f }, afterValues.Select(m => m.Value!.GetValue<float>()));
            Assert.Equal(2, (await client.GetSlotAsync(external, 0, false, ct)).Components.Count);
            client.ResetMetrics();
            Assert.Empty((await world.PlanApplyAsync(document, cancellationToken: ct)).Changes);
            Assert.Equal(0, (await world.ApplyAsync(document, cancellationToken: ct)).ComponentsUpdated);
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
