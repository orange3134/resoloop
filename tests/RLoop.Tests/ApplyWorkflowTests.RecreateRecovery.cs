using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALostRecreateRemovalResponseLeavesSiblingIndexesUnverified(bool reload)
    {
        var name = "recreate-lost-removal-" + reload;
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
            RecreateRecoveryWorld(name, 2), RecreateRecoveryWorld(name, 3));
        var slot = client.Root.Children.Single();
        await client.AddComponentAsync(slot.Id, "Test.Indexed", new Dictionary<string, string> { ["Value"] = "999" });
        client.ResetWriteCounts();
        client.LoseResponseOnWrite = 2;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal(2, client.Writes);
        Assert.Equal(-1, JsonNode.Parse(File.ReadAllText(state))!["components"]!["v1"]!["componentIndex"]!.GetValue<int>());
        Assert.Single(slot.Components, component => component.Type == "Test.Renderer");
        client.LoseResponseOnWrite = null;
        if (reload)
        {
            client.ReloadWorld("session-after-lost-recreate-removal");
            slot = client.Root.Children.Single();
            client.ResetWriteCounts();
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
            Assert.Equal("session-changed", error.Context["reason"]);
            Assert.Single(JsonSerializer.SerializeToNode(error.Context["candidates"])!.AsArray());
            await RecoverRecreateAfterReloadAsync(client, state, error);
            Assert.Equal(0, client.Writes);
            var repaired = JsonNode.Parse(File.ReadAllText(state))!;
            Assert.Equal(2, repaired["components"]!["renderer"]!["componentIndex"]!.GetValue<int>());
            Assert.Equal(-1, repaired["components"]!["v1"]!["componentIndex"]!.GetValue<int>());

            var stopped = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

            Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", stopped.Code);
            Assert.Equal(0, client.Writes);
            AssertUnmanagedIndexedValue(client);
            var sibling = Assert.Single(slot.Components, component => component.Type == "Test.Indexed" &&
                component.Members["Value"].Value!.GetValue<int>() == 1);
            await ConfirmRecoveryIndexAsync(client, state, "v1", sibling);
        }
        await service.ApplyAsync(shrunk, new ApplyOptions(state));
        await AssertRecreateRecoveryConvergedAsync(client, service, shrunk, state);
    }

    [Fact]
    public async Task AReloadedRecreateReportsPositionsAndRepairsEverySlotKeyBeforeRetry()
    {
        const string name = "recreate-reload-guidance";
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
            RecreateRecoveryWorld(name, 2, source: true), RecreateRecoveryWorld(name, 3, source: true));
        var slot = client.Root.Children.Single();
        await client.AddComponentAsync(slot.Id, "Test.Indexed", new Dictionary<string, string> { ["Value"] = "999" });
        client.ResetWriteCounts();
        client.FailRemovingComponent = StateId(state, "renderer");
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailRemovingComponent = null;
        client.ReloadWorld("session-before-guided-recreate-recovery");
        slot = client.Root.Children.Single();
        client.ResetWriteCounts();
        var checkpoint = JsonNode.Parse(File.ReadAllText(state))!;
        checkpoint["components"]!["renderer"]!["id"] = "";
        File.WriteAllText(state, checkpoint.ToJsonString());

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_RECREATE_INTERRUPTED", error.Code);
        Assert.Equal("session-changed", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        var candidates = JsonSerializer.SerializeToNode(error.Context["candidates"])!.AsArray();
        Assert.Equal(new[] { 0, 4 }, candidates.Select(candidate => candidate!["position"]!.GetValue<int>()));
        foreach (var candidate in candidates)
        {
            Assert.Equal(candidate!["id"]!.GetValue<string>(), slot.Components[candidate["position"]!.GetValue<int>()].Id);
            Assert.NotNull(candidate["lists"]);
            Assert.NotNull(candidate["referencedBy"]);
        }
        var slotKeys = JsonSerializer.SerializeToNode(error.Context["slotKeys"])!.AsArray();
        Assert.Equal(checkpoint["components"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal),
            slotKeys.Select(item => item!["key"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        foreach (var item in slotKeys)
            Assert.Equal(checkpoint["components"]![item!["key"]!.GetValue<string>()]!["componentIndex"]!.GetValue<int>(),
                item["componentIndex"]!.GetValue<int>());
        Assert.Equal(3, error.Suggestions.Count);
        Assert.Contains("referencedBy", error.Suggestions[0]);
        Assert.Contains("resoloop component remove ID --yes", error.Suggestions[0]);
        Assert.Contains("only the IDs in candidates", error.Suggestions[0]);
        Assert.Contains("earlier session", error.Suggestions[0]);
        Assert.Contains("not proof of ownership", error.Suggestions[0]);
        Assert.Contains("confirmed to belong to the interrupted recreate", error.Suggestions[0]);
        Assert.Contains("leave all candidates untouched", error.Suggestions[0]);
        Assert.Contains("keep the state file as it is", error.Suggestions[0]);
        Assert.Contains("back up the state file", error.Suggestions[1]);
        Assert.Contains("components.renderer.id", error.Suggestions[1]);
        Assert.Contains("ID is empty", error.Suggestions[1]);
        Assert.Contains("delete supersededId", error.Suggestions[1]);
        Assert.Contains("adjust", error.Suggestions[1]);
        Assert.Contains("before re-running apply", error.Suggestions[1]);
        Assert.Contains("move one position earlier", error.Suggestions[2]);
        Assert.Contains("identityFields", error.Suggestions[2]);
        Assert.Contains("saved componentIndex", error.Suggestions[2]);
        Assert.Contains("kept candidate's position", error.Suggestions[2]);
        Assert.Contains("greater than the removed candidate's position", error.Suggestions[2]);
        Assert.Contains("subtract 1", error.Suggestions[2]);
        Assert.Contains("every key in slotKeys", error.Suggestions[2]);
        Assert.Contains("-1", error.Suggestions[2]);
        Assert.Contains("resoloop component inspect ID", error.Suggestions[2]);
        Assert.Contains("current position", error.Suggestions[2]);
        Assert.Contains("keep the state file as it is", error.Suggestions[2]);
        Assert.Contains("do not guess", error.Suggestions[2]);
        Assert.Contains("nothing is removed", error.Suggestions[2]);
        Assert.Contains("do not subtract", error.Suggestions[2]);

        var kept = Assert.Single(candidates, candidate => candidate!["referencedBy"]!.AsArray().Count > 0)!;
        Assert.Equal(slot.Components.Single(component => component.Type == "Test.Source").Members["Target"].TargetId,
            kept["id"]!.GetValue<string>());
        await RecoverRecreateAfterReloadAsync(client, state, error);
        Assert.Equal(1, client.Writes);
        Assert.Equal(checkpoint.ToJsonString(), JsonNode.Parse(File.ReadAllText(state + ".backup"))!.ToJsonString());
        var repaired = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal(checkpoint["components"]!["renderer"]!["supersededId"]!.GetValue<string>(), StateId(state, "renderer"));
        Assert.Null(repaired["components"]!["renderer"]!["supersededId"]);
        Assert.Equal(3, repaired["components"]!["renderer"]!["componentIndex"]!.GetValue<int>());
        Assert.Equal(-1, repaired["components"]!["v1"]!["componentIndex"]!.GetValue<int>());
        Assert.Equal(-1, repaired["components"]!["source"]!["componentIndex"]!.GetValue<int>());
        var sibling = Assert.Single(slot.Components, component => component.Type == "Test.Indexed" &&
            component.Members["Value"].Value!.GetValue<int>() == 1);
        await ConfirmRecoveryIndexAsync(client, state, "v1", sibling);
        var source = Assert.Single(slot.Components, component => component.Type == "Test.Source");
        Assert.Equal(kept["id"]!.GetValue<string>(), source.Members["Target"].TargetId);
        await ConfirmRecoveryIndexAsync(client, state, "source", source);

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        await AssertRecreateRecoveryConvergedAsync(client, service, shrunk, state);
        Assert.Equal(StateId(state, "renderer"), Sources(client).Single().Members["Target"].TargetId);
    }

    [Fact]
    public async Task ACancelledRecreateUndoLeavesNewSiblingIndexesUnverified()
    {
        const string name = "recreate-cancelled-undo";
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
            RecreateRecoveryWorld(name, 2, sibling: "k3"), RecreateRecoveryWorld(name, 3, sibling: null));
        var slot = client.Root.Children.Single();
        client.ResetWriteCounts();
        client.FailRemovingComponent = StateId(state, "renderer");
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailRemovingComponent = null;
        var replacement = slot.Components.Single(component => component.Id == StateId(state, "renderer"));
        await client.SetComponentMembersAsync(replacement.Id, replacement.Type,
            new Dictionary<string, string> { ["Materials"] = "[\"A\",\"B\",\"C\"]" });
        await client.AddComponentAsync(slot.Id, "Test.Indexed", new Dictionary<string, string> { ["Value"] = "999" });
        using var cancellation = new CancellationTokenSource();
        client.ResetWriteCounts();
        client.Cancellation = cancellation;
        client.CancelAfterWrites = 2;

        var interrupted = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(shrunk, new ApplyOptions(state), cancellation.Token));

        Assert.Equal("APPLY_CANCELLED", interrupted.Code);
        Assert.Equal(2, client.Writes);
        Assert.DoesNotContain(slot.Components, component => component.Id == replacement.Id);
        Assert.Equal(-1, JsonNode.Parse(File.ReadAllText(state))!["components"]!["k3"]!["componentIndex"]!.GetValue<int>());
        client.CancelAfterWrites = null;
        client.Cancellation = null;
        client.ReloadWorld("session-after-cancelled-recreate-undo");
        slot = client.Root.Children.Single();
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        Assert.Equal("session-changed", error.Context["reason"]);
        await RecoverRecreateAfterReloadAsync(client, state, error);
        Assert.Equal(0, client.Writes);

        var stopped = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", stopped.Code);
        Assert.Equal(0, client.Writes);
        AssertUnmanagedIndexedValue(client);
        var sibling = Assert.Single(slot.Components, component => component.Type == "Test.Indexed" &&
            component.Members["Value"].Value!.GetValue<int>() == 3);
        await ConfirmRecoveryIndexAsync(client, state, "k3", sibling);
        await service.ApplyAsync(shrunk, new ApplyOptions(state));
        await AssertRecreateRecoveryConvergedAsync(client, service, shrunk, state);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task AReloadedFailedRecreateDoesNotAuthorizeDeletingUnmanagedCandidates(int unmanagedCount, bool referenced)
    {
        var name = $"recreate-unmanaged-reload-{unmanagedCount}-{referenced}";
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
            RecreateRecoveryWorld(name, 2, source: true, sibling: null),
            RecreateRecoveryWorld(name, 3, source: true, sibling: null));
        var slot = client.Root.Children.Single();
        var unmanaged = new List<string>();
        for (var index = 0; index < unmanagedCount; index++)
            unmanaged.Add((await client.AddComponentAsync(slot.Id, "Test.Renderer",
                new Dictionary<string, string> { ["Materials"] = $"[\"unmanaged-{index}\"]" })).Id);
        if (referenced)
            await client.SetComponentMemberAsync(StateId(state, "source"), "Target", unmanaged[0]);
        client.ResetWriteCounts();
        client.FailOnWrite = 1;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal(1, client.Writes);
        Assert.Equal(unmanagedCount + 1, slot.Components.Count(component => component.Type == "Test.Renderer"));
        var interrupted = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal("", interrupted["components"]!["renderer"]!["id"]!.GetValue<string>());
        Assert.NotNull(interrupted["components"]!["renderer"]!["supersededId"]);
        Assert.DoesNotContain(interrupted["components"]!.AsObject(), pair => pair.Key != "renderer" &&
            pair.Value!["slotKey"]!.GetValue<string>() == "root" && pair.Value["type"]!.GetValue<string>() == "Test.Renderer");
        client.FailOnWrite = null;
        client.ReloadWorld("session-after-failed-recreate-add");
        slot = client.Root.Children.Single();
        client.ResetWriteCounts();
        var checkpoint = File.ReadAllText(state);
        var before = JsonSerializer.Serialize(slot.Components);

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_RECREATE_INTERRUPTED", error.Code);
        Assert.Equal("session-changed", error.Context["reason"]);
        var candidates = JsonSerializer.SerializeToNode(error.Context["candidates"])!.AsArray();
        Assert.Equal(unmanagedCount + 1, candidates.Count);
        Assert.Single(candidates, candidate => candidate!["referencedBy"]!.AsArray().Count > 0);
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
        Assert.Equal(before, JsonSerializer.Serialize(slot.Components));
        for (var index = 0; index < unmanagedCount; index++)
            Assert.Single(slot.Components, component => component.Type == "Test.Renderer" &&
                component.Members["Materials"].Elements!.Count == 1 &&
                component.Members["Materials"].Elements!.Single().TargetId == $"unmanaged-{index}");
        Assert.Equal(3, error.Suggestions.Count);
        Assert.Contains("unmanaged same-type", error.Suggestions[0]);
        Assert.Contains("not necessarily", error.Suggestions[0]);
        Assert.Contains("not proof of ownership", error.Suggestions[0]);
        Assert.Contains("confirmed to belong to the interrupted recreate", error.Suggestions[0]);
        Assert.Contains("resoloop component remove ID --yes", error.Suggestions[0]);
        Assert.Contains("leave all candidates untouched", error.Suggestions[0]);
        Assert.Contains("keep the state file as it is", error.Suggestions[0]);
        Assert.Contains("do not guess", error.Suggestions[0]);
        Assert.DoesNotContain("remove the other", error.Suggestions[0]);
    }

    [Fact]
    public async Task ARecreatePlanLimitsPruneConfirmationGuidanceToStaleTargets()
    {
        const string name = "recreate-plan-prune-guidance";
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
            RecreateRecoveryWorld(name, 2), RecreateRecoveryWorld(name, 3));
        var checkpoint = File.ReadAllText(state);

        var plan = await service.PlanApplyAsync(shrunk, new ApplyOptions(state));

        Assert.Contains(plan.Operations, operation => operation.Action == "recreate");
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
        Assert.Contains("stale", plan.Recovery);
        Assert.Contains("--prune --yes", plan.Recovery);
        Assert.Contains("recreate", plan.Recovery);
        Assert.Contains("relocate", plan.Recovery);
        Assert.Contains("without --prune", plan.Recovery);
        Assert.DoesNotContain("deletion requires --prune --yes", plan.Recovery);
    }

    private static string RecreateRecoveryWorld(string name, int materials, bool source = false, string? sibling = "v1")
    {
        var indexed = sibling is null ? "" : $$$""",{"key":"{{{sibling}}}","type":"Test.Indexed","fields":{"Value":{{{sibling[1..]}}}}}""";
        var reference = source ? """,{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""" : "";
        return $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{name}}}"},"slot":{"key":"root","name":"Owned","parent":"Root"},
             "components":[{"key":"renderer","type":"Test.Renderer","fields":{"Materials":[{{{string.Join(",", new[] { "\"A\"", "\"B\"", "\"C\"" }.Take(materials))}}}]}}{{{indexed}}}{{{reference}}}]}
            """;
    }

    private static async Task RecoverRecreateAfterReloadAsync(FakeResoniteClient client, string state, RLoopException error)
    {
        var candidates = JsonSerializer.SerializeToNode(error.Context["candidates"])!.AsArray();
        // These fixtures create exactly one managed renderer and its replacement; any unmanaged Components are indexed.
        // Their known creation history establishes ownership, not the observed lists or references alone.
        var materialSlot = AllSlots(client.Root).SingleOrDefault(slot => slot.Name == "Materials");
        var expectedMaterials = materialSlot is null ? new[] { "A", "B", "C" }
            : materialSlot.Components.Select(component => component.Id).ToArray();
        foreach (var candidate in candidates)
        {
            var inspected = await client.GetComponentAsync(candidate!["id"]!.GetValue<string>());
            Assert.Equal("Test.Renderer", inspected.Type);
            var materials = inspected.Members["Materials"].Elements!;
            Assert.True(materials.Count == expectedMaterials.Length || materials.Count == expectedMaterials.Length - 1);
            Assert.Equal(expectedMaterials.Take(materials.Count), materials.Select(material => material.TargetId));
        }
        Assert.True(candidates.Count(candidate => candidate!["referencedBy"]!.AsArray().Count > 0) <= 1);
        var kept = candidates.FirstOrDefault(candidate => candidate!["referencedBy"]!.AsArray().Count > 0) ?? candidates[0]!;
        var removed = candidates.Where(candidate => candidate != kept).ToArray();
        Assert.True(removed.Length <= 1);
        foreach (var candidate in removed)
            await client.RemoveComponentAsync(candidate!["id"]!.GetValue<string>());
        File.Copy(state, state + ".backup", true);
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        var record = saved["components"]![(string)error.Context["componentKey"]!]!.AsObject();
        if (record["id"]!.GetValue<string>().Length == 0) record["id"] = record["supersededId"]!.GetValue<string>();
        record.Remove("supersededId");
        record["componentIndex"] = kept["position"]!.GetValue<int>();
        foreach (var item in JsonSerializer.SerializeToNode(error.Context["slotKeys"])!.AsArray())
        {
            var component = saved["components"]![item!["key"]!.GetValue<string>()]!;
            var index = component["componentIndex"]?.GetValue<int>();
            if (removed.Length == 1 && index > removed[0]!["position"]!.GetValue<int>())
                component["componentIndex"] = index - 1;
        }
        File.WriteAllText(state, saved.ToJsonString());
    }

    private static async Task ConfirmRecoveryIndexAsync(FakeResoniteClient client, string state, string key,
        FakeResoniteClient.FakeComponent identified)
    {
        var inspected = await client.GetComponentAsync(identified.Id);
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        var record = saved["components"]![key]!;
        Assert.Equal(record["type"]!.GetValue<string>(), inspected.Type);
        if (inspected.Type == "Test.Indexed")
            Assert.Equal(int.Parse(key[1..], System.Globalization.CultureInfo.InvariantCulture), inspected.Members["Value"].Value!.GetValue<int>());
        else
            Assert.Equal(identified.Members["Target"].TargetId, inspected.Members["Target"].TargetId);
        var slot = Assert.Single(AllSlots(client.Root), slot => slot.Components.Contains(identified));
        record["componentIndex"] = slot.Components.IndexOf(identified);
        File.WriteAllText(state, saved.ToJsonString());
    }

    private static void AssertUnmanagedIndexedValue(FakeResoniteClient client) =>
        Assert.Single(AllSlots(client.Root).SelectMany(slot => slot.Components), component => component.Type == "Test.Indexed" &&
            component.Members["Value"].Value!.GetValue<int>() == 999);

    private static async Task AssertRecreateRecoveryConvergedAsync(FakeResoniteClient client, WorldService service,
        ApplyDocument document, string state)
    {
        Assert.DoesNotContain("supersededId", File.ReadAllText(state));
        Assert.Equal(2, Assert.Single(client.Root.Children.Single().Components, component => component.Type == "Test.Renderer")
            .Members["Materials"].Elements!.Count);
        AssertSavedIndexesMatchLayout(client, state);
        AssertUnmanagedIndexedValue(client);
        client.ResetWriteCounts();
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        client.ReloadWorld("session-after-recreate-recovery");
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        AssertUnmanagedIndexedValue(client);
    }
}
