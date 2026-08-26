using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class ValueCodecTests
{
    [Theory]
    [InlineData("bool", "true", typeof(Link.Field_bool))]
    [InlineData("int", "42", typeof(Link.Field_int))]
    [InlineData("float", "1.25", typeof(Link.Field_float))]
    [InlineData("string", "hello", typeof(Link.Field_string))]
    [InlineData("float3", "1,2,3", typeof(Link.Field_float3))]
    [InlineData("floatQ", "0,0,0,1", typeof(Link.Field_floatQ))]
    [InlineData("colorX", "0.1,0.2,0.3,1", typeof(Link.Field_colorX))]
    public async Task ConvertsCommonFieldValues(string type, string raw, Type expected)
    {
        var link = new Link.LinkInterface();
        var definition = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = type } };
        var result = await ValueCodec.ParseAsync(link, definition, raw);
        Assert.IsType(expected, result);
    }

    [Fact]
    public async Task ConvertsReferencesAndNull()
    {
        var link = new Link.LinkInterface();
        var definition = new Link.ReferenceDefinition { TargetType = new Link.TypeReference { Type = "FrooxEngine.Slot" } };
        var reference = Assert.IsType<Link.Reference>(await ValueCodec.ParseAsync(link, definition, "Reso_1"));
        Assert.Equal("Reso_1", reference.TargetID);
        var nullReference = Assert.IsType<Link.Reference>(await ValueCodec.ParseAsync(link, definition, "null"));
        Assert.Null(nullReference.TargetID);
    }

    [Fact]
    public async Task ConvertsReferenceListsFromJsonArray()
    {
        var link = new Link.LinkInterface();
        var definition = new Link.ListDefinition
        {
            ElementDefinition = new Link.ReferenceDefinition { TargetType = new Link.TypeReference { Type = "FrooxEngine.Material" } }
        };

        var list = Assert.IsType<Link.SyncList>(await ValueCodec.ParseAsync(link, definition, "[\"Reso_1\",\"Reso_2\"]"));

        Assert.Equal(["Reso_1", "Reso_2"], list.Elements.Cast<Link.Reference>().Select(x => x.TargetID));
    }

    [Fact]
    public async Task ReportsMalformedListJson()
    {
        var link = new Link.LinkInterface();
        var definition = new Link.ListDefinition { ElementDefinition = new Link.ReferenceDefinition() };

        var ex = await Assert.ThrowsAsync<RLoop.Core.RLoopException>(() => ValueCodec.ParseAsync(link, definition, "[not-json]"));

        Assert.Equal("LIST_VALUE_INVALID", ex.Code);
    }

    [Fact]
    public async Task ReportsUnsupportedMemberKind()
    {
        var link = new Link.LinkInterface();
        var definition = new Link.DictionaryDefinition();
        var ex = await Assert.ThrowsAsync<RLoop.Core.RLoopException>(() => ValueCodec.ParseAsync(link, definition, "1"));
        Assert.Equal("MEMBER_TYPE_UNSUPPORTED", ex.Code);
    }
}
