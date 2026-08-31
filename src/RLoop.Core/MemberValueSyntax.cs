using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public static class MemberValueSyntax
{
    public static bool IsStructuredTuple(string? type, out int count)
    {
        var simple = SimpleType(type);
        count = simple switch
        {
            "float2" or "double2" or "int2" or "uint2" or "long2" or "ulong2" => 2,
            "float3" or "double3" or "int3" or "uint3" or "long3" or "ulong3" => 3,
            "float4" or "double4" or "int4" or "uint4" or "long4" or "ulong4" or
                "floatq" or "quaternion" or "color" or "colorx" => 4,
            _ => 0
        };
        return count != 0;
    }

    public static JsonArray ParseTuple(string? type, string raw)
    {
        if (!IsStructuredTuple(type, out var count))
            throw new RLoopException("VALUE_TYPE_UNSUPPORTED", $"Field type '{type}' is not a supported tuple value.", ExitCodes.ValidationFailed);
        try
        {
            var trimmed = raw.Trim();
            JsonArray result;
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
                result = JsonNode.Parse(trimmed) as JsonArray ?? throw new JsonException("Expected an array.");
            else if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                var source = JsonNode.Parse(trimmed) as JsonObject ?? throw new JsonException("Expected an object.");
                var names = source.ContainsKey("x") ? new[] { "x", "y", "z", "w" } :
                    source.ContainsKey("r") ? new[] { "r", "g", "b", "a" } : [];
                if (names.Length == 0) throw new JsonException("Expected x/y/z/w or r/g/b/a properties.");
                result = new JsonArray(names.Take(count).Select(name => source[name]?.DeepClone()).ToArray());
            }
            else
            {
                var parts = trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                result = new JsonArray(parts.Select(part => JsonValue.Create(double.Parse(part, CultureInfo.InvariantCulture))).ToArray());
            }

            if (result.Count != count || result.Any(value => value is null || !TryNumber(value, out _))) throw new FormatException();
            return result;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            throw new RLoopException("VALUE_CONVERSION_FAILED", $"Cannot convert '{raw}' to '{type}'.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["value"] = raw, ["targetType"] = type }, AcceptedExamples(type, count), ex);
        }
    }

    public static IReadOnlyList<string> AcceptedExamples(string? type, int? knownCount = null)
    {
        var count = knownCount ?? (IsStructuredTuple(type, out var detected) ? detected : 0);
        if (count == 0) return [];
        var numbers = string.Join(',', Enumerable.Range(0, count).Select(index => index == count - 1 && count == 4 ? "1" : "0"));
        var axes = count switch { 2 => "{\"x\":0,\"y\":0}", 3 => "{\"x\":0,\"y\":0,\"z\":0}", _ => "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}" };
        if (SimpleType(type) is "color" or "colorx") axes = "{\"r\":0,\"g\":0,\"b\":0,\"a\":1}";
        return [$"Use a JSON array such as [{numbers}].", $"Use an object such as {axes}.", $"Comma strings such as \"{numbers}\" remain supported for compatibility."];
    }

    public static JsonNode? NormalizeTupleOrJson(string? type, string raw, int? inferredTupleCount = null)
    {
        if (IsStructuredTuple(type, out _)) return ParseTuple(type, raw);
        if (inferredTupleCount is > 0)
        {
            var synthetic = inferredTupleCount switch { 2 => "float2", 3 => "float3", _ => "float4" };
            try { return ParseTuple(synthetic, raw); } catch (RLoopException) { }
        }
        try { return JsonNode.Parse(raw); }
        catch (JsonException) { return JsonValue.Create(raw); }
    }

    public static bool TryNumber(JsonNode value, out double number)
    {
        if (value is JsonValue json && json.TryGetValue<double>(out number)) return true;
        number = default;
        return false;
    }

    private static string SimpleType(string? type)
    {
        type ??= string.Empty;
        var end = type.IndexOf(']');
        if (end >= 0) type = type[(end + 1)..];
        return type.Split('.').Last().TrimEnd('?').ToLowerInvariant();
    }
}
