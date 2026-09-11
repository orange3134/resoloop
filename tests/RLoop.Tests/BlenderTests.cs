using RLoop.Core;
using RLoop.Cli;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

public sealed class BlenderTests
{
    [Fact]
    public void ExplicitDiscoveryDoesNotSilentlyFallBack()
    {
        var configured = Path.GetFullPath("missing-blender.exe");
        var ex = Assert.Throws<RLoopException>(() => BlenderDiscovery.Resolve(configured,
            [new("installed.exe", "PATH")], path => path != configured));
        Assert.Equal("BLENDER_PATH_INVALID", ex.Code);
        Assert.Equal("configuration", BlenderDiscovery.Resolve(configured, [], _ => true).Source);
    }

    [Fact]
    public void DiscoveryUsesFirstExistingCandidateAndExplainsAbsence()
    {
        var found = BlenderDiscovery.Resolve(null, [new("missing", "PATH"), new("installed", "standard-install")], p => p == "installed");
        Assert.Equal("standard-install", found.Source);
        var ex = Assert.Throws<RLoopException>(() => BlenderDiscovery.Resolve(null, [], _ => false));
        Assert.Equal("BLENDER_NOT_FOUND", ex.Code);
        Assert.Contains(ex.Suggestions, s => s.Contains("permission"));
    }

    [Fact]
    public void ScriptArgumentsPreserveSpacesAndSeparatePythonArguments()
    {
        var parsed = ParsedArguments.Parse(["blender", "run", "model script.py", "--arg=--output", "--arg", "a path/日本語.blend", "--json"]);
        var args = BlenderProcess.ScriptArguments(parsed.Positional(2, "script"), "source file.blend", parsed.Options("arg"));
        Assert.Equal("--background", args[0]);
        Assert.True(args.ToList().IndexOf("--python-exit-code") < args.ToList().IndexOf("--python"));
        Assert.Equal(["--", "--output", "a path/日本語.blend"], args.TakeLast(3));
        Assert.Contains(Path.GetFullPath("source file.blend"), args);
    }

    [Fact]
    public void MeshJsonUsesOfficialUvAndSubmeshPolymorphism()
    {
        var mesh = MeshImportDocument.Parse(ValidMesh);
        Assert.IsType<global::ResoniteLink.UV2D_Coordinate>(mesh.Vertices[0].UVs[0]);
        Assert.IsType<global::ResoniteLink.TriangleSubmeshFlat>(mesh.Submeshes[0]);
        Assert.Equal(-1, mesh.Vertices[0].Tangent!.Value.w);
    }

    [Theory]
    [InlineData("\"vertexIndices\":[0,1,2]", "\"vertexIndices\":[0,1,3]")]
    [InlineData("\"vertexIndices\":[0,1,2]", "\"vertexIndices\":[0,1]")]
    [InlineData("\"$type\":\"2D\"", "\"$type\":\"unknown\"")]
    [InlineData("\"position\"", "\"pos\"")]
    public void MalformedMeshesFailBeforeImport(string oldValue, string newValue)
    {
        Assert.Equal("MESH_JSON_INVALID", Assert.Throws<RLoopException>(() =>
            MeshImportDocument.Parse(ValidMesh.Replace(oldValue, newValue))).Code);
    }

    [Fact]
    public void MixedUvChannelsFailBeforeImport()
    {
        var invalid = ValidMesh.Replace("\"uvs\":[{\"$type\":\"2D\",\"uv\":{\"x\":1,\"y\":0}}]", "\"uvs\":[]");
        Assert.Equal("MESH_JSON_INVALID", Assert.Throws<RLoopException>(() => MeshImportDocument.Parse(invalid)).Code);
    }

    [Fact]
    public void BinaryConversionPreservesVertexAttributesUvsAndSubmeshOrder()
    {
        var mesh = MeshImportDocument.Parse(ValidMesh);
        foreach (var vertex in mesh.Vertices)
        {
            vertex.Normal = new global::ResoniteLink.float3 { z = -1 };
            vertex.Tangent = new global::ResoniteLink.float4 { x = 1, w = -1 };
            vertex.Color = new global::ResoniteLink.color { r = 0.3f, a = 1 };
            vertex.UVs.Add(new global::ResoniteLink.UV4D_Coordinate { uv = new global::ResoniteLink.float4 { x = 2, y = 3, z = 4, w = 5 } });
        }
        mesh.Submeshes.Add(new global::ResoniteLink.PointSubmesh { VertexIndices = [2] });
        var raw = Assert.IsType<global::ResoniteLink.ImportMeshRawData>(MeshImportDocument.ToRawStatic(mesh));
        Assert.Equal([2, 4], raw.UV_Channel_Dimensions);
        Assert.Equal(1, raw.Positions[1].x);
        Assert.Equal(-1, raw.Normals[2].z);
        Assert.Equal(-1, raw.Tangents[2].w);
        Assert.Equal(0.3f, raw.Colors[0].r);
        Assert.Equal(1, raw.AccessUV_2D(0)[1].x);
        Assert.Equal(5, raw.AccessUV_4D(1)[0].w);
        Assert.Equal([0, 1, 2], raw.Submeshes[0].Indices.ToArray());
        Assert.Equal([2], raw.Submeshes[1].Indices.ToArray());
        Assert.NotEmpty(raw.RawBinaryPayload);
    }

    [Fact]
    public void BinaryConversionDoesNotDiscardNonuniformOrAnimatedData()
    {
        var mesh = MeshImportDocument.Parse(ValidMesh);
        Assert.Null(MeshImportDocument.ToRawStatic(mesh)); // optional normal/tangent data is nonuniform
        foreach (var v in mesh.Vertices) { v.Normal = null; v.Tangent = null; }
        Assert.NotNull(MeshImportDocument.ToRawStatic(mesh));
        mesh.Bones = [new global::ResoniteLink.Bone()];
        Assert.Null(MeshImportDocument.ToRawStatic(mesh));
    }

    [Fact]
    [Trait("Category", "Blender")]
    public async Task BackgroundProcessReportsPythonFailureAndCancels()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_BLENDER_TESTS") != "1") return;
        var blender = BlenderDiscovery.Resolve(Environment.GetEnvironmentVariable("RESOLOOP_BLENDER_EXECUTABLE"));
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "failure with spaces.py");
            await File.WriteAllTextAsync(script, "raise RuntimeError('intentional test failure')\n");
            var failure = await Assert.ThrowsAsync<RLoopException>(() => BlenderProcess.RunAsync(blender.Executable, BlenderProcess.ScriptArguments(script)));
            Assert.Equal("BLENDER_FAILED", failure.Code);
            await File.WriteAllTextAsync(script, "import time\ntime.sleep(60)\n");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BlenderProcess.RunAsync(blender.Executable, BlenderProcess.ScriptArguments(script), cancellation.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    private const string ValidMesh = """
        {"vertices":[
          {"position":{"x":0,"y":0,"z":0},"normal":{"x":0,"y":0,"z":-1},"tangent":{"x":1,"y":0,"z":0,"w":-1},"uvs":[{"$type":"2D","uv":{"x":0,"y":0}}]},
          {"position":{"x":1,"y":0,"z":0},"uvs":[{"$type":"2D","uv":{"x":1,"y":0}}]},
          {"position":{"x":0,"y":1,"z":0},"uvs":[{"$type":"2D","uv":{"x":0,"y":1}}]}],
         "submeshes":[{"$type":"trianglesFlat","vertexIndices":[0,1,2]}]}
        """;
}
