using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class CaptureIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CapturesLocalScreenshotAndRemovesDedicatedCamera()
    {
        // Also opt in to writing an actual photo to the user's screenshot library.
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1" ||
            Environment.GetEnvironmentVariable("RESOLOOP_RUN_CAPTURE_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
            ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var screenshots = Environment.GetEnvironmentVariable("RESOLOOP_SCREENSHOTS_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Resonite");
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-capture-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = Path.Combine(directory, "scene.json");
            await File.WriteAllTextAsync(manifest, """
                {"schemaVersion":"1","ownership":{"key":"capture-integration"},
                 "slot":{"key":"root","name":"ResoLoop_Test_CaptureIntegration"},
                 "cameras":{"main":{"position":[0,2,-6],"target":[0,1,0],"width":640,"height":360}}}
                """);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await using var client = new ResoniteLinkClientAdapter();
            await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(15), deadline.Token);
            var before = (await client.GetSlotAsync("Root", 1, false, deadline.Token)).Children
                .Where(s => s.Name.StartsWith("ResoLoop_Test_Capture_", StringComparison.Ordinal)).Select(s => s.Id).Order().ToArray();
            var result = await new LiveCaptureService(client).CaptureAsync(ApplyDocument.Load(manifest), "main",
                Path.Combine(directory, "capture.jpg"), screenshots, cancellationToken: deadline.Token);
            Assert.True(result.ScreenshotAvailable);
            Assert.Equal((640, 360, "jpeg"), ScreenshotExport.ImageDimensions(await File.ReadAllBytesAsync(result.Output, deadline.Token)));
            var after = (await client.GetSlotAsync("Root", 1, false, deadline.Token)).Children
                .Where(s => s.Name.StartsWith("ResoLoop_Test_Capture_", StringComparison.Ordinal)).Select(s => s.Id).Order().ToArray();
            Assert.Equal(before, after);
        }
        finally { Directory.Delete(directory, true); }
    }
}
