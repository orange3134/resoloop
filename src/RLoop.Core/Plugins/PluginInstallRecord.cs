using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

public sealed record PluginSource(string Kind, string Location, string? Ref);
public sealed record PluginInstallRecord(int SchemaVersion, string Name, string Version, PluginSource Source,
    string? Commit, DateTimeOffset InstalledAt, string ResoloopVersion, IReadOnlyDictionary<string, string> Files)
{
    private const string Invalid = "PLUGIN_RECORD_INVALID";
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly Regex HashPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new(@"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static PluginInstallRecord Read(string path)
    {
        try
        {
            PluginPathPolicy.EnsureNoLinks(path, Invalid);
            using var document = PluginJson.Read(path, MaximumBytes, Invalid);
            var errors = new List<string>();
            PluginJson.FindDuplicateProperties(document.RootElement, "$", errors);
            if (errors.Count != 0)
                throw PluginErrors.Create(Invalid, "Duplicate install record properties.",
                    new Dictionary<string, object?> { ["errors"] = errors.AsReadOnly() });
            var record = document.RootElement.Deserialize<PluginInstallRecord>(JsonOptions)
                ?? throw InvalidRecord("Expected an install record object.");
            return record.ValidatedSnapshot();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw PluginErrors.Create(Invalid, "Could not read plugin install record.", innerException: ex);
        }
    }

    public void Write(string path)
    {
        try
        {
            // Validate and serialize completely before opening/truncating a destination.
            var snapshot = ValidatedSnapshot();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (bytes.Length > MaximumBytes) throw InvalidRecord("Install record exceeds the size limit.");
            PluginPathPolicy.EnsureNoLinks(path, Invalid, allowMissing: true);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw PluginErrors.Create(Invalid, "Could not write plugin install record.", innerException: ex);
        }
    }

    private PluginInstallRecord ValidatedSnapshot()
    {
        if (SchemaVersion != 1 || !PluginNames.IsValid(Name)) throw InvalidRecord("Invalid record schema or plugin name.");
        try { SemanticVersion.Parse(Version); SemanticVersion.Parse(ResoloopVersion); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw PluginErrors.Create(Invalid, "Invalid install record version.", innerException: ex);
        }
        if (InstalledAt.Offset != TimeSpan.Zero) throw InvalidRecord("Install time must be UTC.");
        ValidateSource();
        if (Files is null || Files.Count is < 1 or > 10000) throw InvalidRecord("Invalid recorded file count.");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // Include implicit directories: Assets/a and assets/b also alias on Windows.
        var paths = new Dictionary<string, (string Spelling, bool File)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in Files)
        {
            PluginPathPolicy.ValidateRelativePath(relative, Invalid);
            if (hash is null || !HashPattern.IsMatch(hash)) throw InvalidRecord("Invalid SHA-256 digest.");
            var parts = relative.Split('/');
            var prefix = "";
            for (var i = 0; i < parts.Length; i++)
            {
                prefix = i == 0 ? parts[i] : prefix + "/" + parts[i];
                var isFile = i == parts.Length - 1;
                if (paths.TryGetValue(prefix, out var previous))
                {
                    if (previous.Spelling != prefix || previous.File || isFile)
                        throw InvalidRecord("Recorded paths have a case or file/directory collision.");
                }
                else paths.Add(prefix, (prefix, isFile));
            }
            files.Add(relative, hash);
        }
        if (!files.ContainsKey("resoloop-plugin.json")) throw InvalidRecord("The manifest must be recorded.");
        return this with { Files = new ReadOnlyDictionary<string, string>(files) };
    }

    private void ValidateSource()
    {
        if (Source is null || string.IsNullOrWhiteSpace(Source.Location) || Source.Location.Any(char.IsControl))
            throw InvalidRecord("Invalid recorded source.");
        if (Source.Kind == "directory")
        {
            // A source may no longer exist; never read it while validating an installation.
            if (!Path.IsPathFullyQualified(Source.Location) || Source.Ref is not null || Commit is not null)
                throw InvalidRecord("Invalid directory source metadata.");
            return;
        }
        if (Source.Kind != "git" || Commit is null || !CommitPattern.IsMatch(Commit)
            || string.IsNullOrWhiteSpace(Source.Ref) || Source.Ref.StartsWith('-')
            || Source.Ref.Any(char.IsControl) || Source.Ref.Contains("..", StringComparison.Ordinal))
            throw InvalidRecord("Invalid Git source metadata.");
        // Saved locations may not contain authentication/userinfo, query strings or fragments.
        var location = Source.Location;
        if (location.Contains('?') || location.Contains('#'))
            throw InvalidRecord("Git source location is not sanitized.");
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git" or "file")
        {
            if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw InvalidRecord("Git source location is not sanitized.");
            return;
        }
        // Sanitized scp-style host:path (the public SSH login was removed).
        var colon = location.IndexOf(':');
        if (colon < 1 || colon == location.Length - 1 || location[..colon].Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')
            || location.Contains("://", StringComparison.Ordinal)) throw InvalidRecord("Invalid Git source location.");
    }

    private static RLoopException InvalidRecord(string message) => PluginErrors.Create(Invalid, message);
}
