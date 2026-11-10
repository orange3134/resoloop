using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private static string RotatedSlot(string rotation) => $$$"""
        {"schemaVersion":"1","ownership":{"key":"rotation"},
         "slot":{"key":"root","name":"Turned","parent":"Root","rotation":{{{rotation}}}}}
        """;

    [Theory]
    [InlineData("negated", 0f, -0.38268f, 0f, -0.92388f)]   // the engine reads back q as the equivalent -q
    [InlineData("rounded", 0f, 0.1736f, 0f, 0.9848f)]       // a rounded declaration the engine stores renormalised
    public async Task AnEquivalentStoredRotationIsNotRewritten(string name, float x, float y, float z, float w)
    {
        var document = ReloadDocument("rotation-" + name, RotatedSlot(FormattableString.Invariant($"[{x},{y},{z},{w}]")));
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "rotation-" + name + ".state.json");
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        var scale = (w < 0 ? -1 : 1) / MathF.Sqrt(x * x + y * y + z * z + w * w);
        await client.UpdateSlotAsync(new SlotUpdateRequest(applied.SlotId,
            Rotation: new QuaternionValue(x * scale, y * scale, z * scale, w * scale)));
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Empty(plan.Changes);
        Assert.DoesNotContain(plan.Operations, operation => operation.Action == "update");
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ADifferentRotationIsStillRewritten()
    {
        var document = ReloadDocument("rotation-different", RotatedSlot("[0,0.1736,0,0.9848]"));
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "rotation-different.state.json");
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        await client.UpdateSlotAsync(new SlotUpdateRequest(applied.SlotId, Rotation: new QuaternionValue(0, 0.1745f, 0, 0.9847f)));

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Contains(plan.Operations, operation => operation.Action == "update" && operation.Key == "root");
    }
}
