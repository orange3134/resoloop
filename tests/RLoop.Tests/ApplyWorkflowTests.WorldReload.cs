using System.Security.Cryptography;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private const string ReloadedWorld = """
        {"schemaVersion":"1","ownership":{"key":"reload"},"slot":{"key":"root","name":"OwnedWorld","parent":"Root"},
         "components":[{"key":"renderer","type":"Test.Source","fields":{"Target":"$ref:foundation-target"}}],
         "children":[{"slot":{"key":"terrain","name":"Terrain"},
           "children":[{"slot":{"key":"foundation","name":"foundation"},
             "components":[{"key":"foundation-target","type":"Test.Target","fields":{"Enabled":true}}]}]}]}
        """;

    [Fact]
    public async Task CrossSlotReferencesResolveAfterWorldReloadWithoutMutation()
    {
        var document = ReloadDocument("reload", ReloadedWorld);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "reload.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        var oldFoundationId = client.Root.Children.Single().Children.Single().Children.Single().Id;
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
        Assert.Equal(0, client.Writes);
        Assert.NotEqual(oldFoundationId, client.Root.Children.Single().Children.Single().Children.Single().Id);
        Assert.Equal(0, plan.Creates);
        Assert.Equal(0, plan.Deletes);
        Assert.Empty(plan.Changes);

        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, applied.SlotsCreated);
        Assert.Equal(0, applied.ComponentsAdded);
        Assert.Equal(0, applied.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task PruneAfterWorldReloadPlansTheStaleSlotOnce()
    {
        var document = ReloadDocument("reload-prune", ReloadedWorld);
        var trimmed = ReloadDocument("reload-prune-trimmed", """
            {"schemaVersion":"1","ownership":{"key":"reload"},"slot":{"key":"root","name":"OwnedWorld","parent":"Root"},
             "children":[{"slot":{"key":"terrain","name":"Terrain"}}]}
            """);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "reload-prune.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(trimmed, new ApplyOptions(state, Prune: true));

        var deletedSlot = Assert.Single(plan.Operations, operation => operation.Action == "delete" && operation.Kind == "slot");
        Assert.Equal("foundation", deletedSlot.Key);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task DistinctSlotsOnTheSameManagedPathStayAmbiguousAfterWorldReload()
    {
        var document = ReloadDocument("reload-ambiguous", ReloadedWorld);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "reload-ambiguous.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ReloadWorld("session-reloaded");
        var terrain = client.Root.Children.Single().Children.Single();
        await client.CreateSlotAsync(new SlotCreateRequest(terrain.Id, "foundation"));
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("APPLY_TARGET_AMBIGUOUS", error.Code);
        var ids = Assert.IsAssignableFrom<IEnumerable<string>>(error.Context!["ids"]).ToArray();
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("same-session")]
    [InlineData("new-session")]
    [InlineData("world-reload")]
    public async Task RelocatableRootMovedDeeperInsideTheParentKeepsItsDescendants(string reconnect)
    {
        var (service, client, state) = await ApplyAndMoveRelocatableToolAsync("relocatable-deeper-" + reconnect, reconnect);
        // A new name declares a new path, so the plan relocates the item instead of stopping at APPLY_RUNTIME_RELOCATABLE_ACTIVE.
        var renamed = ReloadDocument("relocatable-deeper-" + reconnect + "-renamed", RelocatableTool("RenamedTool"));

        var plan = await service.PlanApplyAsync(renamed, new ApplyOptions(state));

        Assert.Equal(0, plan.Creates);
        Assert.Contains(plan.Operations, operation => operation.Action == "relocate" && operation.Kind == "slot" && operation.Key == "root");
        Assert.Contains(plan.Operations, operation => operation.Action == "no-op" && operation.Kind == "slot" && operation.Key == "tip");
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task PruneAfterWorldReloadDeletesTheDescendantOfARelocatableRootMovedDeeper()
    {
        var (service, client, state) = await ApplyAndMoveRelocatableToolAsync("relocatable-deeper-prune", "world-reload");
        var trimmed = ReloadDocument("relocatable-deeper-prune-trimmed", """
            {"schemaVersion":"1","ownership":{"key":"relocatable"},"slot":{"key":"root","name":"RenamedTool","parent":"Root","runtimeRelocatable":true},
             "components":[{"key":"identity","type":"Test.Target","fields":{"Enabled":true},"identityFields":["Enabled"]}],
             "children":[{"slot":{"key":"grip","name":"Grip"}}]}
            """);

        var plan = await service.PlanApplyAsync(trimmed, new ApplyOptions(state, Prune: true));

        var deletedSlot = Assert.Single(plan.Operations, operation => operation.Action == "delete" && operation.Kind == "slot");
        Assert.Equal("tip", deletedSlot.Key);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task RelocatableRootConvergesAfterWorldReloadWithoutMove()
    {
        var document = ReloadDocument("relocatable-stay", RelocatableTool("ManagedTool"));
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "relocatable-stay.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(0, plan.Creates);
        Assert.Equal(0, plan.Deletes);
        Assert.Empty(plan.Changes);
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, applied.SlotsCreated);
        Assert.Equal(0, applied.ComponentsAdded);
        Assert.Equal(0, applied.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
    }

    private static string RelocatableTool(string name) => $$$"""
        {"schemaVersion":"1","ownership":{"key":"relocatable"},"slot":{"key":"root","name":"{{{name}}}","parent":"Root","runtimeRelocatable":true},
         "components":[{"key":"identity","type":"Test.Target","fields":{"Enabled":true},"identityFields":["Enabled"]}],
         "children":[{"slot":{"key":"grip","name":"Grip"},
           "children":[{"slot":{"key":"tip","name":"Tip"},
             "components":[{"key":"tip-target","type":"Test.Target","fields":{"Enabled":true}}]}]}]}
        """;

    private async Task<(WorldService Service, FakeResoniteClient Client, string State)> ApplyAndMoveRelocatableToolAsync(
        string name, string reconnect)
    {
        var document = ReloadDocument(name, RelocatableTool("ManagedTool"));
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        // Root/Shelf/ManagedTool stays inside the parent snapshot, but Tip falls below the depth that snapshot reads.
        var shelf = await client.CreateSlotAsync(new SlotCreateRequest("Root", "Shelf"));
        await client.UpdateSlotAsync(new SlotUpdateRequest(applied.SlotId, ParentId: shelf));
        if (reconnect == "world-reload") client.ReloadWorld("session-reloaded");
        else if (reconnect == "new-session") client.SessionId = "session-2";
        client.ResetWriteCounts();
        return (service, client, state);
    }

    private ApplyDocument ReloadDocument(string name, string json)
    {
        var path = Path.Combine(_root, name + ".json");
        File.WriteAllText(path, json);
        return ApplyDocument.Load(path);
    }
}
