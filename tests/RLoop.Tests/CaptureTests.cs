using System.Numerics;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-capture-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6XcAAAAASUVORK5CYII=");
    public CaptureTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("{\"x\":1,\"y\":2,\"z\":3}")]
    [InlineData("1,2,3")]
    public void SlotVectorsAcceptCanonicalAndLegacyForms(string raw) =>
        Assert.Equal(new Vector3Value(1, 2, 3), Vector3Value.Parse(raw, "--position"));

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("NaN,0,0")]
    [InlineData("[1e99,0,0]")]
    public void SlotVectorsRejectWrongLengthAndNonFinite(string raw) =>
        Assert.Equal("INVALID_VECTOR", Assert.Throws<RLoopException>(() => Vector3Value.Parse(raw, "--position")).Code);

    [Fact]
    public async Task ExternalMeshBoundsUseGeometryAndReportUnknownExtents()
    {
        var path = Path.Combine(_root, "bounds.json");
        await File.WriteAllTextAsync(path, """
            {"schemaVersion":"1","ownership":{"key":"bounds"},"slot":{"key":"root","name":"Bounds","position":[5,0,0]},
             "assets":{"mesh":{"kind":"mesh","source":"shape.mesh.json"}},
             "components":[{"key":"mesh","type":"FrooxEngine.StaticMesh","fields":{"URL":"$asset:mesh"}},
               {"key":"renderer","type":"FrooxEngine.MeshRenderer","fields":{"Mesh":"$component:mesh"}}]}
            """);
        var mesh = Path.Combine(_root, "shape.mesh.json");
        await File.WriteAllTextAsync(mesh, """{"vertices":[{"position":{"x":-2,"y":0,"z":0}},{"position":{"x":4,"y":3,"z":1}}]}""");
        var document = ApplyDocument.Load(path);
        var summary = await SceneArtifactService.SummarizeAsync(document);
        Assert.Equal("geometry", summary.Bounds.Kind);
        Assert.Equal(3, summary.Bounds.Min[0]);
        Assert.Equal(9, summary.Bounds.Max[0]);
        await File.WriteAllTextAsync(mesh, "{}");
        summary = await SceneArtifactService.SummarizeAsync(document);
        Assert.Equal("pivots", summary.Bounds.Kind);
        Assert.Contains(summary.Issues, i => i.Code == "SCENE_BOUNDS_INCOMPLETE");
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(-2, 3, -4)]
    public void CameraLooksAlongPositiveLocalZ(float x, float y, float z)
    {
        var q = LiveCaptureService.CameraRotation(new ApplyCameraSpec([0, 0, 0], [x, y, z]));
        var direction = Vector3.Transform(Vector3.UnitZ, new Quaternion(q.X, q.Y, q.Z, q.W));
        Assert.True(Vector3.Distance(direction, Vector3.Normalize(new Vector3(x, y, z))) < .00001f);
    }

    [Fact]
    public void InvalidCameraIsRejected()
    {
        Assert.Equal("CAPTURE_CAMERA_INVALID", Assert.Throws<RLoopException>(() =>
            LiveCaptureService.CameraRotation(new ApplyCameraSpec([0, 0, 0], [0, 0, 0]))).Code);
        Assert.Throws<RLoopException>(() => LiveCaptureService.CameraRotation(new ApplyCameraSpec([float.NaN, 0, 0], [0, 0, 1])));
    }

    [Fact]
    public void ImageValidationRejectsTruncatedPng()
    {
        Assert.Equal((1, 1, "png"), ScreenshotExport.ImageDimensions(Png));
        Assert.Null(ScreenshotExport.ImageDimensions(Png.AsSpan(0, Png.Length - 1)));
        Assert.Null(ScreenshotExport.ImageDimensions("not an image"u8));
    }

    [Fact]
    public async Task WaitsForCompletedNewFileAndPreservesOldPhotos()
    {
        var stale = Path.Combine(_root, "old.png");
        await File.WriteAllBytesAsync(stale, Png);
        using var export = new ScreenshotExport(_root);
        export.Arm();
        var output = Path.Combine(_root, "result", "capture.png");
        var waiting = export.WaitAndCopyAsync(output, 1, 1, TimeSpan.FromSeconds(5));
        var fresh = Path.Combine(_root, "new.png");
        await File.WriteAllBytesAsync(fresh, Png[..30]);
        await Task.Delay(1000);
        Assert.False(waiting.IsCompleted);
        await File.WriteAllBytesAsync(fresh, Png);
        await waiting;
        Assert.Equal(Png, await File.ReadAllBytesAsync(output));
        Assert.Equal(Png, await File.ReadAllBytesAsync(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task RejectsAmbiguousExports()
    {
        using var export = new ScreenshotExport(_root);
        export.Arm();
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.png"), Png);
        await File.WriteAllBytesAsync(Path.Combine(_root, "b.png"), Png);
        var error = await Assert.ThrowsAsync<RLoopException>(() => export.WaitAndCopyAsync(Path.Combine(_root, "out.png"), 1, 1, TimeSpan.FromSeconds(2)));
        Assert.Equal("CAPTURE_AMBIGUOUS", error.Code);
    }

    [Theory]
    [InlineData("out.jpg", 1, "CAPTURE_FORMAT_MISMATCH")]
    [InlineData("out.png", 2, "CAPTURE_RESOLUTION_MISMATCH")]
    public async Task RejectsWrongFormatAndSize(string output, int width, string code)
    {
        using var export = new ScreenshotExport(_root);
        export.Arm();
        await File.WriteAllBytesAsync(Path.Combine(_root, "new.png"), Png);
        var error = await Assert.ThrowsAsync<RLoopException>(() => export.WaitAndCopyAsync(Path.Combine(_root, output), width, 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(code, error.Code);
        Assert.False(File.Exists(Path.Combine(_root, output)));
    }

    [Fact]
    public async Task TimesOutInsteadOfReturningOldImageAndHonorsCancellation()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "old.png"), Png);
        using var export = new ScreenshotExport(_root);
        export.Arm();
        var error = await Assert.ThrowsAsync<RLoopException>(() => export.WaitAndCopyAsync(Path.Combine(_root, "out.png"), 1, 1, TimeSpan.FromMilliseconds(100)));
        Assert.Equal("CAPTURE_EXPORT_TIMEOUT", error.Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAndCopyAsync(Path.Combine(_root, "out.png"), 1, 1, TimeSpan.FromSeconds(3), cancelled.Token));
    }

    [Fact]
    public void RejectsConcurrentCaptureAndReleasesLease()
    {
        using (var export = new ScreenshotExport(_root))
        {
            Assert.Equal("CAPTURE_BUSY", Assert.Throws<RLoopException>(() => new ScreenshotExport(_root)).Code);
            Assert.Equal("CAPTURE_BUSY", Assert.Throws<RLoopException>(() => new ScreenshotExport(_root + Path.DirectorySeparatorChar)).Code);
        }
        using var next = new ScreenshotExport(_root);
    }

    [Fact]
    public void ScreenshotDirectoryMatchesResoniteKnownFolderConvention()
    {
        var pictures = Path.Combine(_root, "Pictures");
        Directory.CreateDirectory(pictures);

        var result = ScreenshotDirectoryResolver.ResolveDefault(pictures, _root, _ => null);

        Assert.Equal(Path.Combine(pictures, "Resonite"), result);
    }

    [Fact]
    public void ScreenshotDirectoryFindsActiveLocalizedOneDrivePicturesFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pictures = Path.Combine(_root, "Pictures");
        var oneDrive = Path.Combine(_root, "OneDrive");
        var active = Path.Combine(oneDrive, "画像", "Resonite");
        Directory.CreateDirectory(pictures);
        Directory.CreateDirectory(active);
        File.WriteAllBytes(Path.Combine(active, "2026-09-05 10.13.34.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
        string? Env(string key) => key is "OneDriveConsumer" or "OneDrive" ? oneDrive : null;

        var result = ScreenshotDirectoryResolver.ResolveDefault(pictures, _root, Env);

        Assert.Equal(active, result);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
