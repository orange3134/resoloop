using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace RLoop.Core;

/// <summary>Captures through a disposable InteractiveCamera and the local screenshot export folder.</summary>
public sealed class LiveCaptureService(IResoniteClient client)
{
    private const string CameraType = "[FrooxEngine]FrooxEngine.InteractiveCamera";

    public async Task<CaptureArtifact> CaptureAsync(ApplyDocument document, string cameraName, string output,
        string screenshotDirectory, int? width = null, int? height = null, int waitSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (document.Cameras is null || !document.Cameras.TryGetValue(cameraName, out var camera))
            throw new RLoopException("CAPTURE_CAMERA_NOT_FOUND", $"Camera bookmark '{cameraName}' is not declared.", ExitCodes.ValidationFailed);
        var rotation = CameraRotation(camera);
        var w = width ?? camera.Width;
        var h = height ?? camera.Height;
        if (w is < 64 or > 8192 || h is < 64 or > 8192)
            throw new RLoopException("CAPTURE_RESOLUTION_INVALID", "Capture dimensions must be between 64 and 8192.", ExitCodes.ValidationFailed);
        if (waitSeconds is < 1 or > 600)
            throw new RLoopException("CAPTURE_TIMEOUT_INVALID", "Capture wait must be between 1 and 600 seconds.", ExitCodes.ValidationFailed);
        var fullOutput = Path.GetFullPath(output);
        var extension = Path.GetExtension(fullOutput).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            throw new RLoopException("CAPTURE_FORMAT_UNSUPPORTED", "Live capture requires .png or .jpg output; use .svg for offline projection.", ExitCodes.ValidationFailed);
        var directory = Path.GetFullPath(screenshotDirectory);
        var summary = await SceneArtifactService.SummarizeAsync(document, cancellationToken);
        using var export = new ScreenshotExport(directory);

        var definition = await client.DescribeComponentTypeAsync(CameraType, cancellationToken);
        if (definition.Methods?.Any(m => m.Name == "Capture" && !m.IsStatic && m.Parameters.Count == 0) != true)
            throw new RLoopException("CAPTURE_METHOD_UNAVAILABLE", "Runtime Reflection does not expose InteractiveCamera.Capture().", ExitCodes.OperationFailed);
        var name = "ResoLoop_Test_Capture_" + Guid.NewGuid().ToString("N");
        string? slotId = null;
        CaptureOwnership? ownership = null;
        Exception? failure = null;
        try
        {
            slotId = await client.CreateSlotAsync(new SlotCreateRequest("Root", name,
                new Vector3Value(camera.Position[0], camera.Position[1], camera.Position[2]), rotation), cancellationToken);
            var added = await client.AddComponentAsync(slotId, CameraType, new Dictionary<string, string>
            {
                ["CameraMode"] = "Camera2D", ["PositioningMode"] = "Manual",
                ["RenderWidth"] = w.ToString(CultureInfo.InvariantCulture),
                ["PreviewWidth"] = w.ToString(CultureInfo.InvariantCulture),
                ["PreviewHeight"] = h.ToString(CultureInfo.InvariantCulture),
                ["Format"] = extension == ".png" ? "PNG" : "JPG",
                ["SpawnPhotoInWorld"] = "false", ["TimerEnabled"] = "false"
            }, cancellationToken);
            var interactive = await client.GetComponentAsync(added.Id, cancellationToken);
            var mainId = interactive.Members.GetValueOrDefault("MainCamera")?.TargetId;
            if (string.IsNullOrEmpty(mainId))
                throw new RLoopException("CAPTURE_CAMERA_INCOMPLETE", "InteractiveCamera did not create its MainCamera reference.", ExitCodes.OperationFailed);
            var owned = await client.GetSlotAsync(slotId, 2, false, cancellationToken);
            ownership = new CaptureOwnership(owned.ParentId ?? "Root", slotId, name);
            static bool Contains(SlotInfo slot, string id) => slot.Components.Any(c => c.Id == id) || slot.Children.Any(s => Contains(s, id));
            if (!Contains(owned, mainId))
                throw new RLoopException("CAPTURE_CAMERA_INCOMPLETE", "MainCamera is outside the dedicated capture slot; refusing to modify it.", ExitCodes.OperationFailed);
            var main = await client.GetComponentAsync(mainId, cancellationToken);
            if (main.Type != "[FrooxEngine]FrooxEngine.Camera")
                throw new RLoopException("CAPTURE_CAMERA_INCOMPLETE", "MainCamera is not a Camera component.", ExitCodes.OperationFailed);
            await client.SetComponentMemberAsync(mainId, "FieldOfView", camera.FieldOfView.ToString(CultureInfo.InvariantCulture), cancellationToken);
            // Let the newly attached camera finish its first update before requesting a render.
            await Task.Delay(250, cancellationToken);
            export.Arm();
            var call = await client.CallComponentMethodAsync(added.Id, "Capture", cancellationToken: cancellationToken);
            if (!call.Success)
                throw new RLoopException("CAPTURE_TRIGGER_FAILED", call.Error ?? "InteractiveCamera.Capture failed.", ExitCodes.OperationFailed);
            await export.WaitAndCopyAsync(fullOutput, w, h, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            var summaryOutput = Path.ChangeExtension(fullOutput, ".scene.json");
            await File.WriteAllTextAsync(summaryOutput, JsonSerializer.Serialize(summary,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + "\n", cancellationToken);
            return new CaptureArtifact(document.SourcePath ?? "", cameraName, w, h, fullOutput, summaryOutput,
                extension == ".png" ? "png" : "jpeg", true,
                "InteractiveCamera.Capture via ResoniteLink; local screenshot export. Scene summary describes the manifest, not the live world.", summary, ownership);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            if (slotId is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var slot = await client.GetSlotAsync(slotId, 0, false, cleanup.Token);
                    if (slot.Id != slotId || slot.Name != name || slotId == "Root")
                        throw new RLoopException("CAPTURE_CLEANUP_UNSAFE", "Capture slot identity changed; refusing cleanup.", ExitCodes.OperationFailed);
                    await client.DeleteSlotAsync(slotId, cleanup.Token);
                    if (ownership is not null) ownership.CleanupCompleted = true;
                }
                catch (Exception cleanupError)
                {
                    throw new RLoopException("CAPTURE_CLEANUP_FAILED", "Could not clean the dedicated capture slot. Re-inspect the reported path before manually removing it.",
                        ExitCodes.OperationFailed, new Dictionary<string, object?>
                        {
                            ["slotId"] = slotId, ["slotPath"] = "Root/" + name,
                            ["captureError"] = failure?.Message, ["cleanupError"] = cleanupError.Message,
                            ["output"] = fullOutput
                        }, innerException: cleanupError);
                }
            }
        }
    }

    public static QuaternionValue CameraRotation(ApplyCameraSpec camera)
    {
        if (camera.Position is not { Length: 3 } || camera.Target is not { Length: 3 } ||
            camera.Position.Concat(camera.Target).Any(v => !float.IsFinite(v)) ||
            !float.IsFinite(camera.FieldOfView) || camera.FieldOfView is < 5 or > 170)
            throw new RLoopException("CAPTURE_CAMERA_INVALID", "Camera requires finite position/target triples and a fieldOfView between 5 and 170.", ExitCodes.ValidationFailed);
        var forward = Vector3.Normalize(new Vector3(camera.Target[0] - camera.Position[0],
            camera.Target[1] - camera.Position[1], camera.Target[2] - camera.Position[2]));
        if (!float.IsFinite(forward.X))
            throw new RLoopException("CAPTURE_CAMERA_INVALID", "Camera position and target cannot be identical.", ExitCodes.ValidationFailed);
        var up = Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) > .99f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(up, forward));
        up = Vector3.Cross(forward, right);
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1)));
        return new QuaternionValue(q.X, q.Y, q.Z, q.W);
    }
}
