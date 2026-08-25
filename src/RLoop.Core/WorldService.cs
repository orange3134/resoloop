using System.Text.Json;

namespace RLoop.Core;

public sealed class WorldService(IResoniteClient client)
{
    public async Task<string> ResolveSlotIdAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new RLoopException("SLOT_SELECTOR_MISSING", "A Slot ID or path is required.", ExitCodes.InvalidArguments);
        if (selector.Equals("Root", StringComparison.OrdinalIgnoreCase) || selector is "/" or "/Root") return "Root";

        if (!selector.Contains('/') && !selector.StartsWith("Root", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return (await client.GetSlotAsync(selector, 0, false, cancellationToken)).Id;
            }
            catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
            {
                var matches = await FindAsync(selector, true, null, 8, cancellationToken);
                return matches.Count switch
                {
                    1 => matches[0].Id,
                    0 => throw new RLoopException("SLOT_NOT_FOUND", $"Slot '{selector}' was not found as an ID or exact name.", ExitCodes.NotFound),
                    _ => throw new RLoopException("SLOT_AMBIGUOUS", $"Slot name '{selector}' matched {matches.Count} Slots; use an ID or path.", ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["matches"] = matches.Select(x => new { x.Id, x.Path }).ToArray() })
                };
            }
        }

        var parts = selector.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("Root", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        var currentId = "Root";
        var currentPath = "Root";
        foreach (var part in parts)
        {
            var current = await client.GetSlotAsync(currentId, 1, false, cancellationToken);
            var matches = current.Children.Where(x => x.Name.Equals(part, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
                throw new RLoopException("SLOT_PATH_NOT_FOUND", $"Path segment '{part}' was not found below '{currentPath}'.", ExitCodes.NotFound,
                    new Dictionary<string, object?> { ["path"] = selector, ["resolvedPrefix"] = currentPath });
            if (matches.Length > 1)
                throw new RLoopException("SLOT_PATH_AMBIGUOUS", $"Path segment '{part}' matched multiple Slots below '{currentPath}'. Use a Slot ID.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["ids"] = matches.Select(x => x.Id).ToArray() });
            currentId = matches[0].Id;
            currentPath += "/" + part;
        }
        return currentId;
    }

    public async Task<SlotInfo> InspectAsync(string selector, int depth, bool includeComponentData,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, depth, includeComponentData, cancellationToken);
        return AddPaths(slot, selector.Contains('/') ? NormalizePath(selector) : slot.Name);
    }

    public async Task<IReadOnlyList<SlotMatch>> FindAsync(string? name, bool exact, string? componentType,
        int depth, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(componentType))
            throw new RLoopException("FIND_FILTER_MISSING", "find requires --name or --component.", ExitCodes.InvalidArguments);

        var root = await client.GetSlotAsync("Root", depth, false, cancellationToken);
        var results = new List<SlotMatch>();
        Visit(root, "Root", slot =>
        {
            var nameMatches = string.IsNullOrWhiteSpace(name) || (exact
                ? slot.Name.Equals(name, StringComparison.Ordinal)
                : slot.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            var componentMatches = string.IsNullOrWhiteSpace(componentType) || slot.Components.Any(c =>
                c.Type.Contains(componentType, StringComparison.OrdinalIgnoreCase));
            if (nameMatches && componentMatches)
                results.Add(new SlotMatch(slot.Id, slot.Name, slot.Path!, slot.Components));
        });
        return results;
    }

    public async Task<IReadOnlyList<ComponentInfo>> ListComponentsAsync(string selector, CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, 0, false, cancellationToken);
        var result = new List<ComponentInfo>();
        foreach (var component in slot.Components)
            result.Add(await client.GetComponentAsync(component.Id, cancellationToken));
        return result;
    }

    public async Task<ApplyResult> ApplyAsync(ApplyDocument document, CancellationToken cancellationToken = default)
    {
        if (document.Slot is null || string.IsNullOrWhiteSpace(document.Slot.Name))
            throw new RLoopException("APPLY_SLOT_NAME_MISSING", "apply document requires slot.name.", ExitCodes.ValidationFailed);
        var parentSelector = string.IsNullOrWhiteSpace(document.Slot.Parent) ? "Root" : document.Slot.Parent;
        var parentId = await ResolveSlotIdAsync(parentSelector, cancellationToken);
        var parent = await client.GetSlotAsync(parentId, 1, false, cancellationToken);
        var existing = parent.Children.Where(x => x.Name == document.Slot.Name).ToArray();
        if (existing.Length > 1)
            throw new RLoopException("APPLY_TARGET_AMBIGUOUS", $"Multiple Slots named '{document.Slot.Name}' exist below '{parentSelector}'.", ExitCodes.ValidationFailed,
                suggestions: ["Use a unique slot.name or edit the target by ID with primitive commands."]);

        var created = existing.Length == 0;
        var slotId = created
            ? await client.CreateSlotAsync(new SlotCreateRequest(parentId, document.Slot.Name,
                document.Slot.Position?.ToVector3("position"), document.Slot.Rotation?.ToQuaternion("rotation"),
                document.Slot.Scale?.ToVector3("scale")), cancellationToken)
            : existing[0].Id;

        if (!created)
            await client.UpdateSlotAsync(new SlotUpdateRequest(slotId, document.Slot.Name,
                document.Slot.Position?.ToVector3("position"), document.Slot.Rotation?.ToQuaternion("rotation"),
                document.Slot.Scale?.ToVector3("scale")), cancellationToken);

        var current = await client.GetSlotAsync(slotId, 0, false, cancellationToken);
        var added = 0;
        var updated = 0;
        foreach (var componentSpec in document.Components ?? [])
        {
            if (string.IsNullOrWhiteSpace(componentSpec.Type))
                throw new RLoopException("APPLY_COMPONENT_TYPE_MISSING", "Every component requires type.", ExitCodes.ValidationFailed);
            var existingComponent = current.Components.FirstOrDefault(x => TypeNamesEquivalent(x.Type, componentSpec.Type));
            var fields = componentSpec.Fields ?? new Dictionary<string, JsonElement>();
            if (existingComponent is null)
            {
                var rawFields = fields.ToDictionary(x => x.Key, x => JsonElementToRaw(x.Value), StringComparer.Ordinal);
                await client.AddComponentAsync(slotId, componentSpec.Type, rawFields, cancellationToken);
                added++;
            }
            else
            {
                foreach (var field in fields)
                    await client.SetComponentMemberAsync(existingComponent.Id, field.Key, JsonElementToRaw(field.Value), cancellationToken);
                updated++;
            }
        }
        return new ApplyResult(slotId, created, added, updated);
    }

    private static bool TypeNamesEquivalent(string left, string right)
    {
        static string Normalize(string value)
        {
            var bracket = value.IndexOf(']');
            return bracket >= 0 ? value[(bracket + 1)..] : value;
        }
        return Normalize(left).Equals(Normalize(right), StringComparison.Ordinal) ||
               Normalize(left).EndsWith('.' + Normalize(right), StringComparison.Ordinal);
    }

    private static string JsonElementToRaw(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.Null => "null",
        JsonValueKind.Array => string.Join(',', element.EnumerateArray().Select(JsonElementToRaw)),
        _ => element.GetRawText()
    };

    private static SlotInfo AddPaths(SlotInfo slot, string path)
    {
        var children = slot.Children.Select(c => AddPaths(c, path.TrimEnd('/') + "/" + c.Name)).ToArray();
        return slot with { Path = path, Children = children };
    }

    private static void Visit(SlotInfo slot, string path, Action<SlotInfo> visitor)
    {
        var withPath = slot with { Path = path };
        visitor(withPath);
        foreach (var child in slot.Children) Visit(child, path + "/" + child.Name, visitor);
    }

    private static string NormalizePath(string path) => "Root/" + string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => !x.Equals("Root", StringComparison.OrdinalIgnoreCase)));
}

public sealed record ApplyDocument(ApplySlotSpec? Slot, IReadOnlyList<ApplyComponentSpec>? Components)
{
    public static ApplyDocument Load(string path)
    {
        if (!File.Exists(path)) throw new RLoopException("APPLY_FILE_NOT_FOUND", $"Apply file '{path}' does not exist.", ExitCodes.NotFound);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new RLoopException("APPLY_FORMAT_UNSUPPORTED", "v0.1 supports JSON apply documents. YAML support is planned.", ExitCodes.ValidationFailed,
                suggestions: ["Use a .json apply document; see examples/agent-test.json."]);
        try
        {
            return JsonSerializer.Deserialize<ApplyDocument>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Document was empty.");
        }
        catch (JsonException ex)
        {
            throw new RLoopException("APPLY_DOCUMENT_INVALID", $"Invalid apply document: {ex.Message}", ExitCodes.ValidationFailed, innerException: ex);
        }
    }
}

public sealed record ApplySlotSpec(string Name, string? Parent, float[]? Position, float[]? Rotation, float[]? Scale);
public sealed record ApplyComponentSpec(string Type, IReadOnlyDictionary<string, JsonElement>? Fields);

internal static class ApplyArrayExtensions
{
    public static Vector3Value ToVector3(this float[] value, string name)
    {
        if (value.Length != 3) throw new RLoopException("APPLY_VECTOR_INVALID", $"slot.{name} requires exactly 3 numbers.", ExitCodes.ValidationFailed);
        return new Vector3Value(value[0], value[1], value[2]);
    }

    public static QuaternionValue ToQuaternion(this float[] value, string name)
    {
        if (value.Length != 4) throw new RLoopException("APPLY_QUATERNION_INVALID", $"slot.{name} requires exactly 4 numbers (x,y,z,w).", ExitCodes.ValidationFailed);
        return new QuaternionValue(value[0], value[1], value[2], value[3]);
    }
}
