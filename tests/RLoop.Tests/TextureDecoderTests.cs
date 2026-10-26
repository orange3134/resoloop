using System.Text;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class TextureDecoderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-texture-" + Guid.NewGuid().ToString("N"));

    public TextureDecoderTests() => Directory.CreateDirectory(_root);

    // ResoniteLink reads raw texture data as x + y * width with the top row first. A flipped or
    // reordered buffer still imports, so a wrong order would only show up as a mirrored texture.
    [Fact]
    public void DecodesRgbaTopRowFirst()
    {
        byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 10, 20, 30, 40];
        var path = Write("rgba.png", TestPng.Encode(2, 2, TestPng.Rgba, 8, pixels));

        var decoded = TextureDecoder.Decode(path);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(pixels, decoded.Rgba);
        Assert.Null(TextureDecoder.UnsupportedReason(path));
    }

    public static TheoryData<string, byte[], byte[]> Variants() => new()
    {
        { "gray", TestPng.Encode(2, 1, TestPng.Gray, 8, [0, 128]), [0, 0, 0, 255, 128, 128, 128, 255] },
        { "palette", TestPng.Encode(2, 1, TestPng.Palette, 8, [1, 0], [1, 2, 3, 4, 5, 6]), [4, 5, 6, 255, 1, 2, 3, 255] },
        // 16-bit samples keep their high byte.
        { "rgb16", TestPng.Encode(1, 1, TestPng.Rgb, 16, [0xFF, 0xFF, 0x80, 0x00, 0x00, 0x00]), [255, 128, 0, 255] }
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void ExpandsPngVariantsToRgba8(string name, byte[] png, byte[] expected)
    {
        var path = Write(name + ".png", png);

        Assert.Equal(expected, TextureDecoder.Decode(path).Rgba);
    }

    // HDR headers decode without error as tone-mapped 8-bit pixels, so only this check keeps them from being sent silently.
    [Fact]
    public void RejectsHdrImages()
    {
        var hdr = Write("image.hdr", Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n").Concat(new byte[] { 128, 64, 32, 129 }).ToArray());

        Assert.Equal("hdr", TextureDecoder.UnsupportedReason(hdr));
        Assert.Equal("hdr", Assert.Throws<RLoopException>(() => TextureDecoder.Decode(hdr)).Context["reason"]);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
