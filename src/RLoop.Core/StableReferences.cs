using System.Text.Json;

namespace RLoop.Core;

public sealed record StableSlotReference(string Key, string Id, string Path, string? SessionId, string OwnershipKey);
public sealed record StableComponentReference(string Key, string Id, string SlotKey, string Type, int TypeOrdinal,
    string? SessionId, string OwnershipKey);
public sealed record ResolvedWorldReference(string Selector, string Id, string Kind, string? Type, string? Path = null);

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
                root.GetProperty("ownershipKey").GetString() ?? string.Empty);
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
            return new StableComponentReference(key, component.GetProperty("id").GetString() ?? string.Empty,
                component.GetProperty("slotKey").GetString() ?? string.Empty,
                component.GetProperty("type").GetString() ?? string.Empty,
                component.GetProperty("typeOrdinal").GetInt32(),
                root.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
                root.GetProperty("ownershipKey").GetString() ?? string.Empty);
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
