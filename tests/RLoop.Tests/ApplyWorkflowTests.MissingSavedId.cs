using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("assets")]
    [InlineData("slots")]
    [InlineData("components")]
    public async Task ReloadedBindingsSurviveAnEarlyApplyCancellation(string phase)
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("reload-cancel-" + phase);
        client.ReloadWorld("reload-cancel-session");
        client.ResetWriteCounts();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document,
            new ApplyOptions(state, Progress: progress =>
            {
                if (progress.Stage == phase) cancellation.Cancel();
            }), cancellation.Token));

        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        AssertSavedIndexesMatchLayout(client, state);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task AMissingSavedIdNeverAdoptsAnUnmanagedComponentOnAnUnchangedSlot(int count, bool reconnect)
    {
        var name = "missing-saved-id-" + count;
        string World(int length) => RubbleWorld(name, Enumerable.Repeat("m1", length).ToArray()).Replace(
            """,{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""", "", StringComparison.Ordinal);
        var (client, service, state, changed) = await ApplyRubbleAsync(name, World(count), World(4));
        var original = StateId(state, "renderer");
        var slot = AllSlots(client.Root).Single(slot => slot.Name == "Rubble01");
        await client.RemoveComponentAsync(original);
        var unmanaged = await client.AddComponentAsync(slot.Id, "Test.Renderer", new Dictionary<string, string>
        {
            ["Materials"] = JsonSerializer.Serialize(Enumerable.Repeat(StateId(state, "m1"), 4))
        });
        client.ResetWriteCounts();
        if (reconnect) client.SessionId = "new-cli-connection";
        var checkpoint = File.ReadAllText(state);

        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(changed, new ApplyOptions(state)));
        var applyError = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(changed, new ApplyOptions(state)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", planError.Code);
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", applyError.Code);
        Assert.Equal("saved-id-not-found", applyError.Context["reason"]);
        Assert.Equal(original, applyError.Context["savedId"]);
        Assert.Equal(unmanaged.Id, Assert.Single(Assert.IsAssignableFrom<IEnumerable<string>>(applyError.Context["candidateIds"])));
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
        Assert.Equal(4, slot.Components.Single(component => component.Id == unmanaged.Id).Members["Materials"].Elements!.Count);

        // Explicit recovery discards only the confirmed deleted key, preserving the unrelated Component.
        var repaired = JsonNode.Parse(checkpoint)!;
        repaired["components"]!.AsObject().Remove("renderer");
        File.WriteAllText(state, repaired.ToJsonString());
        await service.ApplyAsync(changed, new ApplyOptions(state));
        Assert.NotEqual(unmanaged.Id, StateId(state, "renderer"));
        Assert.Equal(4, slot.Components.Single(component => component.Id == unmanaged.Id).Members["Materials"].Elements!.Count);
        Assert.Equal(count, slot.Components.Single(component => component.Id == StateId(state, "renderer")).Members["Materials"].Elements!.Count);
        client.ResetWriteCounts();
        await service.ApplyAsync(changed, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task AMissingSavedIdWithoutASameTypeCandidateCanBeCreatedAgain()
    {
        const string name = "missing-saved-id-no-candidate";
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
            RubbleWorld(name, "m1", "m2", "m3"));
        var original = StateId(state, "renderer");
        await client.RemoveComponentAsync(original);
        client.ResetWriteCounts();

        var applied = await service.ApplyAsync(shrunk, new ApplyOptions(state));

        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Equal(0, applied.ComponentsDeleted);
        Assert.NotEqual(original, StateId(state, "renderer"));
        Assert.Equal(StateId(state, "renderer"), Sources(client).Single().Members["Target"].TargetId);
        Assert.Equal(3, RubbleRenderers(client).Single().Members["Materials"].Elements!.Count);
    }
}
