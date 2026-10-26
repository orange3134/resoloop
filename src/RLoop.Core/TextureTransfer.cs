namespace RLoop.Core;

/// <summary>How texture assets reach Resonite: a path it opens itself, or pixels decoded by this CLI.</summary>
public enum TextureTransfer
{
    File,
    Data
}

/// <summary>RGBA8 pixels in the order ImportTexture2DRawData reads them: rows from the top, x + y * width.</summary>
public sealed record TexturePixels(int Width, int Height, byte[] Rgba);

public static class TextureDecoder
{
    // colorProfile sent with ImportTexture2DRawData. On Resonite 2026.9.18.82 / ResoniteLink 0.13.1, omitting it, sRGB
    // and Linear all returned the same asset URL, which rendered like ImportTexture2DFile of the same image
    // (docs/TEXTURE-TRANSFER.md). Declare sRGB, the profile image files carry, rather than rely on the server default.
    public const string? RawColorProfile = "sRGB";

    // Signatures stb_image accepts for Radiance HDR files.
    private static readonly byte[][] HdrSignatures = ["#?RADIANCE\n"u8.ToArray(), "#?RGBE\n"u8.ToArray()];

    /// <summary>Reads only the header. Null when the image can be sent as 8-bit pixels.</summary>
    public static string? UnsupportedReason(string path)
    {
        using var stream = File.OpenRead(path);
        if (IsHdr(stream)) return "hdr";
        stream.Position = 0;
        return StbImageSharp.ImageInfo.FromStream(stream) is null ? "unknown-format" : null;
    }

    /// <summary>Header check that throws ASSET_TEXTURE_UNDECODABLE before any world change.</summary>
    public static void EnsureDecodable(string path)
    {
        if (UnsupportedReason(path) is { } reason) throw Undecodable(path, reason, null);
    }

    public static TexturePixels Decode(string path)
    {
        EnsureDecodable(path);
        try
        {
            using var stream = File.OpenRead(path);
            var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            return new TexturePixels(image.Width, image.Height, image.Data);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            throw Undecodable(path, "decode-failed", ex);
        }
    }

    private static bool IsHdr(Stream stream)
    {
        var header = new byte[HdrSignatures.Max(signature => signature.Length)];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return HdrSignatures.Any(signature => read >= signature.Length && header.AsSpan(0, signature.Length).SequenceEqual(signature));
    }

    private static RLoopException Undecodable(string path, string reason, Exception? inner) => new("ASSET_TEXTURE_UNDECODABLE",
        reason switch
        {
            "hdr" => $"'{path}' is an HDR image; textureTransfer data sends 8-bit pixels only.",
            "unknown-format" => $"'{path}' is not a PNG, JPEG, BMP, TGA, GIF or PSD image this CLI can decode.",
            _ => $"'{path}' could not be decoded: {inner?.Message}"
        },
        ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["source"] = path, ["reason"] = reason },
        ["Convert the image to an 8-bit PNG, JPEG, BMP, TGA, GIF or PSD file.",
         "Or use --texture-transfer file when Resonite can open this CLI's files (same computer, or a path visible to both)."], inner);
}
