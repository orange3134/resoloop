using System.Security.Cryptography;
using System.Text;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    // A texture that cannot be decoded must stop apply before the first import or world change; otherwise
    // the CLI sends the textures before it and then fails halfway through the bundle.
    [Fact]
    public async Task DataTransferStopsOnUndecodableTextureBeforeAnyChange()
    {
        File.WriteAllBytes(Path.Combine(_root, "good.png"), TestPng.Encode(1, 1, TestPng.Rgba, 8, [1, 2, 3, 255]));
        File.WriteAllBytes(Path.Combine(_root, "bad.webp"), Encoding.ASCII.GetBytes("RIFF$\0\0\0WEBPVP8 \u0018\0\0\0").Concat(new byte[24]).ToArray());
        var document = TextureDocument("undecodable",
            """{"good":{"kind":"texture","source":"good.png"},"bad":{"kind":"texture","source":"bad.webp"}}""", "good", "bad");
        var client = new FakeResoniteClient(document);
        var state = Path.Combine(_root, "undecodable.state.json");

        var ex = await Assert.ThrowsAsync<RLoopException>(() =>
            new WorldService(client, null, TextureTransfer.Data).ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("ASSET_TEXTURE_UNDECODABLE", ex.Code);
        Assert.Equal("bad", ex.Context["asset"]);
        Assert.Empty(client.Mutations);
        Assert.Equal(0, client.Writes);
        Assert.Equal(0, client.AssetImports);
        Assert.Empty(client.PixelImports);

        // The same bundle still applies through file transfer, and once imported the asset is no longer checked.
        await new WorldService(client).ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(["texture", "texture"], client.FileImports);
        var plan = await new WorldService(client, null, TextureTransfer.Data).PlanApplyAsync(document, new ApplyOptions(state));
        Assert.All(plan.Operations.Where(operation => operation.Kind == "asset"), asset => Assert.Equal("no-op", asset.Action));
    }

    [Fact]
    public async Task DataTransferSendsTexturePixelsAndKeepsAudioOnFiles()
    {
        var png = TestPng.Encode(2, 1, TestPng.Rgba, 8, [1, 2, 3, 255, 4, 5, 6, 255]);
        File.WriteAllBytes(Path.Combine(_root, "albedo.png"), png);
        File.WriteAllBytes(Path.Combine(_root, "clip.ogg"), [1, 2, 3]);
        var document = TextureDocument("pixels",
            """{"albedo":{"kind":"Texture2D","source":"albedo.png"},"clip":{"kind":"audio","source":"clip.ogg"}}""", "albedo", "clip");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client, null, TextureTransfer.Data);
        var state = Path.Combine(_root, "pixels.state.json");

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));
        Assert.Single(plan.Warnings, warning => warning.Code == "ASSET_AUDIO_FILE_TRANSFER");

        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal([(TextureDecoder.RawColorProfile, 2, 1)], client.PixelImports);
        Assert.Equal(["audio"], client.FileImports);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(png)), StateAsset(state, "albedo")["sourceHash"]!.GetValue<string>());
    }

    private ApplyDocument TextureDocument(string ownership, string assets, params string[] keys)
    {
        var holders = string.Join(",", keys.Select(key =>
            $$$"""{"key":"holder-{{{key}}}","type":"Test.AssetHolder","fields":{"URL":"$asset:{{{key}}}"}}"""));
        var path = Path.Combine(_root, ownership + ".json");
        File.WriteAllText(path, $$"""
            {"schemaVersion":"1","ownership":{"key":"{{ownership}}"},"slot":{"key":"root","name":"Textures","parent":"Root"},
             "assets":{{assets}},"components":[{{holders}}]}
            """);
        return ApplyDocument.Load(path);
    }
}
