using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class ReflectionCacheIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task VersionMatchedDiskDefinitionsFeedValidationAcrossConnectionsAndRecoverBeforeWrites()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-live-metadata-" + Guid.NewGuid().ToString("N"));
        var uri = new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!);
        const string material = "[FrooxEngine]FrooxEngine.UI_UnlitMaterial";
        try
        {
            foreach (var mode in new[] { "auto", "auto", "refresh", "off" })
            {
                var warm = mode == "auto" && Directory.Exists(directory);
                await using var client = new ResoniteLinkClientAdapter(reflectionCache: new(mode, directory));
                await client.ConnectAsync(uri, TimeSpan.FromSeconds(20), ct);
                await client.ValidateComponentMemberAsync(material, "Sidedness", "Front", ct);
                var metadata = await client.DescribeComponentMetadataAsync(material, ct: ct);
                Assert.Equal(!warm, metadata.Live);
                var metrics = client.SnapshotMetrics();
                Assert.Equal(warm ? 1 : mode == "off" ? 3 : 4, metrics.Requests);
                Assert.Equal(warm ? 2 : 0, metrics.ReflectionCache!.DiskHits);
            }
            // Simulate changed MOD metadata with an old but well-formed same-version enum entry.
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                var entry = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                if (entry["value"]?["enumValues"] is System.Text.Json.Nodes.JsonObject values)
                { values.Remove("Front"); File.WriteAllText(path, entry.ToJsonString()); }
            }
            await using var recovery = new ResoniteLinkClientAdapter(reflectionCache: new("auto", directory));
            await recovery.ConnectAsync(uri, TimeSpan.FromSeconds(20), ct);
            await recovery.ValidateComponentMemberAsync(material, "Sidedness", "Front", ct);
            Assert.True((await recovery.DescribeComponentMetadataAsync(material, ct: ct)).Live);
            Assert.Contains(recovery.SnapshotMetrics().Operations, p => p.Operation == "enum.get" && p.Requests == 1);
            Assert.DoesNotContain(recovery.SnapshotMetrics().Operations, p => p.Operation is "component.add" or "component.update" or "slot.add");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComponentTypeListIsReusedFromDiskAndCollectedAgainForAMissingType()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-live-component-types-" + Guid.NewGuid().ToString("N"));
        var uri = new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!);
        const string material = "[FrooxEngine]FrooxEngine.UI_UnlitMaterial";
        static bool Collected(ClientMetrics metrics) => metrics.Operations.Any(op => op.Operation.StartsWith("component-types.", StringComparison.Ordinal));
        try
        {
            foreach (var mode in new[] { "auto", "auto", "refresh", "off" })
            {
                var warm = mode == "auto" && Directory.Exists(directory);
                await using var client = new ResoniteLinkClientAdapter(reflectionCache: new(mode, directory));
                await client.ConnectAsync(uri, TimeSpan.FromSeconds(20), ct);
                Assert.Contains(material, await client.SearchComponentTypesAsync("UI_UnlitMaterial", 10, ct));
                Assert.Equal(!warm, Collected(client.SnapshotMetrics()));
            }
            // Simulate a type added since the list was cached (by a MOD, for example).
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                var entry = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                if (entry["value"] is System.Text.Json.Nodes.JsonArray types)
                { types.Remove(types.First(type => (string?)type == material)); File.WriteAllText(path, entry.ToJsonString()); }
            }
            await using var recovery = new ResoniteLinkClientAdapter(reflectionCache: new("auto", directory));
            await recovery.ConnectAsync(uri, TimeSpan.FromSeconds(20), ct);
            Assert.Equal(material, (await recovery.DescribeComponentTypeAsync("UI_UnlitMaterial", ct)).FullTypeName);
            Assert.True(Collected(recovery.SnapshotMetrics()));

            await using var later = new ResoniteLinkClientAdapter(reflectionCache: new("auto", directory));
            await later.ConnectAsync(uri, TimeSpan.FromSeconds(20), ct);
            Assert.Contains(material, await later.SearchComponentTypesAsync("UI_UnlitMaterial", 10, ct));
            Assert.False(Collected(later.SnapshotMetrics()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TypeQueriesAndRepeatedConversionShareCurrentConnectionEnumMetadata()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(reflectionCache: new("off"));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(20), ct);
        const string material = "[FrooxEngine]FrooxEngine.UI_UnlitMaterial";
        var component = await client.DescribeComponentTypeAsync(material, ct);
        var enumType = ReflectedMemberType.ValueType(component, "Sidedness");
        client.ResetMetrics();
        var definition = await client.DescribeTypeAsync(enumType, ct);
        Assert.True(definition.IsEnum); Assert.Contains("Front", definition.EnumValues!.Keys);
        for (var i = 0; i < 20; i++)
        {
            await client.DescribeTypeAsync(enumType, ct);
            await client.ValidateComponentMemberAsync(material, "Sidedness", "Front", ct);
        }
        var metrics = client.SnapshotMetrics();
        Assert.Equal(1, Assert.Single(metrics.Operations, op => op.Operation == "type.get").Requests);
        Assert.Equal(1, Assert.Single(metrics.Operations, op => op.Operation == "enum.get").Requests);
        Assert.Equal(2, metrics.Requests);
        Assert.True(metrics.CacheHits >= 40);
        // Entire test is Reflection/conversion preflight only; no Slot or Component is created.
    }
}
