using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RLoop.Core;

/// <summary>Serializes captures and rejects stale, incomplete, or ambiguous local exports.</summary>
public sealed class ScreenshotExport : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lease;
    private HashSet<string>? _before;

    public ScreenshotExport(string directory)
    {
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(_directory) && !Directory.Exists(Path.GetDirectoryName(_directory)))
            throw new RLoopException("CAPTURE_DIRECTORY_NOT_FOUND", $"Screenshot directory parent does not exist: {_directory}", ExitCodes.ValidationFailed);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? _directory.ToUpperInvariant() : _directory)));
        try
        {
            _lease = new FileStream(Path.Combine(Path.GetTempPath(), "resoloop-capture-" + key + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new RLoopException("CAPTURE_BUSY", "Another resoloop capture is using this screenshot directory.", ExitCodes.OperationFailed, innerException: ex);
        }
    }

    private HashSet<string> Files() => Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        : new HashSet<string>();

    public void Arm() => _before = Files();

    public async Task WaitAndCopyAsync(string output, int width, int height, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_before is null) throw new InvalidOperationException("Arm before triggering the camera.");
        var timer = Stopwatch.StartNew();
        string? candidate = null;
        long lastLength = -1;
        DateTime lastWrite = default;
        var stableSince = TimeSpan.Zero;
        while (timer.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var fresh = Files().Except(_before, _before.Comparer).ToArray();
            if (fresh.Length > 1)
                throw new RLoopException("CAPTURE_AMBIGUOUS", "Multiple new screenshots appeared. Avoid other captures and retry.", ExitCodes.OperationFailed);
            if (fresh.Length == 1)
            {
                var info = new FileInfo(fresh[0]);
                if (candidate != info.FullName || lastLength != info.Length || lastWrite != info.LastWriteTimeUtc)
                {
                    candidate = info.FullName;
                    lastLength = info.Length;
                    lastWrite = info.LastWriteTimeUtc;
                    stableSince = timer.Elapsed;
                }
                else if (lastLength > 0 && timer.Elapsed - stableSince >= TimeSpan.FromMilliseconds(750))
                {
                    byte[]? bytes = null;
                    try
                    {
                        // Exclusive read also detects a writer that has not closed its handle yet.
                        using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.None);
                        if (stream.Length <= 256 * 1024 * 1024)
                        {
                            bytes = new byte[checked((int)stream.Length)];
                            await stream.ReadExactlyAsync(bytes, ct);
                        }
                    }
                    catch (IOException) { }
                    if (bytes is not null && ImageDimensions(bytes) is { } dimensions)
                    {
                        if (dimensions.Width != width || dimensions.Height != height)
                            throw new RLoopException("CAPTURE_RESOLUTION_MISMATCH", $"Export is {dimensions.Width}x{dimensions.Height}; expected {width}x{height}.", ExitCodes.OperationFailed);
                        var expected = Path.GetExtension(output).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
                        if (dimensions.Format != expected)
                            throw new RLoopException("CAPTURE_FORMAT_MISMATCH", $"Resonite exported {dimensions.Format}. Use the matching output extension or enable Keep Original Screenshot Format in Resonite settings.", ExitCodes.OperationFailed);
                        output = Path.GetFullPath(output);
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            await File.WriteAllBytesAsync(temporary, bytes, ct);
                            File.Move(temporary, output, true);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        return;
                    }
                }
            }
            await Task.Delay(100, ct);
        }
        throw new RLoopException("CAPTURE_EXPORT_TIMEOUT", $"No complete screenshot arrived in '{_directory}' within {timeout.TotalSeconds:0.#} seconds. Check --screenshots-dir, local Resonite export settings and renderer availability.", ExitCodes.Timeout);
    }

    public static (int Width, int Height, string Format)? ImageDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 45 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            var w = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
            var h = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
            var offset = 8;
            var hasData = false;
            while (offset <= data.Length - 12)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
                if (length > data.Length - offset - 12) return null;
                var type = data.Slice(offset + 4, 4);
                if (type.SequenceEqual("IDAT"u8)) hasData = true;
                if (type.SequenceEqual("IEND"u8))
                    return hasData && length == 0 && w > 0 && h > 0 && offset + 12 == data.Length ? (w, h, "png") : null;
                offset += (int)length + 12;
            }
            return null;
        }
        if (data.Length < 4 || data[0] != 255 || data[1] != 216 || data[^2] != 255 || data[^1] != 217) return null;
        for (var offset = 2; offset < data.Length - 4;)
        {
            if (data[offset++] != 255) return null;
            while (offset < data.Length && data[offset] == 255) offset++;
            if (offset >= data.Length - 2) return null;
            var marker = data[offset++];
            if (marker == 218) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (length < 2 || offset + length > data.Length) return null;
            if (marker is 192 or 193 or 194 && length >= 8)
            {
                var h = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                var w = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                return w > 0 && h > 0 ? (w, h, "jpeg") : null;
            }
            offset += length;
        }
        return null;
    }

    public void Dispose() => _lease.Dispose();
}
