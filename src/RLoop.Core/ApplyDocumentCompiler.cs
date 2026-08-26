using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

/// <summary>Expands the reusable authoring syntax into the deliberately small schema-v1 apply model.</summary>
public static class ApplyDocumentCompiler
{
    private const int MaxFiles = 64;
    private const int MaxExpandedNodes = 10_000;
    private const int MaxExpandedBytes = 10 * 1024 * 1024;

    public sealed record Result(string Json, ApplyCompilationSummary Summary);

    public static Result Compile(string path)
    {
        var context = new Context();
        var root = LoadMerged(Path.GetFullPath(path), context, []);
        var variables = ReadObject(root["parameters"] as JsonObject);
        MergeVariables(variables, root["variables"] as JsonObject, "variables");
        var prototypes = root["prototypes"] as JsonObject ?? new JsonObject();
        context.Prototypes = prototypes.Count;
        root.Remove("include");
        root.Remove("parameters");
        root.Remove("variables");
        root.Remove("prototypes");
        ExpandValue(root, variables, prototypes, context, "$", allowPrototype: false);
        DetectStableKeyConflicts(root);
        context.ExpandedNodes = CountNodes(root);
        if (context.ExpandedNodes > MaxExpandedNodes)
            Fail("APPLY_EXPANDED_NODE_LIMIT", $"Expanded document contains {context.ExpandedNodes} nodes; the limit is {MaxExpandedNodes}.");
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxExpandedBytes)
            Fail("APPLY_EXPANDED_SIZE_LIMIT", $"Expanded document is {bytes} bytes; the limit is {MaxExpandedBytes}.");
        return new Result(json, new ApplyCompilationSummary(context.Files.Count, context.Prototypes,
            context.Instances, context.Repeated, context.ExpandedNodes, bytes));
    }

    private static JsonObject LoadMerged(string path, Context context, IReadOnlyList<string> stack)
    {
        if (stack.Contains(path, StringComparer.OrdinalIgnoreCase))
            Fail("APPLY_INCLUDE_CYCLE", $"Include cycle detected: {string.Join(" -> ", stack.Append(path))}");
        if (!File.Exists(path)) Fail("APPLY_INCLUDE_NOT_FOUND", $"Included apply file '{path}' does not exist.");
        context.Files.Add(path);
        if (context.Files.Count > MaxFiles) Fail("APPLY_INCLUDE_LIMIT", $"An apply document may expand at most {MaxFiles} source files.");
        JsonObject current;
        try
        {
            current = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                      ?? throw new JsonException("The root must be an object.");
        }
        catch (JsonException ex)
        {
            throw new RLoopException("APPLY_DOCUMENT_INVALID", $"Invalid apply document '{path}': {ex.Message}",
                ExitCodes.ValidationFailed, innerException: ex);
        }

        var merged = new JsonObject();
        var includes = current["include"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var single) => new[] { single },
            JsonArray array => array.Select(x => x?.GetValue<string>() ?? string.Empty).ToArray(),
            null => [],
            _ => throw new RLoopException("APPLY_INCLUDE_INVALID", "include must be a path or array of paths.", ExitCodes.ValidationFailed)
        };
        foreach (var include in includes)
        {
            var resolved = Path.GetFullPath(include, Path.GetDirectoryName(path)!);
            Merge(merged, LoadMerged(resolved, context, stack.Append(path).ToArray()), includeLayer: true);
        }
        current.Remove("include");
        Merge(merged, current, includeLayer: false);
        return merged;
    }

    private static void Merge(JsonObject target, JsonObject source, bool includeLayer)
    {
        foreach (var property in source)
        {
            if (property.Value is null) { target[property.Key] = null; continue; }
            if (target[property.Key] is JsonObject targetObject && property.Value is JsonObject sourceObject)
            {
                if (property.Key is "prototypes" or "assets" or "cameras" or "parameters" or "variables")
                {
                    foreach (var named in sourceObject)
                    {
                        if (targetObject.ContainsKey(named.Key))
                            Fail("APPLY_INCLUDE_KEY_CONFLICT", $"Multiple source files declare '{property.Key}.{named.Key}'.");
                        targetObject[named.Key] = named.Value?.DeepClone();
                    }
                    continue;
                }
                Merge(targetObject, sourceObject, includeLayer);
                continue;
            }
            if (target[property.Key] is JsonArray targetArray && property.Value is JsonArray sourceArray &&
                property.Key is "children" or "components" or "tests")
            {
                foreach (var item in sourceArray) targetArray.Add(item?.DeepClone());
                continue;
            }
            target[property.Key] = property.Value.DeepClone();
        }
    }

    private static Dictionary<string, JsonNode?> ReadObject(JsonObject? source) =>
        source?.ToDictionary(x => x.Key, x => x.Value?.DeepClone(), StringComparer.Ordinal)
        ?? new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    private static void MergeVariables(Dictionary<string, JsonNode?> target, JsonObject? source, string label)
    {
        if (source is null) return;
        foreach (var pair in source)
            if (!target.TryAdd(pair.Key, pair.Value?.DeepClone()))
                Fail("APPLY_PARAMETER_CONFLICT", $"{label} '{pair.Key}' is already declared.");
    }

    private static void ExpandValue(JsonNode node, IReadOnlyDictionary<string, JsonNode?> variables,
        JsonObject prototypes, Context context, string path, bool allowPrototype = true)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (obj[key] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    obj[key] = Substitute(text, variables, path + "." + key);
                else if (obj[key] is { } child)
                    ExpandValue(child, variables, prototypes, context, path + "." + key);
            }
            foreach (var arrayName in new[] { "children" })
                if (obj[arrayName] is JsonArray array) ExpandNodeArray(array, variables, prototypes, context, path + "." + arrayName);
            return;
        }
        if (node is JsonArray values)
            for (var i = 0; i < values.Count; i++)
                if (values[i] is JsonValue value && value.TryGetValue<string>(out var text)) values[i] = Substitute(text, variables, path + $"[{i}]");
                else if (values[i] is { } child) ExpandValue(child, variables, prototypes, context, path + $"[{i}]");
    }

    private static void ExpandNodeArray(JsonArray array, IReadOnlyDictionary<string, JsonNode?> variables,
        JsonObject prototypes, Context context, string path)
    {
        var output = new JsonArray();
        for (var i = 0; i < array.Count; i++)
        {
            var source = array[i] as JsonObject ?? throw new RLoopException("APPLY_NODE_INVALID", $"{path}[{i}] must be an object.", ExitCodes.ValidationFailed);
            var repeat = source["$repeat"] as JsonObject;
            var count = repeat?["count"]?.GetValue<int>() ?? 1;
            if (count < 0 || count > MaxExpandedNodes) Fail("APPLY_REPEAT_LIMIT", $"{path}[{i}] repeat count is outside 0..{MaxExpandedNodes}.");
            var variable = repeat?["as"]?.GetValue<string>() ?? "index";
            var offset = repeat?["offset"] is JsonArray offsetArray
                ? offsetArray.Select(x => x?.GetValue<float>() ?? 0).ToArray() : null;
            if (offset is { Length: not 3 }) Fail("APPLY_REPEAT_OFFSET_INVALID", $"{path}[{i}].$repeat.offset requires three numbers.");
            for (var repeatIndex = 0; repeatIndex < count; repeatIndex++)
            {
                var scoped = new Dictionary<string, JsonNode?>(variables, StringComparer.Ordinal)
                {
                    [variable] = JsonValue.Create(repeatIndex)
                };
                var node = Instantiate(source, scoped, prototypes, context, path + $"[{i}]");
                node.Remove("$repeat");
                if (offset is not null && node["slot"] is JsonObject slot)
                {
                    var position = slot["position"] as JsonArray ?? new JsonArray(0, 0, 0);
                    while (position.Count < 3) position.Add(0);
                    for (var axis = 0; axis < 3; axis++)
                        position[axis] = (position[axis]?.GetValue<float>() ?? 0) + offset[axis] * repeatIndex;
                    slot["position"] = position;
                }
                ExpandValue(node, scoped, prototypes, context, path + $"[{i}:{repeatIndex}]");
                output.Add(node);
                if (count > 1) context.Repeated++;
            }
        }
        array.Clear();
        foreach (var item in output) array.Add(item?.DeepClone());
    }

    private static JsonObject Instantiate(JsonObject source, IReadOnlyDictionary<string, JsonNode?> variables,
        JsonObject prototypes, Context context, string path)
    {
        var prototypeName = source["$prototype"]?.GetValue<string>() ?? source["$instance"]?.GetValue<string>();
        if (prototypeName is null) return (JsonObject)source.DeepClone();
        var prototype = prototypes[prototypeName] as JsonObject ?? throw new RLoopException(
            "APPLY_PROTOTYPE_NOT_FOUND", $"{path} references unknown prototype '{prototypeName}'.", ExitCodes.ValidationFailed);
        var scoped = new Dictionary<string, JsonNode?>(variables, StringComparer.Ordinal);
        if (source["$with"] is JsonObject supplied)
            foreach (var pair in supplied) scoped[pair.Key] = pair.Value?.DeepClone();
        var instance = (JsonObject)prototype.DeepClone();
        var overrides = (JsonObject)source.DeepClone();
        overrides.Remove("$prototype"); overrides.Remove("$instance"); overrides.Remove("$with");
        Merge(instance, overrides, includeLayer: false);
        SubstituteTree(instance, scoped, path);
        context.Instances++;
        return instance;
    }

    private static void SubstituteTree(JsonNode node, IReadOnlyDictionary<string, JsonNode?> variables, string path)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = Substitute(text, variables, path);
                else if (obj[key] is { } child) SubstituteTree(child, variables, path);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue arrayValue && arrayValue.TryGetValue<string>(out var arrayText)) array[i] = Substitute(arrayText, variables, path);
                else if (array[i] is { } arrayChild) SubstituteTree(arrayChild, variables, path);
            }
        }
    }

    private static JsonNode? Substitute(string text, IReadOnlyDictionary<string, JsonNode?> variables, string path)
    {
        if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}') && text.Count(x => x == '$') == 1)
        {
            var name = text[2..^1];
            if (!variables.TryGetValue(name, out var exact)) Fail("APPLY_PARAMETER_NOT_FOUND", $"{path} references unknown parameter '{name}'.");
            return exact?.DeepClone();
        }
        var result = text;
        foreach (var pair in variables)
        {
            var token = "${" + pair.Key + "}";
            if (!result.Contains(token, StringComparison.Ordinal)) continue;
            var replacement = pair.Value switch
            {
                null => string.Empty,
                JsonValue value when value.TryGetValue<string>(out var s) => s,
                JsonValue value => value.ToJsonString().Trim('"'),
                _ => pair.Value.ToJsonString()
            };
            result = result.Replace(token, replacement, StringComparison.Ordinal);
        }
        if (result.Contains("${", StringComparison.Ordinal)) Fail("APPLY_PARAMETER_NOT_FOUND", $"{path} contains an unresolved parameter in '{result}'.");
        return JsonValue.Create(result);
    }

    private static void DetectStableKeyConflicts(JsonObject root)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string? key, string path)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!keys.TryAdd(key, path)) Fail("APPLY_EXPANDED_KEY_CONFLICT", $"Stable key '{key}' is used by both '{keys[key]}' and '{path}'.");
        }
        void Visit(JsonObject node, string path)
        {
            if (node["slot"] is JsonObject slot) Add(slot["key"]?.GetValue<string>(), path + ".slot.key");
            if (node["components"] is JsonArray components)
                for (var i = 0; i < components.Count; i++) if (components[i] is JsonObject component) Add(component["key"]?.GetValue<string>(), path + $".components[{i}].key");
            if (node["children"] is JsonArray children)
                for (var i = 0; i < children.Count; i++) if (children[i] is JsonObject child) Visit(child, path + $".children[{i}]");
        }
        Visit(root, "$");
    }

    private static int CountNodes(JsonNode node) => node switch
    {
        JsonObject obj => 1 + obj.Sum(x => x.Value is null ? 0 : CountNodes(x.Value)),
        JsonArray array => 1 + array.Sum(x => x is null ? 0 : CountNodes(x)),
        _ => 1
    };

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new RLoopException(code, message, ExitCodes.ValidationFailed);

    private sealed class Context
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Prototypes { get; set; }
        public int Instances { get; set; }
        public int Repeated { get; set; }
        public int ExpandedNodes { get; set; }
    }
}
