using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class ModelMappingTests
{
    [Fact]
    public void MapsSlotHierarchyWithoutLeakingLinkModels()
    {
        var slot = new Link.Slot
        {
            ID = "A",
            Name = new Link.Field_string { Value = "Parent" },
            Position = new Link.Field_float3 { Value = new Link.float3 { x = 1, y = 2, z = 3 } },
            Components = [new Link.Component
            {
                ID = "C",
                ComponentType = "[FrooxEngine]FrooxEngine.Grabbable",
                Members = new Dictionary<string, Link.Member> { ["Scalable"] = new Link.Field_bool { Value = true } }
            }],
            Children = [new Link.Slot { ID = "B", Name = new Link.Field_string { Value = "Child" } }]
        };
        var mapped = ModelMapper.MapSlot(slot);
        Assert.Equal("Parent", mapped.Name);
        Assert.Equal(new RLoop.Core.Vector3Value(1, 2, 3), mapped.Position);
        Assert.Equal("C", Assert.Single(mapped.Components).Id);
        Assert.True(Assert.Single(mapped.Components).Members!["Scalable"].Value!.GetValue<bool>());
        Assert.Equal("Child", Assert.Single(mapped.Children).Name);
    }

    [Fact]
    public void MapsFieldsAndReferences()
    {
        var component = new Link.Component
        {
            ID = "C",
            ComponentType = "T",
            Members = new Dictionary<string, Link.Member>
            {
                ["Enabled"] = new Link.Field_bool { ID = "F", Value = true },
                ["Target"] = new Link.Reference { ID = "R", TargetID = "S", TargetType = "FrooxEngine.Slot" }
            }
        };
        var mapped = ModelMapper.MapComponent(component);
        Assert.Equal("field", mapped.Members["Enabled"].Kind);
        Assert.Equal("S", mapped.Members["Target"].TargetId);
    }
}
