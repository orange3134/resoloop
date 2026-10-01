using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("prune")]
    [InlineData("move")]
    [InlineData("move-up")]
    public async Task RemovingAComponentSavesTheIndexesOfItsSiblingsForAWorldReload(string removal)
    {
        var name = "indexes-" + removal;
        // move-up moves v1 to the parent Slot, which apply handles before the Slot that v1 leaves.
        var full = ReloadDocument(name, removal == "move-up"
            ? IndexedSiblings(name, [], ["v1", "v2", "v3"])
            : IndexedSiblings(name, ["v1", "v2", "v3"]));
        var changed = ReloadDocument(name + "-changed", removal switch
        {
            "prune" => IndexedSiblings(name, ["v2", "v3"]),
            "move" => IndexedSiblings(name, ["v2", "v3"], ["v1"]),
            _ => IndexedSiblings(name, ["v1"], ["v2", "v3"])
        });
        var client = new FakeResoniteClient(full);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(full, new ApplyOptions(state));
        await service.ApplyAsync(changed, new ApplyOptions(state, Prune: removal == "prune", ConfirmDeletes: removal == "prune"));
        AssertSavedIndexesMatchLayout(client, state);
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();

        var applied = await service.ApplyAsync(changed, new ApplyOptions(state));

        Assert.Equal(0, applied.ComponentsAdded);
        Assert.Equal(0, applied.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task AnUndeclaredSiblingKeepsItsIndexWhenAnotherComponentLeavesTheSlot()
    {
        const string name = "indexes-undeclared";
        var full = ReloadDocument(name, IndexedSiblings(name, ["v1", "v2", "v3"]));
        // v1 moves to another Slot and v3 is no longer declared, but apply keeps it without --prune.
        var moved = ReloadDocument(name + "-moved", IndexedSiblings(name, ["v2"], ["v1"]));
        var client = new FakeResoniteClient(full);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(full, new ApplyOptions(state));
        await service.ApplyAsync(moved, new ApplyOptions(state));
        AssertSavedIndexesMatchLayout(client, state);
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(moved, new ApplyOptions(state, Prune: true));

        Assert.Equal("v3", Assert.Single(plan.Operations, operation => operation.Action == "delete").Key);
        await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task APruneCancelledWhileSavingIndexesKeepsTheRemovedKeyOutOfTheState()
    {
        const string name = "indexes-cancelled-prune";
        var full = ReloadDocument(name, IndexedSiblings(name, ["v1", "v2", "v3"]));
        var trimmed = ReloadDocument(name + "-trimmed", IndexedSiblings(name, ["v2", "v3"]));
        var client = new FakeResoniteClient(full);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(full, new ApplyOptions(state));
        using var cancellation = new CancellationTokenSource();
        client.Cancellation = cancellation;
        client.CancelAfterWrites = 1; // cancel right after the removal of v1 lands
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(trimmed, new ApplyOptions(state, Prune: true, ConfirmDeletes: true), cancellation.Token));

        Assert.Equal("APPLY_CANCELLED", error.Code);
        Assert.Equal(new[] { "v2", "v3" },
            JsonNode.Parse(File.ReadAllText(state))!["components"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        client.CancelAfterWrites = null;
        await service.ApplyAsync(trimmed, new ApplyOptions(state));
        AssertSavedIndexesMatchLayout(client, state);
        client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();
        await service.ApplyAsync(trimmed, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task UndeclaredKeysStayInTheDeletionPlanAfterAnApplyInTheReloadedSession()
    {
        var document = ReloadDocument("bindings", ReloadedWorld);
        var trimmed = ReloadDocument("bindings-trimmed", """
            {"schemaVersion":"1","ownership":{"key":"reload"},"slot":{"key":"root","name":"OwnedWorld","parent":"Root"},
             "children":[{"slot":{"key":"terrain","name":"Terrain"}}]}
            """);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "bindings.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ReloadWorld("session-reloaded");
        var first = await service.PlanApplyAsync(trimmed, new ApplyOptions(state, Prune: true));
        await service.ApplyAsync(trimmed, new ApplyOptions(state));

        var second = await service.PlanApplyAsync(trimmed, new ApplyOptions(state, Prune: true));

        string[] Deletions(ApplyPlanResult plan) => plan.Operations.Where(operation => operation.Action == "delete")
            .Select(operation => operation.Kind + ":" + operation.Key).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["component:renderer", "slot:foundation"], Deletions(first));
        Assert.Equal(Deletions(first), Deletions(second));
        await service.ApplyAsync(trimmed, new ApplyOptions(state, Prune: true, ConfirmDeletes: true));
        var owned = client.Root.Children.Single();
        Assert.Empty(owned.Components);
        Assert.Empty(owned.Children.Single().Children);
    }

    [Fact]
    public async Task TheFirstApplyAfterAReloadSavesTheIndexOfAnUndeclaredSibling()
    {
        const string name = "indexes-after-reload";
        var full = ReloadDocument(name, IndexedSiblings(name, ["v1", "v2", "v3"]));
        var moved = ReloadDocument(name + "-moved", IndexedSiblings(name, ["v2"], ["v1"]));
        var client = new FakeResoniteClient(full);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(full, new ApplyOptions(state));
        client.ReloadWorld("session-2");
        await service.ApplyAsync(moved, new ApplyOptions(state));
        AssertSavedIndexesMatchLayout(client, state);
        client.ReloadWorld("session-3");
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(moved, new ApplyOptions(state, Prune: true));

        Assert.Equal("v3", Assert.Single(plan.Operations, operation => operation.Action == "delete").Key);
        await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // An interrupted move can leave its source Component behind (an existing upstream issue that this change does not
    // address). Wherever the apply stops, the next apply in the same session must still save indexes that bind every key
    // to its own Component after a world reload, and an apply that finished must have saved them already. A pruned
    // Component whose removal response is lost stays in the state even after later applies in the same session; that is a
    // known limitation, so this test covers only moves.
    [Theory]
    [InlineData(false, "down")]
    [InlineData(true, "down")]
    [InlineData(false, "up")]
    [InlineData(true, "up")]
    public async Task AnApplyInterruptedAtAnyWriteSavesBindableIndexesOnTheNextApply(bool responseLost, string direction)
    {
        for (var write = 1; ; write++)
        {
            var name = $"indexes-interrupted-{responseLost}-{direction}-{write}";
            // "up" moves v1 to the parent Slot, which apply handles first. A same-type Component on that Slot already stops
            // this move with an ownership conflict before this change, so the destination holds none.
            var full = ReloadDocument(name, direction == "up"
                ? IndexedSiblings(name, [], ["v1", "v2", "v3"])
                : IndexedSiblings(name, ["v1", "v2", "v3"]));
            var moved = ReloadDocument(name + "-moved", direction == "up"
                ? IndexedSiblings(name, ["v1"], ["v2", "v3"])
                : IndexedSiblings(name, ["v2"], ["v1"]));
            var client = new FakeResoniteClient(full);
            var service = new WorldService(client);
            var state = Path.Combine(_root, name + ".state.json");
            await service.ApplyAsync(full, new ApplyOptions(state));
            client.ResetWriteCounts();
            if (responseLost) client.LoseResponseOnWrite = write;
            else client.FailOnWrite = write;

            var interrupted = await InterruptedAsync(() => service.ApplyAsync(moved, new ApplyOptions(state)));
            client.LoseResponseOnWrite = null;
            client.FailOnWrite = null;
            if (!interrupted) AssertSavedIndexesMatchLayout(client, state);
            await service.ApplyAsync(moved, new ApplyOptions(state));

            AssertSavedIndexesMatchLayout(client, state);
            client.ReloadWorld("session-reloaded");
            client.ResetWriteCounts();
            await service.ApplyAsync(moved, new ApplyOptions(state));
            Assert.True(client.Writes == 0, $"write {write}: the apply after the reload wrote {client.Writes} time(s).");
            if (!interrupted) break;
        }
    }

    // The fake client reports an injected failure or a lost response as an IOException.
    private static async Task<bool> InterruptedAsync(Func<Task> apply)
    {
        try
        {
            await apply();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // Same-type siblings without identityFields: after a world reload only their saved indexes tell them apart.
    // Each key "vN" declares Value N, so binding a key to a sibling's Component shows up as a field write.
    private static string IndexedSiblings(string ownership, string[] onRoot, string[]? onChild = null)
    {
        static string Components(IEnumerable<string> keys) => string.Join(",", keys.Select(key =>
            $$$"""{"key":"{{{key}}}","type":"Test.Indexed","fields":{"Value":{{{key[1..]}}}}}"""));
        var child = onChild is null ? "" :
            $$$""","children":[{"slot":{"key":"elsewhere","name":"Elsewhere"},"components":[{{{Components(onChild)}}}]}]""";
        return $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{ownership}}}"},"slot":{"key":"root","name":"Owned","parent":"Root"},
             "components":[{{{Components(onRoot)}}}]{{{child}}}}
            """;
    }

    // Every saved key whose Component is on its Slot is saved at that Component's position.
    private static void AssertSavedIndexesMatchLayout(FakeResoniteClient client, string state)
    {
        var checkpoint = JsonNode.Parse(File.ReadAllText(state))!;
        var slots = AllSlots(client.Root).ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        foreach (var (key, component) in checkpoint["components"]!.AsObject())
        {
            var slotId = checkpoint["slots"]![component!["slotKey"]!.GetValue<string>()]!["id"]!.GetValue<string>();
            var layout = slots[slotId].Components.Select(candidate => candidate.Id).ToList();
            var index = layout.IndexOf(component["id"]!.GetValue<string>());
            Assert.True(index >= 0, $"Key '{key}' is not bound to a Component on its Slot.");
            Assert.True(index == component["componentIndex"]!.GetValue<int>(),
                $"Key '{key}' is saved at index {component["componentIndex"]} but its Component is at {index}.");
        }
    }

    private static IEnumerable<FakeResoniteClient.FakeSlot> AllSlots(FakeResoniteClient.FakeSlot slot) =>
        new[] { slot }.Concat(slot.Children.SelectMany(AllSlots));
}
