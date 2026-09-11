using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class ModelMappingTests
{
    [Fact]
    public void PreservesPlaybackTypeForFluxElementBindingsAndPlaybackState()
    {
        var mapped = ModelMapper.MapMember(new Link.SyncPlayback
        {
            ID = "PlaybackMember", Play = true, Loop = true, Position = 12.5, Speed = 1
        });
        Assert.Equal("SyncPlayback", mapped.Kind);
        Assert.Equal("PlaybackMember", mapped.Id);
        Assert.Equal("[FrooxEngine]FrooxEngine.SyncPlayback", mapped.Type);
        Assert.True(mapped.Value!["play"]!.GetValue<bool>());
        Assert.True(mapped.Value["loop"]!.GetValue<bool>());
        Assert.Equal(12.5, mapped.Value["position"]!.GetValue<double>());
    }

    [Fact]
    public void MapsSlotHierarchyWithoutLeakingLinkModels()
    {
        var slot = new Link.Slot
        {
            ID = "A",
            Name = new Link.Field_string { Value = "Parent" },
            Position = new Link.Field_float3 { ID = "PositionMember", Value = new Link.float3 { x = 1, y = 2, z = 3 } },
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
        Assert.Equal("PositionMember", mapped.Members!["Position"].Id);
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

    [Fact]
    public void MapsSyncObjectAndDictionaryMembersRecursively()
    {
        var syncObject = new Link.SyncObject
        {
            Members = new Dictionary<string, Link.Member> { ["Count"] = new Link.Field_int { Value = 3 } }
        };
        var dictionary = new Link.SyncDictionary_string
        {
            Elements = new Dictionary<string, Link.Member> { ["enabled"] = new Link.Field_bool { Value = true } }
        };

        var mappedObject = ModelMapper.MapMember(syncObject);
        var mappedDictionary = ModelMapper.MapMember(dictionary);

        Assert.Equal(3, mappedObject.Members!["Count"].Value!.GetValue<int>());
        Assert.True(mappedDictionary.Members!["enabled"].Value!.GetValue<bool>());
    }
}
