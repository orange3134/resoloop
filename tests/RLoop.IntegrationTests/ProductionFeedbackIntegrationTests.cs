using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class ProductionFeedbackIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PreflightNullableNestedConvergenceAndSlotFieldOwnership()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(30), token);
        var name = "ResoLoop_Test_Feedback_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(directory);
        string? root = null;
        try
        {
            foreach (var type in new[] { "FrooxEngine.StaticTexture2D", "FrooxEngine.RelativePositioner", "FrooxEngine.Spinner" })
                Assert.NotEmpty((await client.DescribeComponentTypeAsync(type, token)).Members);
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-30, 0, -30)), token);
            var json = JsonNode.Parse("""
                {"schemaVersion":"1","ownership":{"key":"feedback"},"slot":{"key":"root","name":"Model"},
                 "components":[
                  {"key":"texture","type":"FrooxEngine.StaticTexture2D","fields":{"PreferredProfile":"NotAProfile"}},
                  {"key":"positioner","type":"FrooxEngine.RelativePositioner","fields":{"Enabled":false,"ReferenceBoundsSpace":{"LocalSpace":"$slot:root"}}},
                  {"key":"spinner","type":"FrooxEngine.Spinner","fields":{"Enabled":false,"_speed":[0,0,1],"_target":"$slot-member:driven.Rotation"}}],
                 "children":[{"slot":{"key":"driven","name":"Driven"}}]}
                """)!;
            json["slot"]!["parent"] = root;
            var path = Path.Combine(directory, "model.json");
            await File.WriteAllTextAsync(path, json.ToJsonString(), token);
            var world = new WorldService(client);
            var options = new ApplyOptions(Path.Combine(directory, "state.json"));
            client.ResetMetrics();
            var error = await Assert.ThrowsAsync<RLoopException>(() => world.ApplyAsync(ApplyDocument.Load(path), options, token));
            Assert.Contains("NotAProfile", System.Text.Json.JsonSerializer.Serialize(error.Context));
            Assert.Empty((await client.GetSlotAsync(root, 1, false, token)).Children);
            Assert.DoesNotContain(client.SnapshotMetrics().Operations, o => o.Operation.Contains("import") || o.Operation == "slot.add" || o.Operation == "component.add");

            json["components"]![0]!["fields"]!["PreferredProfile"] = "Linear";
            await File.WriteAllTextAsync(path, json.ToJsonString(), token);
            var document = ApplyDocument.Load(path);
            var applied = await world.ApplyAsync(document, options, token);
            var observed = await client.GetSlotAsync(applied.SlotId, 2, true, token);
            Assert.NotNull(observed.Members!["Position"].Id);
            var driven = Assert.Single(observed.Children);
            var spinner = observed.Components.Single(c => c.Type.EndsWith(".Spinner"));
            Assert.Equal(driven.Members!["Rotation"].Id, spinner.Members!["_target"].TargetId);
            var resolved = await world.ResolveStableReferenceAsync(Path.Combine(directory, "state.json"), "$slot-member:driven.Rotation", "new-connection", token);
            Assert.Equal(driven.Members["Rotation"].Id, resolved.Id);
            var texture = observed.Components.Single(c => c.Type.EndsWith(".StaticTexture2D"));
            Assert.Equal("Linear", texture.Members!["PreferredProfile"].Value!.GetValue<string>());
            var audit = ItemAuditService.Audit(observed);
            Assert.DoesNotContain(audit.Issues, issue => issue.Member == "_target");
            Assert.True(audit.InternalReferences >= 3);
            var again = await world.ApplyAsync(document, options, token);
            Assert.Equal(0, again.ComponentsUpdated);
            await client.SetComponentMemberAsync(texture.Id, "PreferredProfile", "null", token);
            Assert.Null((await client.GetComponentAsync(texture.Id, token)).Members["PreferredProfile"].Value);
            await client.SetComponentMemberAsync(texture.Id, "PreferredProfile", "0", token);
            Assert.Equal("Linear", (await client.GetComponentAsync(texture.Id, token)).Members["PreferredProfile"].Value!.GetValue<string>());
        }
        finally
        {
            if (root is not null)
            {
                var observed = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(root, observed.Id);
                Assert.Equal(name, observed.Name);
                Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root);
            }
            Directory.Delete(directory, true);
        }
    }
}
