using System.Text.Json;
using RLoop.Core;
using RLoop.Flux;

namespace RLoop.Tests;

public sealed class P1WorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rloop-p1-" + Guid.NewGuid().ToString("N"));
    public P1WorkflowTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CompilerExpandsIncludeParametersPrototypeAndRepeat()
    {
        var include = Path.Combine(_root, "common.json");
        File.WriteAllText(include, """
            { "prototypes": { "box": { "slot": { "key": "box-${i}", "name": "Box ${i}", "position": [0,0,0] }, "components": [] } } }
            """);
        var main = Path.Combine(_root, "world.json");
        File.WriteAllText(main, """
            {
              "include": "common.json",
              "schemaVersion": "1",
              "ownership": { "key": "compiler" },
              "parameters": { "spacing": 2 },
              "variables": { "title": "Compiled" },
              "slot": { "key": "root", "name": "${title}", "parent": "Root" },
              "children": [
                { "$prototype": "box", "$repeat": { "count": 3, "as": "i", "offset": ["${spacing}",0,0] } }
              ]
            }
            """);

        var document = ApplyDocument.Load(main);

        Assert.Equal("Compiled", document.Slot!.Name);
        Assert.Equal(3, document.Children!.Count);
        Assert.Equal([0f, 0f, 0f], document.Children[0].Slot.Position!);
        Assert.Equal([4f, 0f, 0f], document.Children[2].Slot.Position!);
        Assert.Equal(3, document.Compilation!.Instances);
        Assert.Equal(2, document.Compilation.SourceFiles);
    }

    [Fact]
    public void CompilerRejectsIncludeCyclesAndExpandedKeyConflicts()
    {
        var a = Path.Combine(_root, "a.json");
        var b = Path.Combine(_root, "b.json");
        File.WriteAllText(a, "{\"include\":\"b.json\"}");
        File.WriteAllText(b, "{\"include\":\"a.json\"}");
        Assert.Equal("APPLY_INCLUDE_CYCLE", Assert.Throws<RLoopException>(() => ApplyDocument.Load(a)).Code);

        var conflict = Path.Combine(_root, "conflict.json");
        File.WriteAllText(conflict, """
            { "schemaVersion":"1", "ownership":{"key":"x"}, "slot":{"key":"root","name":"X"},
              "children":[{"slot":{"key":"same","name":"A"}},{"slot":{"key":"same","name":"B"}}] }
            """);
        Assert.Equal("APPLY_EXPANDED_KEY_CONFLICT", Assert.Throws<RLoopException>(() => ApplyDocument.Load(conflict)).Code);
    }

    [Fact]
    public async Task CaptureProducesDeterministicSvgAndSceneSummaryArtifacts()
    {
        var source = Path.Combine(_root, "capture.json");
        File.WriteAllText(source, """
            { "schemaVersion":"1", "ownership":{"key":"capture"},
              "slot":{"key":"root","name":"RLoop_Test_Capture","position":[0,0,0],"scale":[2,2,2]},
              "cameras":{"main":{"position":[0,2,-6],"target":[0,0,0],"width":640,"height":360,"representative":true}},
              "children":[{"slot":{"key":"child","name":"Child","position":[1,0,0]}}] }
            """);
        var output = Path.Combine(_root, "capture.svg");

        var result = await SceneArtifactService.CaptureAsync(ApplyDocument.Load(source), "main", output);

        Assert.False(result.ScreenshotAvailable);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(result.SummaryOutput));
        Assert.Equal(2, result.Summary.Slots);
        Assert.Contains("rloop wireframe", File.ReadAllText(output));
    }

    [Fact]
    public async Task FluxManifestBuildsAndDeploysModulesInDependencyOrderAndThenNoOps()
    {
        File.WriteAllText(Path.Combine(_root, "base.pg"), "module Base where { 1->display }");
        File.WriteAllText(Path.Combine(_root, "main.pg"), "module Main where { 2->display }");
        var manifest = Path.Combine(_root, "flux.json");
        File.WriteAllText(manifest, """
            { "schemaVersion":"1", "modules":[
              {"name":"main","source":"main.pg","module":"Main","dependsOn":["base"]},
              {"name":"base","source":"base.pg","module":"Base"}
            ] }
            """);
        var fake = new FakeFluxTool();
        var orchestrator = new FluxManifestOrchestrator(fake);

        var first = await orchestrator.DeployAsync(manifest, "Reso_Parent", new Uri("ws://localhost:12449"), null, null);
        var second = await orchestrator.DeployAsync(manifest, "Reso_Parent", new Uri("ws://localhost:12449"), null, null);

        Assert.True(first.Success);
        Assert.Equal(["Base", "Main"], fake.Deployed);
        Assert.All(second.Modules, module => Assert.Equal("no-op", module.Action));
        Assert.False(first.Atomic);
    }

    [Theory]
    [InlineData("1.9.0", true)]
    [InlineData("1.9.3-preview", true)]
    [InlineData("2.0.0", false)]
    [InlineData(null, false)]
    public void FluxSdkCompatibilityIsExplicit(string? version, bool expected) =>
        Assert.Equal(expected, FluxCompatibility.Check(version).Compatible);

    [Fact]
    public void FluxDiagnosticsParseStdoutLocationsAndSeparatePrimaryFromCascade()
    {
        const string stdout = """
            C:/project/Main.pg(3,3,3,4): error: Unable to parse expression inside a context sequence:
            Expecting: Expression
            C:/project/Main.pg(3,3,3,4): warning: Found a bottom value. Skipping node generation for it and its children.
            """;

        var diagnostics = FluxDiagnostics.Parse(stdout, "");

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("stdout", diagnostics[0].Channel);
        Assert.Equal("parse", diagnostics[0].Category);
        Assert.True(diagnostics[0].IsPrimary);
        Assert.Equal("cascade", diagnostics[1].Category);
        Assert.False(diagnostics[1].IsPrimary);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeFluxTool : IFluxTool
    {
        public List<string> Deployed { get; } = [];
        public Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new FluxResult(true, 0, "", ""));
        public Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => BuildAsync(request, cancellationToken);
        public Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => BuildAsync(request, cancellationToken);
        public Task<FluxResult> DeployAsync(FluxDeployRequest request, CancellationToken cancellationToken = default)
        {
            Deployed.Add(request.Module);
            return Task.FromResult(new FluxResult(true, 0, "", "", "Reso_" + request.Module));
        }
        public Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new FluxToolStatus(true, "fake", "1.9.0"));
    }
}
