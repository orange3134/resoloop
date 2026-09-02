using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class ResoniteLinkEndToEndTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReparentsIsolatedSlotWithoutChangingItsId()
    {
        if (Environment.GetEnvironmentVariable("RLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var name = "RLoop_Test_Reparent_" + Guid.NewGuid().ToString("N")[..8];
        string? rootId = null;
        try
        {
            rootId = await client.CreateSlotAsync(new SlotCreateRequest("Root", name));
            var sourceId = await client.CreateSlotAsync(new SlotCreateRequest(rootId, "Source"));
            var destinationId = await client.CreateSlotAsync(new SlotCreateRequest(rootId, "Destination"));
            var movedId = await client.CreateSlotAsync(new SlotCreateRequest(sourceId, "Moved", new Vector3Value(1, 2, 3)));

            await client.UpdateSlotAsync(new SlotUpdateRequest(movedId, ParentId: destinationId));

            var source = await client.GetSlotAsync(sourceId, 1, false);
            var destination = await client.GetSlotAsync(destinationId, 1, false);
            var moved = Assert.Single(destination.Children);
            Assert.Empty(source.Children);
            Assert.Equal(movedId, moved.Id);
            Assert.Equal(destinationId, moved.ParentId);
        }
        finally
        {
            if (rootId is not null) await client.DeleteSlotAsync(rootId);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyRelocatesOwnershipRootAndPrunesItsOldChild()
    {
        if (Environment.GetEnvironmentVariable("RLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var containerName = "RLoop_Test_RootMove_" + suffix;
        var directory = Path.Combine(Path.GetTempPath(), "rloop-live-root-move-" + suffix);
        Directory.CreateDirectory(directory);
        var initialPath = Path.Combine(directory, "initial.json");
        var desiredPath = Path.Combine(directory, "desired.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(initialPath, $$$"""
            { "schemaVersion":"1", "ownership":{"key":"live-root-move-{{{suffix}}}"},
              "slot":{"key":"root","name":"Managed","parent":"Root/{{{containerName}}}/ParentA",
                      "position":[2,0,0],"relocationTransform":"world"},
              "children":[{"slot":{"key":"stale","name":"Stale"}}] }
            """);
        await File.WriteAllTextAsync(desiredPath, $$$"""
            { "schemaVersion":"1", "ownership":{"key":"live-root-move-{{{suffix}}}"},
              "slot":{"key":"root","name":"Managed","parent":"Root/{{{containerName}}}/ParentB",
                      "position":[2,0,0],"relocationTransform":"world"}, "children":[] }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        string? containerId = null;
        try
        {
            containerId = await client.CreateSlotAsync(new SlotCreateRequest("Root", containerName));
            var parentAId = await client.CreateSlotAsync(new SlotCreateRequest(containerId, "ParentA", new Vector3Value(10, 0, 0)));
            var parentBId = await client.CreateSlotAsync(new SlotCreateRequest(containerId, "ParentB", new Vector3Value(20, 0, 0)));
            var world = new WorldService(client);
            var initial = await world.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(statePath));

            var applied = await world.ApplyAsync(ApplyDocument.Load(desiredPath),
                new ApplyOptions(statePath, Prune: true, ConfirmDeletes: true));

            Assert.Equal(initial.SlotId, applied.SlotId);
            Assert.Equal(1, applied.SlotsUpdated);
            Assert.Equal(1, applied.SlotsDeleted);
            Assert.Empty((await client.GetSlotAsync(parentAId, 1, false)).Children);
            var moved = Assert.Single((await client.GetSlotAsync(parentBId, 2, false)).Children);
            Assert.Equal(initial.SlotId, moved.Id);
            Assert.NotNull(moved.Position);
            Assert.Equal(-8f, moved.Position!.X, 3);
            Assert.Empty(moved.Children);
        }
        finally
        {
            if (containerId is not null) await client.DeleteSlotAsync(containerId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreatesMutatesAndCleansIsolatedSlot()
    {
        if (Environment.GetEnvironmentVariable("RLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var session = await client.GetSessionInfoAsync();
        Assert.True(session.Connected);
        var name = "RLoop_Test_Integration_" + Guid.NewGuid().ToString("N")[..8];
        string? slotId = null;
        try
        {
            slotId = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(0, 1.5f, 2)));
            await client.UpdateSlotAsync(new SlotUpdateRequest(slotId, Position: new Vector3Value(1, 2, 3), Scale: new Vector3Value(0.5f, 0.5f, 0.5f)));
            var created = await client.GetSlotAsync(slotId, 0, false);
            Assert.Equal(name, created.Name);
            Assert.Equal(new Vector3Value(1, 2, 3), created.Position);
            var types = await client.SearchComponentTypesAsync("Grabbable", 10);
            Assert.NotEmpty(types);
            var component = await client.AddComponentAsync(slotId, types[0], new Dictionary<string, string> { ["Scalable"] = "true" });
            await client.SetComponentMemberAsync(component.Id, "Scalable", "false");
            var inspected = await client.GetComponentAsync(component.Id);
            Assert.Contains("Scalable", inspected.Members.Keys);
        }
        finally
        {
            if (slotId is not null) await client.DeleteSlotAsync(slotId);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyCheckpointsAndConvergesWithoutSecondRunWrites()
    {
        if (Environment.GetEnvironmentVariable("RLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "RLoop_Test_Apply_" + suffix;
        var directory = Path.Combine(Path.GetTempPath(), "rloop-live-" + suffix);
        Directory.CreateDirectory(directory);
        var documentPath = Path.Combine(directory, "apply.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(documentPath, $$"""
            {
              "schemaVersion": "1",
              "ownership": { "key": "live-{{suffix}}" },
              "slot": { "key": "root", "name": "{{name}}", "parent": "Root", "position": [0, 1.5, 2] },
              "components": [
                { "key": "grabbable", "type": "FrooxEngine.Grabbable", "fields": { "Scalable": true } }
              ]
            }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var world = new WorldService(client);
        string? slotId = null;
        try
        {
            var first = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath, Profile: true));
            slotId = first.SlotId;
            Assert.Equal(1, first.SlotsCreated);
            Assert.Equal(1, first.ComponentsAdded);
            Assert.True(File.Exists(statePath));
            Assert.Equal(slotId, await world.ResolveSlotSelectorAsync("$slot:root", statePath));
            Assert.Equal(Assert.Single((await client.GetSlotAsync(slotId, 0, false)).Components).Id,
                await world.ResolveComponentSelectorAsync("$component:grabbable", statePath));

            var second = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath, Profile: true));
            Assert.Equal(0, second.SlotsCreated);
            Assert.Equal(0, second.SlotsUpdated);
            Assert.Equal(0, second.ComponentsAdded);
            Assert.Equal(0, second.ComponentsUpdated);
            Assert.Equal(2, second.Profile!.NoOps);
            var audit = await world.AuditItemAsync(slotId, strict: true);
            Assert.True(audit.Portable);
            Assert.True(audit.HasGrabbable);
        }
        finally
        {
            if (slotId is not null) await client.DeleteSlotAsync(slotId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyPreservesTransformMigratesKeysAndPrunesParentAsOneOperation()
    {
        if (Environment.GetEnvironmentVariable("RLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "RLoop_Test_Policy_" + suffix;
        var directory = Path.Combine(Path.GetTempPath(), "rloop-live-policy-" + suffix);
        Directory.CreateDirectory(directory);
        var initialPath = Path.Combine(directory, "initial.json");
        var desiredPath = Path.Combine(directory, "desired.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(initialPath, $$$"""
            {
              "schemaVersion":"1", "ownership":{"key":"live-policy-{{{suffix}}}"},
              "slot":{"key":"old-root","name":"{{{name}}}","parent":"Root","position":[0,1,2]},
              "components":[{"key":"old-grabbable","type":"FrooxEngine.Grabbable","fields":{"Scalable":true}}],
              "children":[{
                "slot":{"key":"obsolete-parent","name":"Obsolete"},
                "components":[{"key":"obsolete-component","type":"FrooxEngine.Grabbable","fields":{"Scalable":true}}]
              }]
            }
            """);
        await File.WriteAllTextAsync(desiredPath, $$$"""
            {
              "schemaVersion":"1", "ownership":{"key":"live-policy-{{{suffix}}}"},
              "slot":{"key":"new-root","migrateFrom":"old-root","name":"{{{name}}}","parent":"Root",
                      "position":[0,1,2],"preserveWorldTransform":true},
              "components":[{"key":"new-grabbable","migrateFrom":"old-grabbable","type":"FrooxEngine.Grabbable","fields":{"Scalable":true}}],
              "children":[]
            }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var world = new WorldService(client);
        string? slotId = null;
        try
        {
            var initial = await world.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(statePath));
            slotId = initial.SlotId;
            await client.UpdateSlotAsync(new SlotUpdateRequest(slotId, Position: new Vector3Value(5, 6, 7)));

            var plan = await world.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(statePath));
            Assert.DoesNotContain(plan.Operations, operation => operation.Action == "create");
            Assert.Single(plan.Operations, operation => operation.Action == "delete" && operation.Kind == "slot");
            Assert.DoesNotContain(plan.Operations, operation => operation.Action == "delete" && operation.Kind == "component");

            var applied = await world.ApplyAsync(ApplyDocument.Load(desiredPath),
                new ApplyOptions(statePath, Prune: true, ConfirmDeletes: true));
            var inspected = await client.GetSlotAsync(slotId, 1, false);
            Assert.Equal(new Vector3Value(5, 6, 7), inspected.Position);
            Assert.Empty(inspected.Children);
            Assert.Equal(1, applied.SlotsDeleted);
            Assert.Equal(0, applied.ComponentsDeleted);
            Assert.Equal(slotId, applied.SlotId);
        }
        finally
        {
            if (slotId is not null) await client.DeleteSlotAsync(slotId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
