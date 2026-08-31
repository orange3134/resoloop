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

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("{\"x\":1,\"y\":2,\"z\":3}")]
    [InlineData("1,2,3")]
    public async Task ConvertsStructuredValuesFromCanonicalAndCompatibilityShapes(string raw)
    {
        var link = new Link.LinkInterface();
        var definition = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "float3" } };

        var result = Assert.IsType<Link.Field_float3>(await ValueCodec.ParseAsync(link, definition, raw));

        Assert.Equal(1, result.Value.x);
        Assert.Equal(2, result.Value.y);
        Assert.Equal(3, result.Value.z);
    }

    [Fact]
    public async Task ConvertsReflectedIntegerTupleFromCanonicalArray()
    {
        var link = new Link.LinkInterface();
        var definition = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "int3" } };

        var result = Assert.IsAssignableFrom<Link.Field>(await ValueCodec.ParseAsync(link, definition, "[1,2,3]"));
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        object? Coordinate(string name) => value.GetType().GetField(name)?.GetValue(value) ??
                                            value.GetType().GetProperty(name)?.GetValue(value);

        Assert.Equal("Field_int3", result.GetType().Name);
        Assert.Equal(1, Coordinate("x"));
        Assert.Equal(2, Coordinate("y"));
        Assert.Equal(3, Coordinate("z"));
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
    public async Task ConvertsStringDictionary()
    {
        var link = new Link.LinkInterface();
        var definition = new Link.DictionaryDefinition
        {
            KeyType = new Link.TypeReference { Type = "string" },
            ElementDefinition = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "int" } }
        };
        var dictionary = Assert.IsAssignableFrom<Link.SyncDictionary>(await ValueCodec.ParseAsync(link, definition, "{\"one\":1,\"two\":2}"));
        var elements = Assert.IsAssignableFrom<System.Collections.IDictionary>(dictionary.GetType().GetProperty("Elements")!.GetValue(dictionary));
        Assert.Equal(2, elements.Count);
    }

    [Fact]
    public async Task ConvertsNullableAndNestedValueThroughRuntimeFieldWrappers()
    {
        var link = new Link.LinkInterface();
        var nullable = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "System.Nullable<float>" } };
        var nullableField = await ValueCodec.ParseAsync(link, nullable, "null");
        Assert.Contains("Nullable", nullableField.GetType().Name);

        var nested = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "float3x3" } };
        var nestedField = await ValueCodec.ParseAsync(link, nested, "{}");
        Assert.Equal("Field_float3x3", nestedField.GetType().Name);
    }

    [Fact]
    public void ValidatesEnumFlagsBoundaries()
    {
        var values = new Dictionary<string, long> { ["Read"] = 1, ["Write"] = 2 };
        Assert.Equal("Read,Write", ValueCodec.ValidateEnumValue("Permissions", values, true, "Read, Write"));
        Assert.Equal("ENUM_FLAGS_INVALID", Assert.Throws<RLoop.Core.RLoopException>(() =>
            ValueCodec.ValidateEnumValue("Mode", values, false, "Read,Write")).Code);
        Assert.Equal("ENUM_VALUE_INVALID", Assert.Throws<RLoop.Core.RLoopException>(() =>
            ValueCodec.ValidateEnumValue("Permissions", values, true, "Execute")).Code);
    }
}
