using System.Text.Json;
using RLoop.Core;
using RLoop.Flux;

namespace RLoop.Tests;

public sealed class RealWorldFixtureTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "resoloop-real-world-fixtures-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CameraFixtureRejectsBrokenQuaternionAndAcceptsFixedDocument()
    {
        var broken = await ApplyDocumentValidator.ValidateAsync(ApplyDocument.Load(Fixture("camera", "broken.apply.json")));
        var fixedResult = await ApplyDocumentValidator.ValidateAsync(ApplyDocument.Load(Fixture("camera", "fixed.apply.json")));

        Assert.Contains(broken.Issues, issue => issue.Code == "APPLY_QUATERNION_INVALID");
        Assert.True(fixedResult.Valid);
    }

    [Fact]
    public void TeleporterFixtureRequiresRuntimeInsideThePortableRoot()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var broken = JsonSerializer.Deserialize<SlotInfo>(File.ReadAllText(Fixture("teleporter", "broken.audit.json")), options)!;
        var fixedItem = JsonSerializer.Deserialize<SlotInfo>(File.ReadAllText(Fixture("teleporter", "fixed.audit.json")), options)!;

        var brokenReport = ItemAuditService.Audit(broken, strict: true);
        var fixedReport = ItemAuditService.Audit(fixedItem, strict: true);

        Assert.False(brokenReport.Portable);
        Assert.Contains(brokenReport.Issues, issue => issue.Code == "ITEM_FLUX_EXTERNAL_REFERENCE");
        Assert.True(fixedReport.Portable);
    }

    [Fact]
    public async Task UixFixtureRejectsInterfaceGlobalAndFixedElementBindingConverges()
    {
        Directory.CreateDirectory(_temp);
        foreach (var source in Directory.GetFiles(Path.GetDirectoryName(Fixture("uix", "broken.pg"))!))
            File.Copy(source, Path.Combine(_temp, Path.GetFileName(source)));
        var resolved = new Dictionary<string, FluxResolvedModuleBindings>
        {
            ["team-panel"] = new([new("TouchButton", "source", "$component:button", "C_Button", "component",
                "FrooxEngine.PhysicalButton")])
        };
        var tool = new FixtureFluxTool();
        var orchestrator = new FluxManifestOrchestrator(tool);

        var broken = await Assert.ThrowsAsync<RLoopException>(() => orchestrator.DeployAsync(
            Path.Combine(_temp, "broken.flux.json"), "S_Parent", new Uri("ws://localhost:1"), null, null,
            "session", resolved));
        var first = await orchestrator.DeployAsync(Path.Combine(_temp, "fixed.flux.json"), "S_Parent",
            new Uri("ws://localhost:1"), null, null, "session", resolved);
        var second = await orchestrator.DeployAsync(Path.Combine(_temp, "fixed.flux.json"), "S_Parent",
            new Uri("ws://localhost:1"), null, null, "session", resolved);

        Assert.Equal("FLUX_INTERFACE_GLOBAL_UNSUPPORTED", broken.Code);
        Assert.True(first.Success);
        Assert.True(Assert.Single(first.Modules).Deployed);
        Assert.Equal("no-op", Assert.Single(second.Modules).Action);
        Assert.Single(tool.DeployRequests);
    }

    private static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "real-world", .. parts]);

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }

    private sealed class FixtureFluxTool : IFluxTool
    {
        public List<FluxDeployRequest> DeployRequests { get; } = [];
        public Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FluxResult(true, 0, "Packing 1 ProtoFlux nodes and 0 comments.", ""));
        public Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => BuildAsync(request, cancellationToken);
        public Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => BuildAsync(request, cancellationToken);
        public Task<FluxResult> DeployAsync(FluxDeployRequest request, CancellationToken cancellationToken = default)
        {
            DeployRequests.Add(request);
            return Task.FromResult(new FluxResult(true, 0, "", "", "S_Module"));
        }
        public Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FluxToolStatus(true, "fixture", "1.9.0"));
    }
}
