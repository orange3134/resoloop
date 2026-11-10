using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task ValueOnlyUpdateSkipsTheReferenceReadBack()
    {
        var initial = Document("recheck-value", """
            [{"key":"target","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"source","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}]
            """);
        var client = new FakeResoniteClient(initial);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "recheck-value.state.json"));
        await service.ApplyAsync(initial, options);
        client.ResetWriteCounts();

        var applied = await service.ApplyAsync(Document("recheck-value", """
            [{"key":"target","type":"Test.Target","fields":{"Enabled":false}},
             {"key":"source","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}]
            """), options);

        Assert.Equal(1, applied.ComponentsUpdated);
        Assert.Equal(0, client.ComponentReads);
    }

    [Fact]
    public async Task RejectedReferenceWriteIsCaughtWithoutALifecycleChange()
    {
        var initial = Document("recheck-reference", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"second","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"owner","type":"Test.Source","fields":{"Target":"$member:second.Enabled"}},
             {"key":"source","type":"Test.Source","fields":{"Target":"$member:first.Enabled"}}]
            """);
        var client = new FakeResoniteClient(initial);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "recheck-reference.state.json"));
        await service.ApplyAsync(initial, options);
        client.TargetClaimedBy = await service.ResolveComponentSelectorAsync("$component:owner", options.StateFile);

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(Document("recheck-reference", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"second","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"owner","type":"Test.Source","fields":{"Target":"$member:second.Enabled"}},
             {"key":"source","type":"Test.Source","fields":{"Target":"$member:second.Enabled"}}]
            """), options));

        Assert.Equal("APPLY_REFERENCE_NOT_RETAINED", error.Code);
    }
}
