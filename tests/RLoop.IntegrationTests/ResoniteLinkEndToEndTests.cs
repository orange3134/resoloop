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
}
