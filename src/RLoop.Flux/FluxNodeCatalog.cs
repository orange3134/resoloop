using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RLoop.Flux;

public sealed record FluxNodePort(string Name, string Type);

public sealed record FluxNodeDefinition(string Name, string FullName, string Category, bool IsGeneric, string? ThisType,
    IReadOnlyList<FluxNodePort> Inputs, IReadOnlyList<FluxNodePort> Outputs, IReadOnlyList<FluxNodePort> Globals);

public sealed record FluxNodeCatalogDocument(string FluxSdkVersion, string LibraryIdentity, DateTimeOffset GeneratedAt,
    IReadOnlyList<FluxNodeDefinition> Nodes, string CachePath);

public static class FluxNodeCatalog
{
    public static async Task<FluxNodeCatalogDocument> GetOrCreateAsync(FluxProcessTool tool, string? libraryPath,
        string cacheDirectory, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var status = await tool.GetStatusAsync(cancellationToken);
        var version = status.Version ?? "unknown";
        var libraryIdentity = string.IsNullOrWhiteSpace(libraryPath) ? "auto" : Path.GetFullPath(libraryPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version + "\n" + libraryIdentity)))[..16];
        var cachePath = Path.Combine(Path.GetFullPath(cacheDirectory), $"nodes-{Safe(version)}-{key}.json");
        if (!refresh && File.Exists(cachePath))
        {
            var cached = JsonSerializer.Deserialize<FluxNodeCatalogDocument>(await File.ReadAllTextAsync(cachePath, cancellationToken), JsonOptions);
            if (cached is not null) return cached with { CachePath = cachePath };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporary = cachePath + ".metadata.tmp";
        try
        {
            var result = await tool.GenerateNodeDocsAsync(temporary, libraryPath, cancellationToken);
            if (!result.Success || !File.Exists(temporary))
                throw new RLoop.Core.RLoopException("FLUX_NODE_CATALOG_FAILED",
                    "Flux-SDK could not generate Froox node metadata.", RLoop.Core.ExitCodes.ExternalToolFailed,
                    new Dictionary<string, object?> { ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError });
            var nodes = Parse(await File.ReadAllTextAsync(temporary, cancellationToken));
            var document = new FluxNodeCatalogDocument(version, libraryIdentity, DateTimeOffset.UtcNow, nodes, cachePath);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(document, JsonOptions) + "\n", cancellationToken);
            return document;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static IReadOnlyList<FluxNodeDefinition> Parse(string metadata)
    {
        var nodes = new List<FluxNodeDefinition>();
        Builder? current = null;
        string? section = null;
        foreach (var raw in metadata.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Finish(); continue; }
            if (!char.IsWhiteSpace(line[0]) && line.EndsWith(':'))
            {
                Finish();
                current = new Builder(line[..^1]);
                section = null;
                continue;
            }
            if (current is null) continue;
            if (line.StartsWith("  FullName: ", StringComparison.Ordinal)) current.FullName = Unquote(line[12..]);
            else if (line.StartsWith("  ThisType: ", StringComparison.Ordinal)) current.ThisType = Unquote(line[12..]);
            else if (line is "  Inputs:" or "  Outputs:" or "  Globals:") section = line.Trim()[..^1];
            else if (line.StartsWith("  Inputs: {}", StringComparison.Ordinal) || line.StartsWith("  Outputs: {}", StringComparison.Ordinal) ||
                     line.StartsWith("  Globals: {}", StringComparison.Ordinal)) section = null;
            else if (section is not null && line.StartsWith("    ", StringComparison.Ordinal))
            {
                var separator = line.IndexOf(':', 4);
                if (separator > 4)
                    current.Ports(section).Add(new FluxNodePort(Unquote(line[4..separator].Trim()), Unquote(line[(separator + 1)..].Trim())));
            }
        }
        Finish();
        return nodes;

        void Finish()
        {
            if (current is null || string.IsNullOrWhiteSpace(current.FullName)) return;
            var namespaceParts = (current.FullName.Contains(".Nodes.", StringComparison.Ordinal)
                ? current.FullName.Split(".Nodes.", 2, StringSplitOptions.None)[1]
                : current.FullName).Split('.');
            var category = namespaceParts.Length > 1 ? string.Join('/', namespaceParts[..^1]) : string.Empty;
            nodes.Add(new FluxNodeDefinition(current.Name, current.FullName, category,
                current.Name.Contains('<') || current.FullName.Contains('`'), current.ThisType,
                current.Inputs, current.Outputs, current.Globals));
            current = null;
        }
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
        ? value[1..^1].Replace("\\\"", "\"") : value;
    private static string Safe(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private sealed class Builder(string name)
    {
        public string Name { get; } = name;
        public string FullName { get; set; } = string.Empty;
        public string? ThisType { get; set; }
        public List<FluxNodePort> Inputs { get; } = [];
        public List<FluxNodePort> Outputs { get; } = [];
        public List<FluxNodePort> Globals { get; } = [];
        public List<FluxNodePort> Ports(string section) => section switch
        {
            "Inputs" => Inputs,
            "Outputs" => Outputs,
            _ => Globals
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public sealed record FluxModulePort(string Name, string Direction, string Type, string? Modifier);

public static class FluxModuleSignature
{
    public static IReadOnlyList<FluxModulePort> Parse(string source)
    {
        var ports = new List<FluxModulePort>();
        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Split("//", 2, StringSplitOptions.None)[0].Trim();
            if (line.StartsWith("where", StringComparison.Ordinal)) break;
            var direction = line.StartsWith("in ", StringComparison.Ordinal) ? "source" :
                line.StartsWith("out ", StringComparison.Ordinal) ? "drive" : null;
            if (direction is null) continue;
            var body = line[(direction == "source" ? 3 : 4)..].Trim();
            var separator = body.IndexOf(':');
            if (separator <= 0 || separator == body.Length - 1) continue;
            var name = body[..separator].Trim();
            var typeParts = body[(separator + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var modifier = typeParts.LastOrDefault() is "global" or "element" or "mutable" ? typeParts[^1] : null;
            var type = modifier is null ? string.Join(' ', typeParts) : string.Join(' ', typeParts[..^1]);
            if (name.Length > 0 && type.Length > 0) ports.Add(new FluxModulePort(name, direction, type, modifier));
        }
        return ports;
    }
}
