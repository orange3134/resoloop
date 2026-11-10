using RLoop.Core;
using RLoop.Flux;
using RLoop.Flux.Deployer;

namespace RLoop.Tests;

public sealed partial class P1WorkflowTests
{
    [Theory]
    [InlineData("unchanged", "no-op")]
    [InlineData("reload", "no-op")]
    [InlineData("swapped-targets", "update")]
    [InlineData("rewired-same-ids", "update")]
    [InlineData("unrelated-reference", "update")]
    [InlineData("duplicate-port", "update")]
    [InlineData("missing-verifier", "update")]
    public async Task FluxNoOpRequiresEachNamedBindingToMatch(string scenario, string action)
    {
        File.WriteAllText(Path.Combine(_root, "Swap.pg"),
            "module Swap\nin First: Slot element\nin Second: Slot element\nwhere { First->display; Second->display }");
        var manifest = Path.Combine(_root, "swap.json");
        File.WriteAllText(manifest, """
            {"schemaVersion":"1","modules":[{"name":"swap","source":"Swap.pg","module":"Swap","bindings":{
              "First":{"target":"$slot:first","mode":"source"},
              "Second":{"target":"$slot:second","mode":"source"}}}]}
            """);
        Dictionary<string, FluxResolvedModuleBindings> Bindings(string a, string b) => new()
        {
            ["swap"] = new([new("First","source","$slot:first",a,"slot","Slot"),
                           new("Second","source","$slot:second",b,"slot","Slot")])
        };
        var fake = new FakeFluxTool();
        var service = new FluxManifestOrchestrator(fake);
        var uri = new Uri("ws://localhost:1");
        await service.DeployAsync(manifest, "Parent", uri, null, null, "connection-1", Bindings("A", "B"),
            (_, _) => Task.FromResult<string?>("Module"));
        var desired = scenario == "swapped-targets" ? Bindings("B", "A")
            : scenario == "reload" ? Bindings("NewA", "NewB") : Bindings("A", "B");
        var ports = new List<SlotInfo>
        {
            FluxBindingFixture.Input("First", scenario == "reload" ? "NewA" : scenario == "rewired-same-ids" ? "B" : "A"),
            FluxBindingFixture.Input("Second", scenario == "reload" ? "NewB" : scenario == "rewired-same-ids" ? "A" : "B")
        };
        if (scenario == "duplicate-port") ports.Add(FluxBindingFixture.Input("First", "A"));
        if (scenario == "unrelated-reference")
        {
            ports[0] = FluxBindingFixture.Input("First", "B");
            ports.Add(FluxBindingFixture.Input("Unrelated", "A"));
        }
        var module = FluxBindingFixture.Slot("module", ports);
        var report = await service.DeployAsync(manifest, scenario == "reload" ? "NewParent" : "Parent", uri, null, null,
            "connection-2", desired, (_, _) => Task.FromResult<string?>(scenario == "reload" ? "NewModule" : "Module"),
            verifyModuleBindings: scenario == "missing-verifier" ? null : (_, bindings, _) =>
                Task.FromResult(FluxSdkBindingInspector.Verify(module, bindings.InputMap, bindings.OutputMap)));
        Assert.Equal(action, Assert.Single(report.Modules).Action);
        Assert.Equal(action == "update" ? 2 : 1, fake.Requests.Count);
        if (action == "update") Assert.Equal(desired["swap"].InputMap, fake.Requests[^1].InputMap);
    }
}

public sealed class FluxBindingInspectorTests
{
    [Fact]
    public void KeepsOutputNamesDistinctEvenWhenTheTargetSetIsTheSame()
    {
        var module = FluxBindingFixture.Slot("Module", [FluxBindingFixture.Output("First", "A"), FluxBindingFixture.Output("Second", "B")]);
        Assert.True(FluxBindingFixture.Verify(module, outputs: new() { ["First"] = "A", ["Second"] = "B" }));
        Assert.False(FluxBindingFixture.Verify(module, outputs: new() { ["First"] = "B", ["Second"] = "A" }));
    }

    [Theory]
    [InlineData("Input:", "ValueSource")]
    [InlineData("Input:", "ObjectValueSource")]
    [InlineData("Input:", "ReferenceSource")]
    [InlineData("Input:[Elem]", "ElementSource")]
    [InlineData("Input:[Global]", null)]
    public void RecognizesNamedInputForms(string prefix, string? nodeKind)
    {
        var port = FluxBindingFixture.Input("Source", "Expected", prefix, nodeKind);
        Assert.True(FluxBindingFixture.Verify(FluxBindingFixture.Slot("Module", [port]), inputs: new() { ["Source"] = "Expected" }));
    }

    [Theory]
    [InlineData("ValueFieldDrive", "FieldDriveBase")]
    [InlineData("ObjectFieldDrive", "FieldDriveBase")]
    [InlineData("ReferenceDrive", "ReferenceDrive")]
    public void RecognizesNamedOutputForms(string nodeKind, string proxyKind)
    {
        var port = FluxBindingFixture.Output("Result", "Expected", nodeKind, proxyKind);
        Assert.True(FluxBindingFixture.Verify(FluxBindingFixture.Slot("Module", [port]), outputs: new() { ["Result"] = "Expected" }));
    }

    [Theory]
    [InlineData("swapped")]
    [InlineData("wrong-direction")]
    [InlineData("duplicate")]
    [InlineData("nested")]
    [InlineData("missing-members")]
    [InlineData("unknown-type")]
    [InlineData("disconnected-source")]
    [InlineData("disconnected-drive")]
    [InlineData("extra-backing")]
    [InlineData("reference-only")]
    public void DoesNotAcceptUnrelatedAmbiguousOrDisconnectedReferences(string fault)
    {
        var input = FluxBindingFixture.Input("First", "A");
        var output = FluxBindingFixture.Output("Second", "B");
        var children = new List<SlotInfo> { input, output };
        switch (fault)
        {
            case "swapped": children = [FluxBindingFixture.Input("First", "B"), FluxBindingFixture.Output("Second", "A")]; break;
            case "wrong-direction": children = [FluxBindingFixture.Output("First", "A"), FluxBindingFixture.Input("Second", "B")]; break;
            case "duplicate": children.Add(input); break;
            case "nested": children = [FluxBindingFixture.Slot("Wrapper", children)]; break;
            case "missing-members": children[0] = input with { Components = input.Components.Select(c => c with { Members = null }).ToArray() }; break;
            case "unknown-type": children[0] = input with { Components = input.Components.Select(c => c with { Type = "Other" }).ToArray() }; break;
            case "disconnected-source": ((Dictionary<string, MemberValue>)input.Components[0].Members!)["Source"] = new("reference", TargetId: "Other"); break;
            case "disconnected-drive": ((Dictionary<string, MemberValue>)output.Components[1].Members!)["Node"] = new("reference", TargetId: "Other"); break;
            case "extra-backing": children[0] = input with { Components = [..input.Components, input.Components[1] with { Id = "Duplicate" }] }; break;
            case "reference-only": children[0] = input with { IsReferenceOnly = true }; break;
        }
        Assert.False(FluxBindingFixture.Verify(FluxBindingFixture.Slot("Module", children),
            new() { ["First"] = "A" }, new() { ["Second"] = "B" }));
    }
}

internal static class FluxBindingFixture
{
    private const string Nodes = "[ProtoFluxBindings]FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.";
    public static SlotInfo Slot(string name, IReadOnlyList<SlotInfo>? children = null, IReadOnlyList<ComponentSummary>? components = null) =>
        new(name, name, null, null, null, null, true, true, null, false, components ?? [], children ?? []);

    public static SlotInfo Input(string name, string target, string prefix = "Input:[Elem]", string? nodeKind = "ElementSource")
    {
        var backingId = name + "-backing";
        var backing = new ComponentSummary(backingId, "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<object>",
            new Dictionary<string, MemberValue> { ["Reference"] = new("reference", TargetId: target) });
        var node = new ComponentSummary(name + "-node", Nodes + nodeKind + "<object>",
            new Dictionary<string, MemberValue> { ["Source"] = new("reference", TargetId: backingId) });
        return Slot(prefix + name, components: nodeKind is null ? [backing] : [node, backing]);
    }

    public static SlotInfo Output(string name, string target, string nodeKind = "ValueFieldDrive", string proxyKind = "FieldDriveBase")
    {
        var nodeId = name + "-node";
        var node = new ComponentSummary(nodeId, Nodes + nodeKind + "<object>", new Dictionary<string, MemberValue>());
        var backing = new ComponentSummary(name + "-backing", "[FrooxEngine]FrooxEngine.ProtoFlux.CoreNodes." + proxyKind + "<object>+Proxy",
            new Dictionary<string, MemberValue> { ["Drive"] = new("reference", TargetId: target), ["Node"] = new("reference", TargetId: nodeId) });
        return Slot("Output:" + name, components: [node, backing]);
    }

    public static bool Verify(SlotInfo module, Dictionary<string, string>? inputs = null, Dictionary<string, string>? outputs = null) =>
        FluxSdkBindingInspector.Verify(module, inputs ?? [], outputs ?? []);
}
