namespace RLoop.Core;

/// <summary>Finds the local folder used by Resonite's generic OS screenshot exporter.</summary>
public static class ScreenshotDirectoryResolver
{
    private const string PlatformDirectory = "Resonite";
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    public static string ResolveDefault(
        string? picturesDirectory = null,
        string? userProfile = null,
        Func<string, string?>? getEnvironment = null)
    {
        getEnvironment ??= Environment.GetEnvironmentVariable;
        picturesDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var primary = Path.GetFullPath(Path.Combine(picturesDirectory, PlatformDirectory));
        var candidates = new HashSet<string>(PathComparer) { primary };

        if (OperatingSystem.IsWindows())
        {
            AddOneDriveCandidates(candidates, getEnvironment("OneDriveConsumer"));
            AddOneDriveCandidates(candidates, getEnvironment("OneDrive"));

            if (Directory.Exists(userProfile))
            {
                try
                {
                    foreach (var root in Directory.EnumerateDirectories(userProfile, "OneDrive*", SearchOption.TopDirectoryOnly))
                        AddOneDriveCandidates(candidates, root);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }

        // Resonite creates the primary path on first export. Prefer an existing candidate
        // with the newest exported image so stale known-folder redirection does not win.
        var active = candidates
            .Select(path => (Path: path, Latest: LatestImageWriteTime(path)))
            .Where(candidate => candidate.Latest is not null)
            .OrderByDescending(candidate => candidate.Latest)
            .FirstOrDefault();
        return active.Path ?? primary;
    }

    private static void AddOneDriveCandidates(HashSet<string> candidates, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        root = Path.GetFullPath(root);
        candidates.Add(Path.Combine(root, PlatformDirectory));
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                candidates.Add(Path.Combine(directory, PlatformDirectory));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static DateTime? LatestImageWriteTime(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            DateTime? latest = null;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                var writeTime = File.GetLastWriteTimeUtc(path);
                if (latest is null || writeTime > latest) latest = writeTime;
            }
            return latest;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
