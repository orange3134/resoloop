using System.Text.Json;
using RLoop.Core;

namespace RLoop.ResoniteLink;

// The bundled exporter speaks the pinned ResoniteLink asset schema, so it belongs in the adapter.
public static class BlenderExport
{
    public static async Task<JsonElement> ExportAsync(string executable, string blendFile, string outputDirectory,
        string name, string parent, string? collection = null, CancellationToken cancellationToken = default,
        bool preserveHierarchy = false, bool packPbr = false, bool legacyRootProviders = false)
    {
        blendFile = Path.GetFullPath(blendFile);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (!File.Exists(blendFile)) throw new RLoopException("BLENDER_SOURCE_NOT_FOUND", $"File not found: {blendFile}", ExitCodes.NotFound);
        if (!Path.GetExtension(blendFile).Equals(".blend", StringComparison.OrdinalIgnoreCase))
            throw new RLoopException("BLENDER_SOURCE_INVALID", "blender export requires a .blend file.", ExitCodes.InvalidArguments);
        if (File.Exists(outputDirectory) || Directory.Exists(outputDirectory))
            throw new RLoopException("BLENDER_OUTPUT_EXISTS", "Choose a new output directory; exports never overwrite an existing bundle.", ExitCodes.ValidationFailed);
        var script = Path.Combine(Path.GetTempPath(), "resoloop-export-" + Guid.NewGuid().ToString("N") + ".py");
        try
        {
            using var stream = typeof(BlenderExport).Assembly.GetManifestResourceStream("RLoop.ResoniteLink.blender_export.py")!;
            using var reader = new StreamReader(stream);
            await File.WriteAllTextAsync(script, await reader.ReadToEndAsync(cancellationToken), cancellationToken);
            var args = new List<string> { "--output", outputDirectory, "--name", name, "--parent", parent };
            if (collection is not null) args.AddRange(["--collection", collection]);
            if (preserveHierarchy) args.Add("--preserve-hierarchy");
            if (packPbr) args.Add("--pack-pbr");
            if (legacyRootProviders) args.Add("--legacy-root-providers");
            await BlenderProcess.RunAsync(executable, BlenderProcess.ScriptArguments(script, blendFile, args), cancellationToken);
            // Parse using the official models too: malformed/polymorphic payloads never count as successful exports.
            foreach (var meshPath in Directory.GetFiles(outputDirectory, "*.mesh.json"))
                MeshImportDocument.Parse(await File.ReadAllTextAsync(meshPath, cancellationToken));
            var document = ApplyDocument.Load(Path.Combine(outputDirectory, "model.apply.json"));
            ApplyDocumentValidator.ThrowIfInvalid(await ApplyDocumentValidator.ValidateAsync(document, cancellationToken: cancellationToken));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "report.json"), cancellationToken));
            return report.RootElement.Clone();
        }
        finally { File.Delete(script); }
    }
}

internal static class MeshImportDocument
{
    // Live 0.13.1 rejects otherwise valid JSON UVs ("already configured with 0 dimensions").
    // Static, uniform meshes use the public binary API, which also avoids verbose JSON transport.
    // Preserve the original JSON path for data outside this lossless conversion's scope.
    public static global::ResoniteLink.ImportMeshRawData? ToRawStatic(global::ResoniteLink.ImportMeshJSON mesh)
    {
        if (mesh.Bones is { Count: > 0 } || mesh.BlendShapes is { Count: > 0 } || mesh.Vertices.Any(v => v.BoneWeights is { Count: > 0 })) return null;
        var first = mesh.Vertices[0];
        if (mesh.Vertices.Any(v => v.Normal.HasValue != first.Normal.HasValue || v.Tangent.HasValue != first.Tangent.HasValue || v.Color.HasValue != first.Color.HasValue)) return null;
        var raw = new global::ResoniteLink.ImportMeshRawData
        {
            VertexCount = mesh.Vertices.Count, HasNormals = first.Normal.HasValue,
            HasTangents = first.Tangent.HasValue, HasColors = first.Color.HasValue,
            UV_Channel_Dimensions = (first.UVs ?? []).Select(uv => uv switch
            {
                global::ResoniteLink.UV2D_Coordinate => 2,
                global::ResoniteLink.UV3D_Coordinate => 3,
                global::ResoniteLink.UV4D_Coordinate => 4,
                _ => throw new InvalidOperationException("Unsupported UV type.")
            }).ToList(),
            Submeshes = mesh.Submeshes.Select<global::ResoniteLink.Submesh, global::ResoniteLink.SubmeshRawData>(s => s switch
            {
                global::ResoniteLink.TriangleSubmeshFlat f => new global::ResoniteLink.TriangleSubmeshRawData { TriangleCount = f.VertexIndices.Count / 3 },
                global::ResoniteLink.TriangleSubmesh t => new global::ResoniteLink.TriangleSubmeshRawData { TriangleCount = t.Triangles.Count },
                global::ResoniteLink.PointSubmesh p => new global::ResoniteLink.PointSubmeshRawData { PointCount = p.VertexIndices.Count },
                _ => throw new InvalidOperationException("Unsupported submesh type.")
            }).ToList()
        };
        raw.AllocateBuffer();
        for (var i = 0; i < mesh.Vertices.Count; i++)
        {
            var vertex = mesh.Vertices[i];
            raw.Positions[i] = vertex.Position;
            if (raw.HasNormals) raw.Normals[i] = vertex.Normal!.Value;
            if (raw.HasTangents) raw.Tangents[i] = vertex.Tangent!.Value;
            if (raw.HasColors) raw.Colors[i] = vertex.Color!.Value;
            for (var channel = 0; channel < raw.UV_Channel_Dimensions.Count; channel++)
                switch (vertex.UVs[channel])
                {
                    case global::ResoniteLink.UV2D_Coordinate uv: raw.AccessUV_2D(channel)[i] = uv.uv; break;
                    case global::ResoniteLink.UV3D_Coordinate uv: raw.AccessUV_3D(channel)[i] = uv.uv; break;
                    case global::ResoniteLink.UV4D_Coordinate uv: raw.AccessUV_4D(channel)[i] = uv.uv; break;
                }
        }
        for (var i = 0; i < mesh.Submeshes.Count; i++)
        {
            var indices = mesh.Submeshes[i] switch
            {
                global::ResoniteLink.TriangleSubmeshFlat f => f.VertexIndices.ToArray(),
                global::ResoniteLink.TriangleSubmesh t => t.Triangles.SelectMany(t => new[] { t.Vertex0Index, t.Vertex1Index, t.Vertex2Index }).ToArray(),
                global::ResoniteLink.PointSubmesh p => p.VertexIndices.ToArray(),
                _ => throw new InvalidOperationException("Unsupported submesh type.")
            };
            indices.AsSpan().CopyTo(raw.Submeshes[i].Indices);
        }
        return raw;
    }

    public static global::ResoniteLink.ImportMeshJSON Parse(string json)
    {
        try
        {
            using var raw = JsonDocument.Parse(json);
            var mesh = JsonSerializer.Deserialize<global::ResoniteLink.ImportMeshJSON>(json) ?? throw new JsonException("Empty mesh.");
            if (mesh.Vertices is not { Count: > 0 } || mesh.Submeshes is not { Count: > 0 })
                throw new JsonException("vertices and submeshes must be nonempty arrays.");
            if (raw.RootElement.GetProperty("vertices").EnumerateArray().Any(v => !v.TryGetProperty("position", out _)))
                throw new JsonException("Every vertex requires position.");
            var channels = mesh.Vertices[0].UVs?.Select(x => x.GetType()).ToArray() ?? [];
            foreach (var vertex in mesh.Vertices)
            {
                if (!float.IsFinite(vertex.Position.x) || !float.IsFinite(vertex.Position.y) || !float.IsFinite(vertex.Position.z))
                    throw new JsonException("Positions must be finite.");
                if (!(vertex.UVs?.Select(x => x.GetType()) ?? []).SequenceEqual(channels))
                    throw new JsonException("All vertices must have the same UV channel count and dimensions.");
            }
            foreach (var submesh in mesh.Submeshes)
            {
                IEnumerable<int> indices = submesh switch
                {
                    global::ResoniteLink.TriangleSubmeshFlat flat when flat.VertexIndices is not null && flat.VertexIndices.Count % 3 == 0 => flat.VertexIndices,
                    global::ResoniteLink.TriangleSubmesh triangles when triangles.Triangles is not null => triangles.Triangles.SelectMany(t => new[] { t.Vertex0Index, t.Vertex1Index, t.Vertex2Index }),
                    global::ResoniteLink.PointSubmesh points when points.VertexIndices is not null => points.VertexIndices,
                    _ => throw new JsonException("Invalid submesh indices.")
                };
                if (indices.Any(i => i < 0 || i >= mesh.Vertices.Count)) throw new JsonException("Submesh index outside vertex array.");
            }
            return mesh;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        {
            throw new RLoopException("MESH_JSON_INVALID", $"Invalid ResoniteLink ImportMeshJSON: {ex.Message}", ExitCodes.ValidationFailed, innerException: ex);
        }
    }
}
