using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RLoop.Tests;

/// <summary>Builds small PNG files with exact pixel values for decoder tests.</summary>
internal static class TestPng
{
    public const byte Gray = 0, Rgb = 2, Palette = 3, Rgba = 6;

    // pixels holds the unfiltered samples row by row (big-endian for 16-bit); each row gets filter type 0.
    public static byte[] Encode(int width, int height, byte colorType, byte bitDepth, byte[] pixels, byte[]? palette = null)
    {
        var channels = colorType switch { Gray or Palette => 1, Rgb => 3, Rgba => 4, _ => throw new ArgumentOutOfRangeException(nameof(colorType)) };
        var rowBytes = width * channels * bitDepth / 8;
        if (pixels.Length != rowBytes * height) throw new ArgumentException("Pixel data does not match the image size.", nameof(pixels));
        var filtered = new byte[(rowBytes + 1) * height];
        for (var y = 0; y < height; y++) Array.Copy(pixels, y * rowBytes, filtered, y * (rowBytes + 1) + 1, rowBytes);

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        Chunk(output, "IHDR", header);
        if (palette is not null) Chunk(output, "PLTE", palette);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write(filtered);
            Chunk(output, "IDAT", compressed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(typeAndData);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        output.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
