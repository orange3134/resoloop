using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANullOrMissingSavedIdNeverRecreatesAMatchedComponent(bool missing)
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("invalid-saved-id-" + missing,
            RubbleWorld("invalid-saved-id-" + missing, "m1", "m2", "m3"));
        var original = RubbleRenderers(client).Single().Id;
        var json = JsonNode.Parse(File.ReadAllText(state))!;
        var saved = json["components"]!["renderer"]!.AsObject();
        if (missing) saved.Remove("id");
        else saved["id"] = null;
        File.WriteAllText(state, json.ToJsonString());
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(original, RubbleRenderers(client).Single().Id);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task TestSeesTheCurrentComponentBeforeAListShrink()
    {
        var name = "test-before-recreate";
        var initial = RubbleWorld(name, "m1", "m2", "m3", "m4");
        var shrunk = RubbleWorld(name, "m1", "m2", "m3").Replace("\"children\":[",
            "\"tests\":[{\"name\":\"existing\",\"assertions\":[{\"target\":\"$component:renderer\",\"exists\":true}]}],\"children\":[",
            StringComparison.Ordinal);
        var (client, service, state, document) = await ApplyRubbleAsync(name, shrunk, initial);
        client.ResetWriteCounts();

        var result = await service.TestAsync(document, new ApplyOptions(state));

        Assert.True(result.Passed);
        Assert.True(Assert.Single(Assert.Single(result.Tests).Assertions).Passed);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task TestResolvesARecreateTargetInAssertionsAndAReversibleProbe()
    {
        var name = "test-recreate-probe";
        static string World(string ownership, params string[] materials) => RubbleWorld(ownership, materials)
            .Replace("\"fields\":{\"Materials\":", "\"fields\":{\"Enabled\":false,\"Materials\":", StringComparison.Ordinal);
        var initial = World(name, "m1", "m2", "m3", "m4");
        var shrunk = World(name, "m1", "m2", "m3").Replace("\"children\":[",
            "\"tests\":[{\"name\":\"existing\",\"assertions\":[{\"target\":\"$component:renderer\",\"exists\":true},{\"target\":\"$component:source.Target\",\"expected\":\"$ref:renderer\"},{\"target\":\"$component:renderer.Enabled\",\"expected\":false},{\"target\":\"$component:renderer.Enabled\",\"expected\":true,\"phase\":\"after\"}],\"probe\":{\"kind\":\"set-member\",\"target\":\"$component:renderer.Enabled\",\"value\":true,\"restore\":true,\"safe\":true}}],\"children\":[",
            StringComparison.Ordinal);
        var (client, service, state, document) = await ApplyRubbleAsync(name, shrunk, initial);
        var oldId = StateId(state, "renderer");
        client.ResetWriteCounts();

        var result = await service.TestAsync(document, new ApplyOptions(state), allowProbe: true);

        Assert.True(result.Passed, string.Join("; ", Assert.Single(result.Tests).Assertions.Select(x => $"{x.Target}: {x.Message}")));
        Assert.True(Assert.Single(result.Tests).ProbeExecuted);
        Assert.All(Assert.Single(result.Tests).Assertions, assertion => Assert.True(assertion.Passed));
        Assert.Equal(oldId, StateId(state, "renderer"));
        Assert.Equal(2, client.Writes); // temporary probe and restore; test did not recreate the Component
    }

    [Fact]
    public async Task DeferredMutualReferenceListsMustConvergeBeforeOldComponentsAreDeleted()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("deferred-refill",
            MutuallyReferencingListsWorld("deferred-refill", 1), MutuallyReferencingListsWorld("deferred-refill", 2));
        var old = RubbleRenderers(client).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        client.RefillListsTo = 2;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.True(old.SetEquals(RubbleRenderers(client).Select(x => x.Id)));
        Assert.Contains(StateId(state, "r1"), old);
        Assert.Contains(StateId(state, "r2"), old);
    }

    [Fact]
    public async Task DeferredMutualReferenceListsConvergeWhenTheRuntimeDoesNotRefill()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("deferred-converges",
            MutuallyReferencingListsWorld("deferred-converges", 1), MutuallyReferencingListsWorld("deferred-converges", 2));
        var old = RubbleRenderers(client).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        var renderers = RubbleRenderers(client);
        Assert.DoesNotContain(renderers, renderer => old.Contains(renderer.Id));
        Assert.Single(renderers.Single(x => x.Id == StateId(state, "r1")).Members["Materials"].Elements!);
        Assert.Single(renderers.Single(x => x.Id == StateId(state, "r2")).Members["Materials"].Elements!);
        Assert.Equal(StateId(state, "r2"), MaterialTargets(renderers.Single(x => x.Id == StateId(state, "r1"))).Single());
        Assert.Equal(StateId(state, "r1"), MaterialTargets(renderers.Single(x => x.Id == StateId(state, "r2"))).Single());
        client.ReloadWorld("deferred-converges-reloaded");
        client.ResetWriteCounts();
        await service.ApplyAsync(shrunk, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    private static string MutuallyReferencingListsWorld(string ownership, int count) =>
        PointingRenderersWorld(ownership, count, count, mutual: true)
            .Replace("\"key\":\"r1\",\"type\":\"Test.Renderer\",\"fields\":{\"Materials\":[\"$ref:m1\"",
                "\"key\":\"r1\",\"type\":\"Test.Renderer\",\"fields\":{\"Materials\":[\"$ref:r2\"", StringComparison.Ordinal)
            .Replace("\"key\":\"r2\",\"type\":\"Test.Renderer\",\"fields\":{\"Materials\":[\"$ref:m1\"",
                "\"key\":\"r2\",\"type\":\"Test.Renderer\",\"fields\":{\"Materials\":[\"$ref:r1\"", StringComparison.Ordinal);

    [Fact]
    public async Task AReferencedResumedReplacementIsNotUndoneWhenRefilledAfterSnapshot()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("referenced-resume-refill",
            RubbleWorld("referenced-resume-refill", "m1", "m2", "m3"));
        client.FailRemovingComponent = StateId(state, "renderer");
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailRemovingComponent = null;
        var replacement = StateId(state, "renderer");
        Assert.Equal(replacement, Sources(client).Single().Members["Target"].TargetId);
        var refilled = false;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk,
            new ApplyOptions(state, Progress: progress =>
            {
                if (refilled || progress.Stage != "components") return;
                refilled = true;
                var renderer = RubbleRenderers(client).Single(x => x.Id == replacement);
                var materials = renderer.Members["Materials"];
                renderer.Members["Materials"] = materials with { Elements = [.. materials.Elements!, materials.Elements![0]] };
            })));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Contains(replacement, RubbleRenderers(client).Select(x => x.Id));
        Assert.Equal(replacement, Sources(client).Single().Members["Target"].TargetId);
        Assert.Equal(replacement, StateId(state, "renderer"));
        Assert.Contains("supersededId", File.ReadAllText(state));
        Assert.Contains("renderer", Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["inProgress"]));
    }

    [Fact]
    public async Task AResumedReplacementWithoutAnOldComponentStillChecksListLength()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("no-old-refill",
            RubbleWorld("no-old-refill", "m1", "m2", "m3"));
        client.LoseResponseOnWrite = 3; // replacement, Source, then the removal of the old Component
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.LoseResponseOnWrite = null;
        Assert.Single(RubbleRenderers(client));
        var replacement = StateId(state, "renderer");
        var refilled = false;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk,
            new ApplyOptions(state, Progress: progress =>
            {
                if (refilled || progress.Stage != "components") return;
                refilled = true;
                var renderer = RubbleRenderers(client).Single(x => x.Id == replacement);
                var materials = renderer.Members["Materials"];
                renderer.Members["Materials"] = materials with { Elements = [.. materials.Elements!, materials.Elements![0]] };
            })));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Single(RubbleRenderers(client));
        Assert.Equal(replacement, StateId(state, "renderer"));
        Assert.Contains("supersededId", File.ReadAllText(state));
    }

    [Fact]
    public async Task ANewReplacementWithoutAnOldComponentKeepsItsStateWhenRefilled()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("lost-both-refill",
            RubbleWorld("lost-both-refill", "m1", "m2", "m3"));
        client.LoseResponseOnWrite = 3;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.LoseResponseOnWrite = null;
        var lostReplacement = StateId(state, "renderer");
        await client.RemoveComponentAsync(lostReplacement);
        client.RefillListsTo = 4;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        var current = RubbleRenderers(client).Single();
        Assert.NotEqual(lostReplacement, current.Id);
        Assert.Equal(current.Id, StateId(state, "renderer"));
        Assert.Contains("supersededId", File.ReadAllText(state));
        Assert.Contains("renderer", Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["inProgress"]));
    }

    [Fact]
    public async Task AResumedShorterDeclarationCanConvergeAfterOneUndo()
    {
        static string World(params string[] materials) => RubbleWorld("shrink-again-unreferenced", materials)
            .Replace("\"Target\":\"$ref:renderer\"", "\"Target\":null", StringComparison.Ordinal);
        var (client, service, state, three) = await ApplyRubbleAsync("shrink-again-unreferenced",
            World("m1", "m2", "m3"), World("m1", "m2", "m3", "m4"));
        client.FailRemovingComponent = StateId(state, "renderer");
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailRemovingComponent = null;
        var two = ReloadDocument("shrink-again-unreferenced-two", World("m1", "m2"));
        var plan = await service.PlanApplyAsync(two, new ApplyOptions(state));
        Assert.Contains("shorter", Assert.Single(plan.Operations, operation => operation.Key == "renderer").Reason,
            StringComparison.OrdinalIgnoreCase);

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(two, new ApplyOptions(state)));
        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Contains("same apply command", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Re-run the same apply command", Assert.IsType<string>(error.Context["recovery"]));
        Assert.DoesNotContain(error.Suggestions, suggestion => suggestion.Contains("re-running the same command recreates and undoes", StringComparison.Ordinal));
        await service.ApplyAsync(two, new ApplyOptions(state));
        Assert.Equal(2, RubbleRenderers(client).Single().Members["Materials"].Elements!.Count);
    }

    [Fact]
    public async Task ARefilledListDoesNotSuggestRetryingWithoutAChange()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("refill-recovery",
            RubbleWorld("refill-recovery", "m1", "m2", "m3"));
        client.RefillListsTo = 4;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.DoesNotContain("Re-run the same apply command.", Assert.IsType<string>(error.Context["recovery"]));
        Assert.DoesNotContain(error.Suggestions, suggestion => suggestion.StartsWith("Re-run the same apply command after resolving", StringComparison.Ordinal));
    }
}
