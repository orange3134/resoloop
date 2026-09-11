using System.Numerics;
using System.Text.Json;

namespace RLoop.Core;

// Declaration-side local mesh inspection; no ResoniteLink wire models or remote asset downloads.
internal sealed class SceneMeshBounds
{
    private readonly ApplyDocument _document;
    private readonly Dictionary<string, ApplyComponentSpec> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Vector3 Min, Vector3 Max)?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SceneMeshBounds(ApplyDocument document)
    {
        _document = document;
        void Visit(IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children)
        {
            foreach (var component in components ?? [])
                if (component.Key is not null) _components[component.Key] = component;
            foreach (var child in children ?? []) Visit(child.Components, child.Children);
        }
        Visit(document.Components, document.Children);
    }

    public IReadOnlyList<(Vector3 Min, Vector3 Max)> ForRenderers(IReadOnlyList<ApplyComponentSpec>? components,
        bool nativeGeometryKnown, out bool unknown)
    {
        unknown = false;
        var result = new List<(Vector3, Vector3)>();
        foreach (var renderer in components ?? [])
        {
            if (!renderer.Type.EndsWith(".MeshRenderer", StringComparison.Ordinal)) continue;
            var reference = StringField(renderer, "Mesh");
            if (reference?.StartsWith("$component:", StringComparison.Ordinal) != true ||
                !_components.TryGetValue(reference[11..], out var provider)) { unknown = true; continue; }
            if (!provider.Type.EndsWith(".StaticMesh", StringComparison.Ordinal))
            {
                unknown |= !nativeGeometryKnown;
                continue;
            }
            var url = StringField(provider, "URL");
            if (url?.StartsWith("$asset:", StringComparison.Ordinal) != true ||
                _document.Assets?.TryGetValue(url[7..], out var asset) != true) { unknown = true; continue; }
            var source = asset!.Source;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile && !Path.IsPathFullyQualified(source))
            { unknown = true; continue; }
            var path = Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : source,
                Path.GetDirectoryName(_document.SourcePath) ?? Environment.CurrentDirectory);
            if (!_cache.TryGetValue(path, out var bounds)) _cache[path] = bounds = Read(path);
            if (bounds is { } known) result.Add(known); else unknown = true;
        }
        return result;
    }

    private static string? StringField(ApplyComponentSpec component, string name) =>
        component.Fields?.TryGetValue(name, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (Vector3, Vector3)? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("vertices", out var vertices) || vertices.GetArrayLength() == 0) return null;
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            foreach (var vertex in vertices.EnumerateArray())
            {
                var p = vertex.GetProperty("position");
                var point = new Vector3(p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle(), p.GetProperty("z").GetSingle());
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return null;
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
            return (min, max);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return null; }
    }
}
