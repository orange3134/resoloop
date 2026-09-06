using System.Text.Json;

namespace RLoop.Core;

public static class GeneratedContentMetadata
{
    public const string ComponentType = "FrooxEngine.AI_GeneratedContent";
    public const string SourceMember = "Source";

    public static string SourceForVersion(string version) => $"[resoloop {version}]";

    internal static ApplyDocument AddToGeneratedRoots(ApplyDocument document, string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || document.Slot is null) return document;
        return document with
        {
            Components = EnsureMarker(document.Components, source),
            Children = AnnotateChildren(document.Children, source)
        };
    }

    private static IReadOnlyList<ApplyNodeSpec>? AnnotateChildren(IReadOnlyList<ApplyNodeSpec>? children, string source)
    {
        if (children is null) return null;
        return children.Select(child => child with
        {
            Components = IsPortableRoot(child)
                ? EnsureMarker(child.Components, source)
                : child.Components,
            Children = AnnotateChildren(child.Children, source)
        }).ToArray();
    }

    private static bool IsPortableRoot(ApplyNodeSpec node) =>
        node.Slot.RuntimeRelocatable || (node.Components ?? []).Any(component =>
            PortableRootMarkers.Contains(SimpleTypeName(component.Type)));

    private static IReadOnlyList<ApplyComponentSpec> EnsureMarker(
        IReadOnlyList<ApplyComponentSpec>? components, string source)
    {
        var result = (components ?? []).ToList();
        var markerIndex = result.FindIndex(component => TypeNamesEquivalent(component.Type, ComponentType));
        var sourceValue = JsonSerializer.SerializeToElement(source);
        if (markerIndex < 0)
        {
            result.Add(new ApplyComponentSpec(ComponentType,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [SourceMember] = sourceValue
                }));
            return result;
        }

        var marker = result[markerIndex];
        var fields = new Dictionary<string, JsonElement>(marker.Fields ??
            new Dictionary<string, JsonElement>(), StringComparer.Ordinal)
        {
            [SourceMember] = sourceValue
        };
        Dictionary<string, JsonElement>? initialFields = marker.InitialFields is null
            ? null
            : new Dictionary<string, JsonElement>(marker.InitialFields, StringComparer.Ordinal);
        initialFields?.Remove(SourceMember);
        result[markerIndex] = marker with { Fields = fields, InitialFields = initialFields };
        return result;
    }

    private static string SimpleTypeName(string type)
    {
        var normalized = NormalizeType(type);
        var separator = normalized.LastIndexOf('.');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static bool TypeNamesEquivalent(string left, string right)
    {
        var normalizedLeft = NormalizeType(left);
        var normalizedRight = NormalizeType(right);
        return normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal) ||
               normalizedLeft.EndsWith('.' + normalizedRight, StringComparison.Ordinal) ||
               normalizedRight.EndsWith('.' + normalizedLeft, StringComparison.Ordinal);
    }

    private static string NormalizeType(string value)
    {
        var bracket = value.IndexOf(']');
        return bracket >= 0 ? value[(bracket + 1)..] : value;
    }

    private static readonly HashSet<string> PortableRootMarkers = new(StringComparer.Ordinal)
    {
        "Grabbable",
        "RawDataTool",
        "AvatarRoot",
        "ObjectRoot"
    };
}
