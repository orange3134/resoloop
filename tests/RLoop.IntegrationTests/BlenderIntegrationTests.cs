using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class BlenderIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildsExportsImportsAndReappliesTexturedBlenderProp()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1" ||
            Environment.GetEnvironmentVariable("RESOLOOP_RUN_BLENDER_TESTS") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL") ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var blender = BlenderDiscovery.Resolve(Environment.GetEnvironmentVariable("RESOLOOP_BLENDER_EXECUTABLE"));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "ResoLoop.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var suffix = Guid.NewGuid().ToString("N");
        var name = "ResoLoop_Test_Blender_" + suffix;
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-blender-" + suffix);
        Directory.CreateDirectory(directory);
        string? root = null;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(45));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30), token);
        try
        {
            var textureType = await client.DescribeComponentTypeAsync("FrooxEngine.StaticTexture2D", token);
            var profile = await client.DescribeTypeAsync(ReflectedMemberType.ValueType(textureType, "PreferredProfile"), token);
            Assert.True(profile.IsEnum);
            Assert.Equal(0L, profile.EnumValues!["Linear"]);
            Assert.Equal(1L, profile.EnumValues["sRGB"]);
            var blend = Path.Combine(directory, "prop with spaces.blend");
            await BlenderProcess.RunAsync(blender.Executable, BlenderProcess.ScriptArguments(
                Path.Combine(repository!.FullName, "examples", "blender", "build_prop.py"), arguments: [blend]), token);
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(0, 2, 2)), token);
            var bundle = Path.Combine(directory, "bundle");
            var report = await BlenderExport.ExportAsync(blender.Executable, blend, bundle, "Prop", root, cancellationToken: token);
            Assert.False(report.GetProperty("rendered").GetBoolean());
            Assert.InRange(report.GetProperty("triangles").GetInt32(), 100, 2000);
            var path = Path.Combine(bundle, "model.apply.json");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
            json["cameras"] = JsonNode.Parse("""
                {"check":{"position":[0,2.15,-0.5],"target":[0,2.15,2],"fieldOfView":40,"width":640,"height":480}}
                """);
            await File.WriteAllTextAsync(path, json.ToJsonString(), token);
            var document = ApplyDocument.Load(path);
            ApplyDocumentValidator.ThrowIfInvalid(await ApplyDocumentValidator.ValidateAsync(document, client, token));
            var world = new WorldService(client);
            var options = new ApplyOptions(Path.Combine(directory, "state.json"));
            var applied = await world.ApplyAsync(document, options, token);
            Assert.Equal(document.Assets!.Count, applied.AssetsImported);
            var observed = await client.GetSlotAsync(applied.SlotId, 3, true, token);
            Assert.Equal(4, observed.Children.Count);
            var providers = observed.Children.Single(c => c.Name == "Blender_Providers").Children.SelectMany(c => c.Components).ToArray();
            foreach (var child in observed.Children.Where(c => c.Name != "Blender_Providers"))
            {
                var mesh = Assert.Single(child.Components, c => c.Type.EndsWith(".StaticMesh"));
                var renderer = Assert.Single(child.Components, c => c.Type.EndsWith(".MeshRenderer"));
                Assert.Contains(new Uri(mesh.Members!["URL"].Value!.GetValue<string>()).Scheme, new[] { "local", "resdb" });
                Assert.Equal(mesh.Id, renderer.Members!["Mesh"].TargetId);
                Assert.NotEmpty(renderer.Members["Materials"].Elements!);
                foreach (var element in renderer.Members["Materials"].Elements!)
                    Assert.Contains(providers, c => c.Id == element.TargetId && c.Type.EndsWith(".PBS_Metallic"));
            }
            var again = await world.ApplyAsync(document, options, token);
            Assert.Equal(0, again.AssetsImported);
            Assert.Equal(0, again.ComponentsUpdated);
            Assert.Equal(0, again.ComponentsAdded);
            Assert.Equal(0, again.SlotsCreated);
            if (Environment.GetEnvironmentVariable("RESOLOOP_BLENDER_CAPTURE_FILE") is { Length: > 0 } capture)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capture))!);
                await File.WriteAllTextAsync(Path.ChangeExtension(capture, ".observed.json"), System.Text.Json.JsonSerializer.Serialize(observed), token);
                var screenshots = Environment.GetEnvironmentVariable("RESOLOOP_SCREENSHOTS_DIR") ?? ScreenshotDirectoryResolver.ResolveDefault();
                await Task.Delay(2000, token); // Asset integration is asynchronous; capture is an opt-in visual check.
                var captured = await new LiveCaptureService(client).CaptureAsync(document, "check", capture, screenshots, 640, 480, 60, token);
                Assert.True(captured.Ownership!.CleanupCompleted);
                Assert.StartsWith("ResoLoop_Test_Capture_", captured.Ownership.SlotName);
                Assert.False(string.IsNullOrEmpty(captured.Ownership.ParentId));
                Assert.True(File.Exists(capture));
            }
        }
        finally
        {
            if (root is not null)
            {
                var observed = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(name, observed.Name);
                Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root);
            }
            Directory.Delete(directory, true);
        }
    }
}
