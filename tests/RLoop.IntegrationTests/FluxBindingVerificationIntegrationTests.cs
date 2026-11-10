using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Flux;
using RLoop.Flux.Deployer;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class FluxBindingVerificationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreservesVerifiedWiringAndRedeploysSwappedNamedInputs()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var uri = new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!);
        var name = "ResoLoop_Test_FluxBindings_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(directory);
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(uri, TimeSpan.FromSeconds(20), ct);
        string? root = null;
        try
        {
            const string valueType = "[FrooxEngine]FrooxEngine.ValueField<bool>";
            Assert.Contains((await client.DescribeComponentTypeAsync(valueType, ct)).Members, member => member.Name == "Value");
            foreach (var (type, member) in new[]
            {
                ("[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.Slot>", "Reference"),
                ("[ProtoFluxBindings]FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.ElementSource<[FrooxEngine]FrooxEngine.Slot>", "Source"),
                ("[FrooxEngine]FrooxEngine.ProtoFlux.CoreNodes.FieldDriveBase<bool>+Proxy", "Drive"),
                ("[FrooxEngine]FrooxEngine.ProtoFlux.CoreNodes.FieldDriveBase<bool>+Proxy", "Node")
            })
                Assert.Contains((await client.DescribeComponentTypeAsync(type, ct)).Members, m => m.Name == member);

            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-40, -40, -40)), ct);
            var first = await client.CreateSlotAsync(new SlotCreateRequest(root, "First"), ct);
            var second = await client.CreateSlotAsync(new SlotCreateRequest(root, "Second"), ct);
            var source = await client.AddComponentAsync(first, valueType, new Dictionary<string,string> { ["Value"] = "true" }, ct);
            var target = await client.AddComponentAsync(second, valueType, new Dictionary<string,string> { ["Value"] = "false" }, ct);
            var sourceMember = (await client.GetComponentAsync(source.Id, ct)).Members["Value"].Id!;
            var targetMember = (await client.GetComponentAsync(target.Id, ct)).Members["Value"].Id!;
            await File.WriteAllTextAsync(Path.Combine(directory, "protograph.toml"), "name = \"binding-review\"\nversion = \"0.1.0\"\n[dependencies]\n", ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "BindingReview.pg"), """
                module BindingReview
                in First: Slot element
                in Second: Slot element
                in Flag: bool
                out Result: bool
                where {
                    First->display;
                    Second->display;
                    Result = Flag;
                }
                """, ct);
            var manifest = Path.Combine(directory, "manifest.json");
            await File.WriteAllTextAsync(manifest, """
                {"schemaVersion":"1","deployState":"deploy-state.json","modules":[
                 {"name":"review","module":"BindingReview","source":"BindingReview.pg","bindings":{
                   "First":{"target":"$slot:first","mode":"source"},
                   "Second":{"target":"$slot:second","mode":"source"},
                   "Flag":{"target":"$member:source.Value","mode":"source"},
                   "Result":{"target":"$member:target.Value","mode":"drive"}}}]}
                """, ct);
            Dictionary<string, FluxResolvedModuleBindings> Bindings(bool swap) => new()
            {
                ["review"] = new([
                    new("First","source","$slot:first",swap ? second : first,"slot","Slot"),
                    new("Second","source","$slot:second",swap ? first : second,"slot","Slot"),
                    new("Flag","source","$member:source.Value",sourceMember,"member","bool"),
                    new("Result","drive","$member:target.Value",targetMember,"member","bool")])
            };
            async Task<string?> Resolve(FluxModuleSpec _, CancellationToken token) =>
                (await client.GetSlotAsync(root, 1, false, token)).Children.SingleOrDefault(slot => slot.Name == "BindingReview")?.Id;
            async Task<bool> Verify(string id, FluxResolvedModuleBindings bindings, CancellationToken token) =>
                FluxSdkBindingInspector.Verify(await client.GetSlotAsync(id, 1, true, token), bindings.InputMap, bindings.OutputMap);
            var tool = new FluxProcessTool(Environment.GetEnvironmentVariable("RESOLOOP_FLUX_EXECUTABLE") ?? "flux-sdk", new FluxSdkDeployer());
            var check = await tool.CheckAsync(new FluxBuildRequest(Path.Combine(directory, "BindingReview.pg"), directory, null,
                Environment.GetEnvironmentVariable("RESONITE_MANAGED_DATA_PATH")), ct);
            Assert.True(check.Success, check.StandardOutput + check.StandardError);
            var service = new FluxManifestOrchestrator(tool);
            Task<FluxManifestResult> Deploy(bool swap = false) => service.DeployAsync(manifest, root, uri,
                Environment.GetEnvironmentVariable("RESONITE_MANAGED_DATA_PATH"), null, "test-connection",
                Bindings(swap), Resolve, ct, Verify);
            var initial = await Deploy();
            Assert.True(initial.Success, System.Text.Json.JsonSerializer.Serialize(initial));
            var moduleId = Assert.Single(initial.Modules).ModuleSlotIdAfter;
            Assert.True(await Verify(moduleId!, Bindings(false)["review"], ct));
            Assert.Equal("no-op", Assert.Single((await Deploy()).Modules).Action);

            // Simulate stale checkpoint IDs after save/reload, without restarting or modifying the user's world.
            var statePath = Path.Combine(directory, "deploy-state.json");
            var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath, ct))!;
            state["parentSlotId"] = "OldParent";
            state["modules"]!["review"]!["slotId"] = "OldModule";
            foreach (var binding in Bindings(false)["review"].Bindings)
                state["modules"]!["review"]!["targets"]![binding.Name] = "Old-" + binding.Name;
            await File.WriteAllTextAsync(statePath, state.ToJsonString(), ct);
            var recovered = Assert.Single((await Deploy()).Modules);
            Assert.Equal("no-op", recovered.Action);
            Assert.Equal(moduleId, recovered.ModuleSlotIdAfter);

            var swapped = await Deploy(true);
            Assert.True(swapped.Success, System.Text.Json.JsonSerializer.Serialize(swapped));
            var changed = Assert.Single(swapped.Modules);
            Assert.Equal("update", changed.Action);
            Assert.NotEqual(moduleId, changed.ModuleSlotIdAfter);
            Assert.True(await Verify(changed.ModuleSlotIdAfter!, Bindings(true)["review"], ct));
            Assert.Equal("no-op", Assert.Single((await Deploy(true)).Modules).Action);

            // Checkpoint and target IDs stay the same, but an in-world edit disconnects this named input.
            var snapshot = await client.GetSlotAsync(changed.ModuleSlotIdAfter!, 1, true, ct);
            var port = snapshot.Children.Single(slot => slot.Name == "Input:[Elem]First");
            var backing = port.Components.Single(component => component.Type.StartsWith("[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<", StringComparison.Ordinal));
            await client.SetComponentMemberAsync(backing.Id, "Reference", first, ct);
            var repaired = await Deploy(true);
            Assert.True(repaired.Success, System.Text.Json.JsonSerializer.Serialize(repaired));
            var repair = Assert.Single(repaired.Modules);
            Assert.Equal("update", repair.Action);
            Assert.NotEqual(changed.ModuleSlotIdAfter, repair.ModuleSlotIdAfter);
            Assert.True(await Verify(repair.ModuleSlotIdAfter!, Bindings(true)["review"], ct));
        }
        finally
        {
            if (root is not null)
            {
                var observed = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(root, observed.Id);
                Assert.Equal(name, observed.Name);
                Assert.Equal("Root", observed.ParentId);
                Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root);
            }
            Directory.Delete(directory, true);
        }
    }
}
