using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class ExistingContentIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportDuplicateSlotsReconnectAndEditOneFieldWithoutTaggingOrRecreation()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var uri = new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(uri, TimeSpan.FromSeconds(30), ct);
        const string type = "[FrooxEngine]FrooxEngine.Light";
        var reflected = await client.DescribeComponentTypeAsync(type, ct);
        Assert.Contains(reflected.Members, m => m.Name == "Intensity" && m.Kind == "field");
        var name = "ResoLoop_Test_ExistingImport_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), name);
        string? root = null;
        try
        {
            root = await client.CreateSlotAsync(new("Root", name), ct);
            var first = await client.CreateSlotAsync(new(root, "Duplicate"), ct);
            var second = await client.CreateSlotAsync(new(root, "Duplicate"), ct);
            var firstLight = await client.AddComponentAsync(first, type, new Dictionary<string, string> { ["Intensity"] = "1" }, ct);
            var secondLight = await client.AddComponentAsync(second, type, new Dictionary<string, string> { ["Intensity"] = "5" }, ct);
            var report = await new ExistingContentService(client).ImportAsync(root, directory, ct: ct);
            Assert.Equal(3, report.Slots);
            Assert.Equal(2, report.Components);
            Assert.Equal(1, report.DuplicateSiblingGroups);
            AssertReadOnly(report.Metrics!);
            var document = ApplyDocument.Load(report.Manifest);
            await using var reconnected = new ResoniteLinkClientAdapter();
            await reconnected.ConnectAsync(uri, TimeSpan.FromSeconds(30), ct);
            var world = new WorldService(reconnected, "[resoloop integration]");
            var initial = await world.PlanApplyAsync(document, cancellationToken: ct);
            Assert.Empty(initial.Changes);
            AssertReadOnly(reconnected.SnapshotMetrics());
            var snapshot = JsonSerializer.Deserialize<ExistingSnapshot>(await File.ReadAllTextAsync(report.Snapshot, ct),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var key = snapshot.Components.Single(c => c.Value.Data.Id == firstLight.Id).Key;
            document = document with { Existing = document.Existing! with { Changes = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
            { [key] = new Dictionary<string, JsonElement> { ["Intensity"] = JsonSerializer.SerializeToElement(2f) } } } };
            var plan = await world.PlanApplyAsync(document, cancellationToken: ct);
            Assert.Equal("Intensity", Assert.Single(Assert.Single(plan.Changes).Members!));
            reconnected.ResetMetrics();
            var applied = await world.ApplyAsync(document, cancellationToken: ct);
            Assert.Equal(1, applied.ComponentsUpdated);
            var mutations = reconnected.SnapshotMetrics().Operations.Where(o => o.Operation is "component.update" or "component.add" or "slot.add" or "slot.update" or "slot.remove" or "component.remove").ToArray();
            Assert.Equal("component.update", Assert.Single(mutations).Operation);
            Assert.Equal(1, mutations[0].Requests);
            Assert.Equal(2, (await reconnected.GetComponentAsync(firstLight.Id, ct)).Members["Intensity"].Value!.GetValue<float>());
            Assert.Equal(5, (await reconnected.GetComponentAsync(secondLight.Id, ct)).Members["Intensity"].Value!.GetValue<float>());
            Assert.Empty((await world.PlanApplyAsync(document, cancellationToken: ct)).Changes);
            var after = await reconnected.GetSlotAsync(root, 2, true, ct);
            Assert.Equal(2, after.Children.Count);
            Assert.All(after.Children, s => Assert.Equal("Duplicate", s.Name));
            Assert.Empty(after.Components);
            Assert.All(after.Children, s => Assert.Single(s.Components));
        }
        finally
        {
            if (root is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var owned = await client.GetSlotAsync(root, 0, false, cleanup.Token);
                Assert.Equal(root, owned.Id);
                Assert.Equal(name, owned.Name);
                Assert.Equal("Root", owned.ParentId);
                Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root, cleanup.Token);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AssertReadOnly(ClientMetrics metrics) => Assert.All(metrics.Operations, operation =>
        Assert.True(operation.Operation.EndsWith(".get", StringComparison.Ordinal) ||
                    operation.Operation.StartsWith("component-types.get-", StringComparison.Ordinal), operation.Operation));
}
