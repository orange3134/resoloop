using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class HierarchySummaryTests
{
    [Fact]
    public void SummaryPreservesReferenceBoundariesAndIdentityButNeverSerializesMemberPayloads()
    {
        var payload = new Dictionary<string, MemberValue> { ["Hidden"] = new("field", "field-id", "string", JsonValue.Create("large-payload")) };
        var child = new SlotInfo("child", "Boundary", "root", null, null, null, null, null, null, true,
            [new ComponentSummary("component", "Test.Type", payload)], [], Members: payload);
        var root = child with { Id = "root", Name = "Model", IsReferenceOnly = false, Children = [child] };
        var summary = HierarchySummary.FromSlot(root, true);
        Assert.True(Assert.Single(summary.Children).IsReferenceOnly);
        Assert.Equal("Test.Type", Assert.Single(summary.Children[0].Components).Type);
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("field-id", json);
        Assert.DoesNotContain("large-payload", json);
        Assert.Empty(HierarchySummary.FromSlot(root, false).Children[0].Components);
    }
}
