namespace RLoop.Core;

/// <summary>Bounded hierarchy projection: references remain marked and no field values are emitted.</summary>
public sealed record HierarchySummary(string Id, string Name, bool IsReferenceOnly,
    IReadOnlyList<HierarchyComponentSummary> Components, IReadOnlyList<HierarchySummary> Children)
{
    public static HierarchySummary FromSlot(SlotInfo slot, bool includeComponents) => new(
        slot.Id, slot.Name, slot.IsReferenceOnly,
        includeComponents ? slot.Components.Select(c => new HierarchyComponentSummary(c.Id, c.Type)).ToArray() : [],
        slot.Children.Select(c => FromSlot(c, includeComponents)).ToArray());
}

public sealed record HierarchyComponentSummary(string Id, string Type);
