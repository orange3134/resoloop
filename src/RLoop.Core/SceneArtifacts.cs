using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;
using System.Text.Json;

namespace RLoop.Core;

public sealed record SceneBounds(float[] Min, float[] Max, float[] Size, float[] Center);
public sealed record ScenePlacement(string Key, string Path, float[] WorldPosition, float[] WorldScale, float[]? GeometrySize = null);
public sealed record SceneIssue(string Code, string Severity, string Path, string Message);
public sealed record SceneSummary(string Source, SceneBounds Bounds, IReadOnlyList<ScenePlacement> Placements,
    IReadOnlyList<SceneIssue> Issues, int Slots, int Components, bool Valid);
public sealed record CaptureArtifact(string Source, string Camera, int Width, int Height, string Output,
    string SummaryOutput, string Format, bool ScreenshotAvailable, string Capability, SceneSummary Summary);

/// <summary>
/// Produces deterministic declaration-space artifacts. For live screenshots use LiveCaptureService.
/// </summary>
public static class SceneArtifactService
{
    public static async Task<SceneSummary> SummarizeAsync(ApplyDocument document, CancellationToken cancellationToken = default)
    {
        var validation = await ApplyDocumentValidator.ValidateAsync(document, cancellationToken: cancellationToken);
        var placements = new List<ScenePlacement>();
        var issues = validation.Issues.Select(x => new SceneIssue(x.Code, x.Severity, x.Path, x.Message)).ToList();
        var components = 0;
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var hasGeometry = false;

        void Visit(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? nodeComponents,
            IReadOnlyList<ApplyNodeSpec>? children, string path, Matrix4x4 parentTransform)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPosition = Vector(slot.Position, Vector3.Zero);
            var localScale = Vector(slot.Scale, Vector3.One);
            var localRotation = slot.Rotation is { Length: 4 } rotation
                ? Quaternion.Normalize(new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3])) : Quaternion.Identity;
            var worldTransform = Matrix4x4.CreateScale(localScale) * Matrix4x4.CreateFromQuaternion(localRotation) *
                                 Matrix4x4.CreateTranslation(localPosition) * parentTransform;
            var worldPosition = Vector3.Transform(Vector3.Zero, worldTransform);
            var worldScale = new Vector3(
                Vector3.TransformNormal(Vector3.UnitX, worldTransform).Length(),
                Vector3.TransformNormal(Vector3.UnitY, worldTransform).Length(),
                Vector3.TransformNormal(Vector3.UnitZ, worldTransform).Length());
            var geometrySize = GeometrySize(nodeComponents);
            placements.Add(new ScenePlacement(slot.Key ?? "$path:" + path, path,
                [worldPosition.X, worldPosition.Y, worldPosition.Z], [worldScale.X, worldScale.Y, worldScale.Z],
                geometrySize is null ? null : Array(geometrySize.Value)));
            components += nodeComponents?.Count ?? 0;

            if (geometrySize is { } size)
            {
                hasGeometry = true;
                var half = size / 2;
                foreach (var x in new[] { -half.X, half.X })
                foreach (var y in new[] { -half.Y, half.Y })
                foreach (var z in new[] { -half.Z, half.Z })
                {
                    var corner = Vector3.Transform(new Vector3(x, y, z), worldTransform);
                    min = Vector3.Min(min, corner);
                    max = Vector3.Max(max, corner);
                }
            }

            foreach (var (component, index) in (nodeComponents ?? []).Select((value, index) => (value, index)))
            {
                if (!component.Type.Contains("MeshRenderer", StringComparison.OrdinalIgnoreCase)) continue;
                var materialFields = (component.Fields ?? new Dictionary<string, JsonElement>())
                    .Where(x => x.Key.Contains("Material", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (materialFields.Length == 0 || materialFields.All(x => x.Value.ValueKind == JsonValueKind.Null ||
                    x.Value.ValueKind == JsonValueKind.String && string.Equals(x.Value.GetString(), "null", StringComparison.OrdinalIgnoreCase)))
                    issues.Add(new SceneIssue("SCENE_MATERIAL_MISSING", "warning", $"{path}.components[{index}]",
                        $"Renderer '{component.Key ?? component.Type}' has no material reference."));
            }
            foreach (var child in children ?? [])
                Visit(child.Slot, child.Components, child.Children, path + "/" + child.Slot.Name, worldTransform);
        }

        if (document.Slot is not null)
            Visit(document.Slot, document.Components, document.Children, "Root/" + document.Slot.Name, Matrix4x4.Identity);
        if (!hasGeometry)
        {
            min = placements.Count == 0 ? Vector3.Zero : new Vector3(placements.Min(x => x.WorldPosition[0]), placements.Min(x => x.WorldPosition[1]), placements.Min(x => x.WorldPosition[2]));
            max = placements.Count == 0 ? Vector3.Zero : new Vector3(placements.Max(x => x.WorldPosition[0]), placements.Max(x => x.WorldPosition[1]), placements.Max(x => x.WorldPosition[2]));
        }
        var size = max - min;
        var center = (min + max) / 2;
        var bounds = new SceneBounds(Array(min), Array(max), Array(size), Array(center));
        return new SceneSummary(document.SourcePath ?? string.Empty, bounds, placements, issues,
            placements.Count, components, issues.All(x => x.Severity != "error"));
    }

    public static async Task<CaptureArtifact> CaptureAsync(ApplyDocument document, string cameraName, string output,
        int? width = null, int? height = null, CancellationToken cancellationToken = default)
    {
        if (document.Cameras is null || !document.Cameras.TryGetValue(cameraName, out var camera))
            throw new RLoopException("CAPTURE_CAMERA_NOT_FOUND", $"Camera bookmark '{cameraName}' is not declared.", ExitCodes.ValidationFailed,
                suggestions: document.Cameras?.Keys.ToArray() ?? []);
        if (camera.Position.Length != 3 || camera.Target.Length != 3)
            throw new RLoopException("CAPTURE_CAMERA_INVALID", "Camera position and target require three numbers.", ExitCodes.ValidationFailed);
        var actualWidth = width ?? camera.Width;
        var actualHeight = height ?? camera.Height;
        if (actualWidth is < 64 or > 8192 || actualHeight is < 64 or > 8192)
            throw new RLoopException("CAPTURE_RESOLUTION_INVALID", "Capture width and height must be between 64 and 8192.", ExitCodes.ValidationFailed);
        var fullOutput = Path.GetFullPath(output);
        if (!Path.GetExtension(fullOutput).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            throw new RLoopException("CAPTURE_FORMAT_UNSUPPORTED", "Offline projection requires .svg; use live capture for .png/.jpg.", ExitCodes.ValidationFailed);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        var summary = await SummarizeAsync(document, cancellationToken);
        var svg = RenderSvg(summary, camera, actualWidth, actualHeight, cameraName);
        await File.WriteAllTextAsync(fullOutput, svg, new UTF8Encoding(false), cancellationToken);
        var summaryOutput = Path.ChangeExtension(fullOutput, ".scene.json");
        await File.WriteAllTextAsync(summaryOutput, JsonSerializer.Serialize(summary, JsonOptions) + "\n", new UTF8Encoding(false), cancellationToken);
        return new CaptureArtifact(document.SourcePath ?? string.Empty, cameraName, actualWidth, actualHeight,
            fullOutput, summaryOutput, "wireframe-svg", false,
            "ResoniteLink 0.13.1 exposes no screenshot API; this is a deterministic camera projection for CI comparison.", summary);
    }

    private static string RenderSvg(SceneSummary summary, ApplyCameraSpec camera, int width, int height, string cameraName)
    {
        var origin = new Vector3(camera.Position[0], camera.Position[1], camera.Position[2]);
        var target = new Vector3(camera.Target[0], camera.Target[1], camera.Target[2]);
        var forward = Vector3.Normalize(target - origin);
        if (!float.IsFinite(forward.X)) throw new RLoopException("CAPTURE_CAMERA_INVALID", "Camera position and target cannot be identical.", ExitCodes.ValidationFailed);
        var right = Vector3.Normalize(Vector3.Cross(forward, Math.Abs(Vector3.Dot(forward, Vector3.UnitY)) > .99f ? Vector3.UnitZ : Vector3.UnitY));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        var focal = height / (2f * MathF.Tan(Math.Clamp(camera.FieldOfView, 5, 170) * MathF.PI / 360f));
        var projected = new List<(ScenePlacement Placement, float X, float Y, float Depth)>();
        foreach (var placement in summary.Placements)
        {
            var point = new Vector3(placement.WorldPosition[0], placement.WorldPosition[1], placement.WorldPosition[2]) - origin;
            var depth = Vector3.Dot(point, forward);
            if (depth <= .01f) continue;
            projected.Add((placement, width / 2f + Vector3.Dot(point, right) * focal / depth,
                height / 2f - Vector3.Dot(point, up) * focal / depth, depth));
        }
        var b = new StringBuilder();
        b.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        b.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#111827\"/><g stroke=\"#67e8f9\" fill=\"#164e63\" fill-opacity=\".55\">");
        foreach (var point in projected.OrderByDescending(x => x.Depth))
        {
            var radius = Math.Clamp(120f / point.Depth, 2, 18);
            b.AppendLine(FormattableString.Invariant($"<circle cx=\"{point.X:0.###}\" cy=\"{point.Y:0.###}\" r=\"{radius:0.###}\"/>"));
        }
        b.AppendLine("</g><g fill=\"#e5e7eb\" font-family=\"monospace\" font-size=\"11\">");
        foreach (var point in projected.OrderBy(x => x.Depth).Take(200))
            b.AppendLine(FormattableString.Invariant($"<text x=\"{point.X + 5:0.###}\" y=\"{point.Y - 5:0.###}\">{SecurityElement.Escape(point.Placement.Key)}</text>"));
        b.AppendLine($"<text x=\"16\" y=\"24\" font-size=\"14\">resoloop wireframe · {SecurityElement.Escape(cameraName)} · {summary.Slots} slots</text></g></svg>");
        return b.ToString();
    }

    private static Vector3 Vector(float[]? value, Vector3 fallback) => value is { Length: 3 } ? new Vector3(value[0], value[1], value[2]) : fallback;
    private static Vector3? GeometrySize(IReadOnlyList<ApplyComponentSpec>? components)
    {
        foreach (var component in components ?? [])
        {
            var fields = component.Fields ?? new Dictionary<string, JsonElement>();
            if (component.Type.EndsWith("BoxMesh", StringComparison.Ordinal) && fields.TryGetValue("Size", out var size) && TryVector(size, out var box)) return box;
            if (component.Type.EndsWith("SphereMesh", StringComparison.Ordinal) && fields.TryGetValue("Radius", out var radius) && radius.TryGetSingle(out var r)) return new Vector3(r * 2);
            if ((component.Type.EndsWith("CylinderMesh", StringComparison.Ordinal) || component.Type.EndsWith("ConeMesh", StringComparison.Ordinal)) &&
                fields.TryGetValue("Radius", out radius) && radius.TryGetSingle(out r) && fields.TryGetValue("Height", out var height) && height.TryGetSingle(out var h))
                return new Vector3(r * 2, h, r * 2);
        }
        return null;
    }
    private static bool TryVector(JsonElement value, out Vector3 vector)
    {
        vector = default;
        if (value.ValueKind != JsonValueKind.Array) return false;
        var values = value.EnumerateArray().ToArray();
        if (values.Length != 3 || values.Any(x => !x.TryGetSingle(out _))) return false;
        vector = new Vector3(values[0].GetSingle(), values[1].GetSingle(), values[2].GetSingle());
        return true;
    }
    private static float[] Array(Vector3 value) => [value.X, value.Y, value.Z];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
