using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void HierarchySummaryAndScopeDoNotConsumeEachOther()
    {
        var parsed = ParsedArguments.Parse(["hierarchy", "--summary", "--include-components", "--under", "$slot:root", "--state", "world.json", "--depth", "1", "--json"]);
        Assert.True(parsed.Has("summary"));
        Assert.True(parsed.Has("include-components"));
        Assert.Equal("$slot:root", parsed.Option("under"));
        Assert.Equal("world.json", parsed.Option("state"));
        Assert.Equal(1, parsed.IntOption("depth", 2));
        Assert.Equal(["hierarchy"], parsed.Positionals);
    }
    [Fact]
    public void ParsesCommandsOptionsAndRepeatedAssignments()
    {
        var parsed = ParsedArguments.Parse(["component", "add", "Root", "Grabbable", "--set", "Scalable=true", "--set=Enabled=false", "--json"]);
        Assert.Equal(["component", "add", "Root", "Grabbable"], parsed.Positionals);
        Assert.Equal(["Scalable=true", "Enabled=false"], parsed.Options("set"));
        Assert.True(parsed.Has("json"));
    }

    [Fact]
    public void BooleanOptionDoesNotConsumeFollowingCommand()
    {
        var parsed = ParsedArguments.Parse(["--json", "status"]);
        Assert.Equal(["status"], parsed.Positionals);
        Assert.True(parsed.Has("json"));
    }

    [Fact]
    public void IntegerOptionValidatesRange()
    {
        var parsed = ParsedArguments.Parse(["hierarchy", "--depth", "100"]);
        var ex = Assert.Throws<RLoopException>(() => parsed.IntOption("depth", 2, -1, 64));
        Assert.Equal("INVALID_OPTION", ex.Code);
    }

    [Fact]
    public void VectorParserUsesInvariantCommaFormat()
    {
        Assert.Equal(new Vector3Value(1, 2.5f, -3), Vector3Value.Parse("1,2.5,-3", "--position"));
        Assert.Throws<RLoopException>(() => Vector3Value.Parse("1,2", "--position"));
    }

    [Fact]
    public void SpecializesAssemblyQualifiedOpenGenericWithCSharpAliases()
    {
        Assert.Equal("[FrooxEngine]FrooxEngine.DynamicValueVariable<string>",
            GenericTypeName.Specialize("[FrooxEngine]FrooxEngine.DynamicValueVariable<>", ["System.String"]));
        Assert.Equal("GENERIC_TYPE_ASSEMBLY_REQUIRED", Assert.Throws<RLoopException>(() =>
            GenericTypeName.Specialize("FrooxEngine.DynamicValueVariable<>", ["string"])).Code);
    }
}
