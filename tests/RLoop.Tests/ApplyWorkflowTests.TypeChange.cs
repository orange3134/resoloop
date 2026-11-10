using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    // Components are matched by their saved type, so a key whose declared type changes still finds the old Component.
    // Apply must replace it rather than write the new type's fields to it.
    [Fact]
    public async Task AKeyWhoseTypeChangesIsRecreatedAndItsReferencesFollow()
    {
        static string World(string thing) => RubbleWorld("type-change", "m1", "m2").Replace(
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}},""" + thing +
            """,{"key":"pointer","type":"Test.Source","fields":{"Target":"$ref:thing"}}""", StringComparison.Ordinal);
        var (client, service, state, changed) = await ApplyRubbleAsync("type-change",
            World("""{"key":"thing","type":"Test.Indexed","fields":{"Value":7}}"""),
            World("""{"key":"thing","type":"Test.Material","fields":{"Enabled":true}}"""));
        var original = StateId(state, "thing");
        FakeResoniteClient.FakeComponent Component(string id) => AllSlots(client.Root).SelectMany(slot => slot.Components).Single(component => component.Id == id);

        var plan = await service.PlanApplyAsync(changed, new ApplyOptions(state));
        await service.ApplyAsync(changed, new ApplyOptions(state));

        var recreate = Assert.Single(plan.Operations, operation => operation.Key == "thing");
        Assert.Equal("recreate", recreate.Action);
        Assert.Contains("type changes from 'Test.Material' to 'Test.Indexed'", recreate.Reason);
        var replacement = StateId(state, "thing");
        Assert.NotEqual(original, replacement);
        Assert.Equal("Test.Indexed", Component(replacement).Type);
        Assert.DoesNotContain(AllSlots(client.Root).SelectMany(slot => slot.Components), component => component.Id == original);
        Assert.Equal(replacement, Component(StateId(state, "pointer")).Members["Target"].TargetId);

        client.ResetWriteCounts();
        var again = await service.PlanApplyAsync(changed, new ApplyOptions(state));
        Assert.All(again.Operations, operation => Assert.Equal("no-op", operation.Action));
        Assert.Equal(0, client.Writes);
    }
}
