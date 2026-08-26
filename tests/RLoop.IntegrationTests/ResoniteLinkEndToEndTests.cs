using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class ResoniteLinkEndToEndTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreatesMutatesAndCleansIsolatedSlot()
    {
        if (Environment.GetEnvironmentVariable("RLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var session = await client.GetSessionInfoAsync();
        Assert.True(session.Connected);
        var name = "RLoop_Test_Integration_" + Guid.NewGuid().ToString("N")[..8];
        string? slotId = null;
        try
        {
            slotId = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(0, 1.5f, 2)));
            await client.UpdateSlotAsync(new SlotUpdateRequest(slotId, Position: new Vector3Value(1, 2, 3), Scale: new Vector3Value(0.5f, 0.5f, 0.5f)));
            var created = await client.GetSlotAsync(slotId, 0, false);
            Assert.Equal(name, created.Name);
            Assert.Equal(new Vector3Value(1, 2, 3), created.Position);
            var types = await client.SearchComponentTypesAsync("Grabbable", 10);
            Assert.NotEmpty(types);
            var component = await client.AddComponentAsync(slotId, types[0], new Dictionary<string, string> { ["Scalable"] = "true" });
            await client.SetComponentMemberAsync(component.Id, "Scalable", "false");
            var inspected = await client.GetComponentAsync(component.Id);
            Assert.Contains("Scalable", inspected.Members.Keys);
        }
        finally
        {
            if (slotId is not null) await client.DeleteSlotAsync(slotId);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyCheckpointsAndConvergesWithoutSecondRunWrites()
    {
        if (Environment.GetEnvironmentVariable("RLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "RLoop_Test_Apply_" + suffix;
        var directory = Path.Combine(Path.GetTempPath(), "rloop-live-" + suffix);
        Directory.CreateDirectory(directory);
        var documentPath = Path.Combine(directory, "apply.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(documentPath, $$"""
            {
              "schemaVersion": "1",
              "ownership": { "key": "live-{{suffix}}" },
              "slot": { "key": "root", "name": "{{name}}", "parent": "Root", "position": [0, 1.5, 2] },
              "components": [
                { "key": "grabbable", "type": "FrooxEngine.Grabbable", "fields": { "Scalable": true } }
              ]
            }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var world = new WorldService(client);
        string? slotId = null;
        try
        {
            var first = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath, Profile: true));
            slotId = first.SlotId;
            Assert.Equal(1, first.SlotsCreated);
            Assert.Equal(1, first.ComponentsAdded);
            Assert.True(File.Exists(statePath));

            var second = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath, Profile: true));
            Assert.Equal(0, second.SlotsCreated);
            Assert.Equal(0, second.SlotsUpdated);
            Assert.Equal(0, second.ComponentsAdded);
            Assert.Equal(0, second.ComponentsUpdated);
            Assert.Equal(2, second.Profile!.NoOps);
        }
        finally
        {
            if (slotId is not null) await client.DeleteSlotAsync(slotId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
