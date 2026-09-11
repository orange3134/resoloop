using System.Text.Json;

namespace RLoop.Core;

public sealed record StableSlotReference(string Key, string Id, string Path, string? SessionId, string OwnershipKey,
    bool RuntimeRelocatable = false);
public sealed record StableComponentReference(string Key, string Id, string SlotKey, string Type, int TypeOrdinal,
    string? SessionId, string OwnershipKey, int? ComponentIndex = null,
    IReadOnlyList<string>? MemberNames = null, IReadOnlyDictionary<string, string>? IdentityValues = null,
    IReadOnlyDictionary<string, string>? ReferenceSelectors = null);
public sealed record ResolvedWorldReference(string Selector, string Id, string Kind, string? Type, string? Path = null);
public sealed record StableSelector(string Original, string Kind, string Key, string? MemberName = null);

public static class StableSelectorSyntax
{
    public static bool TryParse(string value, out StableSelector? selector)
    {
        selector = null;
        if (value.StartsWith("$slot:", StringComparison.Ordinal))
        {
            var key = value[6..];
            if (key.Length > 0) selector = new StableSelector(value, "slot", key);
        }
        else if (value.StartsWith("$component:", StringComparison.Ordinal) || value.StartsWith("$ref:", StringComparison.Ordinal))
        {
            var offset = value.StartsWith("$component:", StringComparison.Ordinal) ? 11 : 5;
            var key = value[offset..];
            if (key.Length > 0) selector = new StableSelector(value, "component", key);
        }
        else if (value.StartsWith("$member:", StringComparison.Ordinal) || value.StartsWith("$slot-member:", StringComparison.Ordinal))
        {
            var slotMember = value.StartsWith("$slot-member:", StringComparison.Ordinal);
            var body = value[(slotMember ? 13 : 8)..];
            var separator = body.LastIndexOf('.');
            if (separator > 0 && separator < body.Length - 1)
                selector = new StableSelector(value, slotMember ? "slot-member" : "member", body[..separator], body[(separator + 1)..]);
        }
        return selector is not null;
    }

    public static StableSelector Parse(string value)
    {
        if (TryParse(value, out var selector)) return selector!;
        throw new RLoopException("STABLE_SELECTOR_INVALID",
            $"Stable selector '{value}' is invalid.", ExitCodes.InvalidArguments,
            suggestions: ["Use $slot:key, $component:key, $member:componentKey.MemberName, or $slot-member:slotKey.MemberName."]);
    }
}

public static class StableReferenceResolver
{
    public static StableSlotReference ResolveSlot(string stateFile, string reference)
    {
        var key = reference.StartsWith("$slot:", StringComparison.Ordinal) ? reference[6..] : reference;
        var path = Path.GetFullPath(stateFile);
        if (!File.Exists(path)) throw new RLoopException("APPLY_STATE_NOT_FOUND", $"World state file '{path}' does not exist.", ExitCodes.NotFound);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.GetProperty("slots").TryGetProperty(key, out var slot))
                throw new RLoopException("STABLE_SLOT_NOT_FOUND", $"Stable slot key '{key}' is not present in '{path}'.", ExitCodes.NotFound);
            return new StableSlotReference(key, slot.GetProperty("id").GetString() ?? string.Empty,
                slot.GetProperty("path").GetString() ?? string.Empty,
                root.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
                root.GetProperty("ownershipKey").GetString() ?? string.Empty,
                slot.TryGetProperty("runtimeRelocatable", out var relocatable) && relocatable.ValueKind == JsonValueKind.True);
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new RLoopException("APPLY_STATE_INVALID", $"Cannot resolve '{reference}' from '{path}': {ex.Message}", ExitCodes.ValidationFailed, innerException: ex);
        }
    }

    public static StableComponentReference ResolveComponent(string stateFile, string reference)
    {
        var key = reference.StartsWith("$component:", StringComparison.Ordinal) ? reference[11..] : reference;
        return ReadState(stateFile, (root, path) =>
        {
            if (!root.GetProperty("components").TryGetProperty(key, out var component))
                throw new RLoopException("STABLE_COMPONENT_NOT_FOUND", $"Stable component key '{key}' is not present in '{path}'.", ExitCodes.NotFound);
            var memberNames = component.TryGetProperty("memberNames", out var memberNamesElement) && memberNamesElement.ValueKind == JsonValueKind.Array
                ? memberNamesElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(value => value.Length > 0).ToArray()
                : null;
            var identityValues = component.TryGetProperty("identityValues", out var identityElement) && identityElement.ValueKind == JsonValueKind.Object
                ? identityElement.EnumerateObject().ToDictionary(property => property.Name,
                    property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
                : null;
            var referenceSelectors = component.TryGetProperty("referenceSelectors", out var referenceElement) && referenceElement.ValueKind == JsonValueKind.Object
                ? referenceElement.EnumerateObject().ToDictionary(property => property.Name,
                    property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
                : null;
            return new StableComponentReference(key, component.GetProperty("id").GetString() ?? string.Empty,
                component.GetProperty("slotKey").GetString() ?? string.Empty,
                component.GetProperty("type").GetString() ?? string.Empty,
                component.GetProperty("typeOrdinal").GetInt32(),
                root.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
                root.GetProperty("ownershipKey").GetString() ?? string.Empty,
                component.TryGetProperty("componentIndex", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32() : null,
                memberNames, identityValues, referenceSelectors);
        }, reference);
    }

    private static T ReadState<T>(string stateFile, Func<JsonElement, string, T> read, string reference)
    {
        var path = Path.GetFullPath(stateFile);
        if (!File.Exists(path)) throw new RLoopException("APPLY_STATE_NOT_FOUND", $"World state file '{path}' does not exist.", ExitCodes.NotFound);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return read(document.RootElement, path);
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new RLoopException("APPLY_STATE_INVALID", $"Cannot resolve '{reference}' from '{path}': {ex.Message}", ExitCodes.ValidationFailed, innerException: ex);
        }
    }
}
