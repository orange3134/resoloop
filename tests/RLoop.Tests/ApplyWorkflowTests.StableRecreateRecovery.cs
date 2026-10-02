using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("$component:r1")]
    [InlineData("$member:r1.Materials")]
    [InlineData("$component:r2")]
    public async Task StableInterruptedRecreatesStopAfterReloadInsteadOfResolvingAnotherKey(string selector)
    {
        var (client, service, state) = await InterruptStableRecreatesAsync("stable-recreate-reload");
        client.ReloadWorld("session-after-two-recreates");
        var slot = AllSlots(client.Root).Single(candidate => candidate.Name == "Rubble01");
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal(slot.Components[^1].Id,
            slot.Components[saved["components"]!["r1"]!["componentIndex"]!.GetValue<int>()].Id);
        var checkpoint = File.ReadAllText(state);
        var before = JsonSerializer.Serialize(slot.Components);
        ResolvedWorldReference? resolved = null;

        var exception = await Record.ExceptionAsync(async () =>
            resolved = await service.ResolveStableReferenceAsync(state, selector, client.SessionId));

        Assert.True(exception is RLoopException,
            $"Expected STABLE_COMPONENT_AMBIGUOUS; resolved {resolved?.Id ?? "<none>"} (r2 replacement: {slot.Components[^1].Id}).");
        AssertStableRecoveryStop(Assert.IsType<RLoopException>(exception), selector, state, selector.Contains("r2", StringComparison.Ordinal) ? "r2" : "r1");
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
        Assert.Equal(before, JsonSerializer.Serialize(slot.Components));
    }

    [Fact]
    public async Task StableInterruptedSelectorStopsBeforeTheResolveThenSetConsumerRuns()
    {
        var (client, service, state) = await InterruptStableRecreatesAsync("stable-recreate-consumer");
        client.ReloadWorld("session-before-stable-consumer");
        var checkpoint = File.ReadAllText(state);
        var before = JsonSerializer.Serialize(RubbleRenderers(client));
        string? selectedId = null;
        var consumerCalled = false;

        var exception = await Record.ExceptionAsync(async () =>
        {
            selectedId = await service.ResolveComponentSelectorAsync("$component:r1", state);
            consumerCalled = true;
            await client.SetComponentMembersAsync(selectedId, "Test.Renderer",
                new Dictionary<string, string> { ["Materials"] = "[]" });
        });

        Assert.True(exception is RLoopException,
            $"Expected STABLE_COMPONENT_AMBIGUOUS before set; selected {selectedId ?? "<none>"}, consumerCalled={consumerCalled}, writes={client.Writes}.");
        AssertStableRecoveryStop(Assert.IsType<RLoopException>(exception), "$component:r1", state, "r1");
        Assert.False(consumerCalled);
        Assert.Null(selectedId);
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
        Assert.Equal(before, JsonSerializer.Serialize(RubbleRenderers(client)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StableInterruptedRecreateResolvesAVerifiedSameSessionReplacementId(int savedIndex)
    {
        var (client, service, state) = await InterruptStableRecreatesAsync("stable-recreate-current-id");
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        saved["components"]!["r1"]!["componentIndex"] = savedIndex;
        File.WriteAllText(state, saved.ToJsonString());
        var replacement = StateId(state, "r1");
        var checkpoint = File.ReadAllText(state);

        Assert.Equal(replacement, (await service.ResolveStableReferenceAsync(state, "$component:r1", client.SessionId)).Id);
        Assert.Equal(replacement, await service.ResolveComponentSelectorAsync("$component:r1", state));
        Assert.Equal(replacement + ":Materials", (await service.ResolveStableReferenceAsync(state, "$member:r1.Materials", client.SessionId)).Id);
        Assert.Equal(replacement, await service.ResolveComponentSelectorAsync(replacement));
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
    }

    [Theory]
    [InlineData(true, "unknown-session")]
    [InlineData(true, "missing-session")]
    [InlineData(true, "empty-id")]
    [InlineData(true, "lost-id")]
    [InlineData(false, "unknown-session")]
    [InlineData(false, "missing-session")]
    [InlineData(false, "empty-id")]
    [InlineData(false, "lost-id")]
    public async Task StableRecoveryDoesNotInferFromPositionWhenTheCurrentSavedIdIsUnverified(bool recreate, string uncertainty)
    {
        var (client, service, state) = await InterruptStableRecreatesAsync("stable-recovery-unverified");
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        var record = saved["components"]!["r1"]!.AsObject();
        if (!recreate)
        {
            record.Remove("supersededId");
            record["componentIndex"] = -1;
        }
        string? currentSession = client.SessionId;
        switch (uncertainty)
        {
            case "unknown-session": currentSession = null; break;
            case "missing-session": saved["sessionId"] = null; currentSession = null; break;
            case "empty-id": record["id"] = ""; break;
            case "lost-id":
                await client.RemoveComponentAsync(record["id"]!.GetValue<string>());
                break;
        }
        File.WriteAllText(state, saved.ToJsonString());
        client.ResetWriteCounts();
        var checkpoint = File.ReadAllText(state);
        var before = JsonSerializer.Serialize(RubbleRenderers(client));

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ResolveStableReferenceAsync(state, "$component:r1", currentSession));

        AssertStableRecoveryStop(error, "$component:r1", state, "r1");
        var memberError = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ResolveStableReferenceAsync(state, "$member:r1.Materials", currentSession));
        AssertStableRecoveryStop(memberError, "$member:r1.Materials", state, "r1");
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
        Assert.Equal(before, JsonSerializer.Serialize(RubbleRenderers(client)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StableUnverifiedIndexResolvesOnlyAConfirmedSameSessionId(bool multipleCandidates)
    {
        var (client, service, state) = await ApplyStableUnverifiedIndexAsync("stable-index-current", multipleCandidates);
        var id = StateId(state, "target");

        Assert.Equal(id, (await service.ResolveStableReferenceAsync(state, "$component:target", client.SessionId)).Id);
        Assert.Equal(id, await service.ResolveComponentSelectorAsync("$component:target", state));
        Assert.Equal(id + ":Enabled", (await service.ResolveStableReferenceAsync(state, "$member:target.Enabled", client.SessionId)).Id);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StableUnverifiedIndexStopsAfterReloadEvenWithASingleCandidate(bool multipleCandidates)
    {
        var (client, service, state) = await ApplyStableUnverifiedIndexAsync("stable-index-reloaded", multipleCandidates);
        client.ReloadWorld("session-after-unverified-index");
        var checkpoint = File.ReadAllText(state);

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ResolveStableReferenceAsync(state, "$component:target", client.SessionId));

        AssertStableRecoveryStop(error, "$component:target", state, "target");
        var selectorError = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ResolveComponentSelectorAsync("$component:target", state));
        AssertStableRecoveryStop(selectorError, "$component:target", state, "target");
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
    }

    [Fact]
    public async Task StableStateReaderRetainsSupersededIdAndDefaultsLegacyStateToNull()
    {
        var (client, service, state) = await InterruptStableRecreatesAsync("stable-reader-recreate");
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        var reference = StableReferenceResolver.ResolveComponent(state, "$component:r1");

        Assert.Equal(saved["components"]!["r1"]!["supersededId"]!.GetValue<string>(),
            JsonSerializer.SerializeToNode(reference)!["SupersededId"]?.GetValue<string>());
        saved["components"]!["r1"]!.AsObject().Remove("supersededId");
        File.WriteAllText(state, saved.ToJsonString());
        Assert.Null(JsonSerializer.SerializeToNode(StableReferenceResolver.ResolveComponent(state, "$component:r1"))!["SupersededId"]);
        Assert.Equal(StateId(state, "r1"), await service.ResolveComponentSelectorAsync("$component:r1", state));
        Assert.Equal(0, client.Writes);
    }

    private async Task<(FakeResoniteClient Client, WorldService Service, string State)> InterruptStableRecreatesAsync(string name)
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
            TwoRendererWorld(name, ["m1", "m2", "m3"], ["m1", "m2"]),
            TwoRendererWorld(name, ["m1", "m2", "m3", "m4"], ["m1", "m2", "m3"]));
        var slot = AllSlots(client.Root).Single(candidate => candidate.Name == "Rubble01");
        var oldR1 = StateId(state, "r1");
        var oldR2 = StateId(state, "r2");
        var source = StateId(state, "source");
        Assert.Equal(new[] { oldR1, oldR2, source }, slot.Components.Select(component => component.Id));
        client.LoseResponseOnWrite = 4;

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal(4, client.Writes);
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal(oldR1, saved["components"]!["r1"]!["supersededId"]!.GetValue<string>());
        Assert.Equal(oldR2, saved["components"]!["r2"]!["supersededId"]!.GetValue<string>());
        Assert.Null(saved["components"]!["r1"]!["identityValues"]);
        Assert.Null(saved["components"]!["r2"]!["identityValues"]);
        Assert.Equal(new[] { oldR2, source, StateId(state, "r1"), StateId(state, "r2") }, slot.Components.Select(component => component.Id));
        Assert.Equal(StateId(state, "r1"), slot.Components.Single(component => component.Id == source).Members["Target"].TargetId);
        Assert.Equal(StateId(state, "r2"), slot.Components[saved["components"]!["r1"]!["componentIndex"]!.GetValue<int>()].Id);
        client.LoseResponseOnWrite = null;
        client.ResetWriteCounts();
        return (client, service, state);
    }

    private async Task<(FakeResoniteClient Client, WorldService Service, string State)> ApplyStableUnverifiedIndexAsync(string name, bool multipleCandidates)
    {
        var document = Document(name, """[{"key":"target","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        if (multipleCandidates)
            await client.AddComponentAsync(client.Root.Children.Single().Id, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        saved["components"]!["target"]!["componentIndex"] = -1;
        File.WriteAllText(state, saved.ToJsonString());
        client.ResetWriteCounts();
        return (client, service, state);
    }

    private static void AssertStableRecoveryStop(RLoopException error, string selector, string state, string key)
    {
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(selector, error.Context["selector"]);
        Assert.Equal(Path.GetFullPath(state), error.Context["stateFile"]);
        Assert.Equal(key, error.Context["componentKey"]);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(error.Context["reason"])));
        Assert.Contains(error.Suggestions, suggestion => suggestion.Contains("preserve", StringComparison.OrdinalIgnoreCase) &&
            suggestion.Contains("state", StringComparison.OrdinalIgnoreCase));
    }
}
