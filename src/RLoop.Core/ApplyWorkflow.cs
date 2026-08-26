using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record ApplyOwnershipSpec(string Key);

public sealed record ApplyDocument(
    string? SchemaVersion,
    ApplyOwnershipSpec? Ownership,
    ApplySlotSpec? Slot,
    IReadOnlyList<ApplyComponentSpec>? Components,
    IReadOnlyList<ApplyNodeSpec>? Children = null)
{
    [JsonIgnore]
    public string? SourcePath { get; init; }

    public static ApplyDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new RLoopException("APPLY_FILE_NOT_FOUND", $"Apply file '{path}' does not exist.", ExitCodes.NotFound);
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new RLoopException("APPLY_FORMAT_UNSUPPORTED", "Apply documents must use JSON.", ExitCodes.ValidationFailed);
        try
        {
            var fullPath = Path.GetFullPath(path);
            return (JsonSerializer.Deserialize<ApplyDocument>(File.ReadAllText(fullPath), JsonOptions)
                    ?? throw new JsonException("Document was empty.")) with { SourcePath = fullPath };
        }
        catch (JsonException ex)
        {
            throw new RLoopException("APPLY_DOCUMENT_INVALID", $"Invalid apply document: {ex.Message}",
                ExitCodes.ValidationFailed, innerException: ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record ApplySlotSpec(
    string Name,
    string? Parent,
    float[]? Position,
    float[]? Rotation,
    float[]? Scale,
    string? Key = null);

public sealed record ApplyComponentSpec(
    string Type,
    IReadOnlyDictionary<string, JsonElement>? Fields,
    string? Key = null);

public sealed record ApplyNodeSpec(
    ApplySlotSpec Slot,
    IReadOnlyList<ApplyComponentSpec>? Components,
    IReadOnlyList<ApplyNodeSpec>? Children = null);

public sealed record ApplyOptions(
    string? StateFile = null,
    bool Adopt = false,
    bool Profile = false,
    Action<ApplyProgress>? Progress = null);

public static class ApplyDocumentValidator
{
    public static async Task<ApplyValidationResult> ValidateAsync(
        ApplyDocument document,
        IResoniteClient? client = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? resolvedTypes = null)
    {
        var issues = new List<ApplyValidationIssue>();
        var strict = client is not null;
        var slots = 0;
        var components = 0;
        var references = 0;
        var componentKeys = new Dictionary<string, (ApplyComponentSpec Spec, string Path)>(StringComparer.Ordinal);
        var slotKeys = new HashSet<string>(StringComparer.Ordinal);
        var componentPaths = new List<(ApplyComponentSpec Spec, string Path)>();

        void Issue(string code, string message, string path) => issues.Add(new ApplyValidationIssue(code, message, path));

        if (document.SchemaVersion != "1")
            Issue("APPLY_SCHEMA_VERSION_UNSUPPORTED", "schemaVersion must be \"1\".", "$.schemaVersion");
        if (string.IsNullOrWhiteSpace(document.Ownership?.Key))
            Issue("APPLY_OWNERSHIP_MISSING", "ownership.key is required for a managed apply boundary.", "$.ownership.key");
        if (document.Slot is null)
            Issue("APPLY_SLOT_MISSING", "slot is required.", "$.slot");
        else if (string.IsNullOrWhiteSpace(document.Slot.Key))
            Issue("APPLY_ROOT_KEY_MISSING", "The root slot requires a stable slot.key.", "$.slot.key");

        void ScanValue(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? string.Empty;
                if (text.StartsWith("$ref:", StringComparison.Ordinal))
                {
                    references++;
                    var key = text[5..];
                    if (string.IsNullOrWhiteSpace(key)) Issue("APPLY_REFERENCE_INVALID", "Reference key is empty.", path);
                }
                else if (text.StartsWith("$member:", StringComparison.Ordinal))
                {
                    references++;
                    var selector = text[8..];
                    if (selector.LastIndexOf('.') <= 0)
                        Issue("APPLY_MEMBER_REFERENCE_INVALID", "Member references use $member:key.MemberName.", path);
                }
                return;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var element in value.EnumerateArray()) ScanValue(element, $"{path}[{index++}]");
            }
        }

        void Visit(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? nodeComponents,
            IReadOnlyList<ApplyNodeSpec>? children, string path)
        {
            slots++;
            if (string.IsNullOrWhiteSpace(slot.Name)) Issue("APPLY_SLOT_NAME_MISSING", "Every slot requires a non-empty name.", path + ".slot.name");
            if (slot.Position is { Length: not 3 }) Issue("APPLY_VECTOR_INVALID", "position requires exactly 3 numbers.", path + ".slot.position");
            if (slot.Rotation is { Length: not 4 }) Issue("APPLY_QUATERNION_INVALID", "rotation requires exactly 4 numbers.", path + ".slot.rotation");
            if (slot.Scale is { Length: not 3 }) Issue("APPLY_VECTOR_INVALID", "scale requires exactly 3 numbers.", path + ".slot.scale");
            if (!string.IsNullOrWhiteSpace(slot.Key) && !slotKeys.Add(slot.Key))
                Issue("APPLY_SLOT_KEY_DUPLICATE", $"Slot key '{slot.Key}' is duplicated.", path + ".slot.key");

            var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < (nodeComponents?.Count ?? 0); i++)
            {
                var component = nodeComponents![i];
                var componentPath = $"{path}.components[{i}]";
                components++;
                componentPaths.Add((component, componentPath));
                if (string.IsNullOrWhiteSpace(component.Type))
                    Issue("APPLY_COMPONENT_TYPE_MISSING", "Every component requires type.", componentPath + ".type");
                else
                    typeCounts[component.Type] = typeCounts.GetValueOrDefault(component.Type) + 1;
                if (!string.IsNullOrWhiteSpace(component.Key))
                {
                    if (!componentKeys.TryAdd(component.Key, (component, componentPath)))
                        Issue("APPLY_KEY_DUPLICATE", $"Component key '{component.Key}' is duplicated.", componentPath + ".key");
                }
                foreach (var field in component.Fields ?? new Dictionary<string, JsonElement>())
                {
                    if (string.IsNullOrWhiteSpace(field.Key)) Issue("APPLY_MEMBER_NAME_MISSING", "Field names cannot be empty.", componentPath + ".fields");
                    ScanValue(field.Value, componentPath + ".fields." + field.Key);
                }
            }

            foreach (var (type, count) in typeCounts.Where(x => x.Value > 1))
            {
                if (nodeComponents!.Where(x => x.Type == type).Any(x => string.IsNullOrWhiteSpace(x.Key)))
                    Issue("APPLY_COMPONENT_KEY_REQUIRED", $"All {count} components of type '{type}' on one Slot require explicit keys.", path + ".components");
            }

            for (var i = 0; i < (children?.Count ?? 0); i++)
            {
                var child = children![i];
                if (child?.Slot is null) Issue("APPLY_CHILD_SLOT_MISSING", "Every child requires slot.", $"{path}.children[{i}].slot");
                else Visit(child.Slot, child.Components, child.Children, $"{path}.children[{i}]");
            }
        }

        if (document.Slot is not null) Visit(document.Slot, document.Components, document.Children, "$");

        foreach (var (component, componentPath) in componentPaths)
        {
            foreach (var field in component.Fields ?? new Dictionary<string, JsonElement>())
            {
                ValidateReferences(field.Value, componentKeys, issues, componentPath + ".fields." + field.Key);
            }
        }

        if (client is not null && issues.Count == 0)
        {
            var definitions = new Dictionary<string, ComponentTypeInfo>(StringComparer.Ordinal);
            foreach (var type in componentPaths.Select(x => x.Spec.Type).Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { definitions[type] = await client.DescribeComponentTypeAsync(resolvedTypes?.GetValueOrDefault(type) ?? type, cancellationToken); }
                catch (RLoopException ex)
                {
                    Issue(ex.Code, ex.Message, componentPaths.First(x => x.Spec.Type == type).Path + ".type");
                }
            }

            foreach (var (component, componentPath) in componentPaths)
            {
                if (!definitions.TryGetValue(component.Type, out var definition)) continue;
                var members = definition.Members.ToDictionary(x => x.Name, StringComparer.Ordinal);
                foreach (var field in component.Fields ?? new Dictionary<string, JsonElement>())
                {
                    if (!members.ContainsKey(field.Key))
                        Issue("COMPONENT_MEMBER_NOT_FOUND", $"Member '{field.Key}' does not exist on '{component.Type}'.", componentPath + ".fields." + field.Key);
                }
            }

            foreach (var (component, componentPath) in componentPaths)
            {
                foreach (var field in component.Fields ?? new Dictionary<string, JsonElement>())
                    await ValidateMemberReferencesStrict(field.Value, definitions, componentKeys, issues,
                        componentPath + ".fields." + field.Key, cancellationToken);
            }
        }

        return new ApplyValidationResult(issues.Count == 0, document.SchemaVersion, slots, components, references, strict, issues);
    }

    public static void ThrowIfInvalid(ApplyValidationResult result)
    {
        if (result.Valid) return;
        throw new RLoopException("APPLY_VALIDATION_FAILED", $"Apply document has {result.Issues.Count} validation error(s).",
            ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["issues"] = result.Issues });
    }

    private static void ValidateReferences(JsonElement value,
        IReadOnlyDictionary<string, (ApplyComponentSpec Spec, string Path)> keys,
        List<ApplyValidationIssue> issues, string path)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            var key = text.StartsWith("$ref:", StringComparison.Ordinal) ? text[5..] :
                text.StartsWith("$member:", StringComparison.Ordinal) ? MemberKey(text[8..]) : null;
            if (key is not null && !keys.ContainsKey(key))
                issues.Add(new ApplyValidationIssue("APPLY_REFERENCE_NOT_FOUND", $"Symbolic reference '{text}' has no declared component key.", path));
            return;
        }
        if (value.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var item in value.EnumerateArray()) ValidateReferences(item, keys, issues, $"{path}[{index++}]");
    }

    private static Task ValidateMemberReferencesStrict(JsonElement value,
        IReadOnlyDictionary<string, ComponentTypeInfo> definitions,
        IReadOnlyDictionary<string, (ApplyComponentSpec Spec, string Path)> keys,
        List<ApplyValidationIssue> issues, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.String && (value.GetString() ?? string.Empty).StartsWith("$member:", StringComparison.Ordinal))
        {
            var selector = value.GetString()![8..];
            var separator = selector.LastIndexOf('.');
            if (separator > 0 && keys.TryGetValue(selector[..separator], out var keyed) &&
                definitions.TryGetValue(keyed.Spec.Type, out var definition) &&
                !definition.Members.Any(x => x.Name == selector[(separator + 1)..]))
                issues.Add(new ApplyValidationIssue("APPLY_MEMBER_REFERENCE_NOT_FOUND", $"Member reference '{value.GetString()}' was not found.", path));
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateMemberReferencesStrict(item, definitions, keys, issues, $"{path}[{index++}]", cancellationToken).GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

    private static string MemberKey(string selector)
    {
        var separator = selector.LastIndexOf('.');
        return separator > 0 ? selector[..separator] : selector;
    }
}

internal sealed record ApplyStateSlot(string Id, string Path);
internal sealed record ApplyStateComponent(string Id, string SlotKey, string Type, int TypeOrdinal);

internal sealed class ApplyState
{
    public int SchemaVersion { get; set; } = 1;
    public string OwnershipKey { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public Dictionary<string, ApplyStateSlot> Slots { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ApplyStateComponent> Components { get; set; } = new(StringComparer.Ordinal);
}

internal static class ApplyStateStore
{
    public static string ResolvePath(ApplyDocument document, string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath)) return Path.GetFullPath(requestedPath);
        var sourceDirectory = document.SourcePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(document.SourcePath)!;
        var projectConfig = ConfigResolver.FindProjectConfigPath(sourceDirectory);
        var projectRoot = projectConfig is null ? sourceDirectory : Path.GetDirectoryName(projectConfig)!;
        var key = Sanitize(document.Ownership?.Key ?? "unowned");
        return Path.Combine(projectRoot, ".rloop", "state", key + ".json");
    }

    public static ApplyState Load(string path, string ownershipKey)
    {
        if (!File.Exists(path)) return new ApplyState { OwnershipKey = ownershipKey };
        try
        {
            var state = JsonSerializer.Deserialize<ApplyState>(File.ReadAllText(path), Options)
                        ?? throw new JsonException("State was empty.");
            if (state.SchemaVersion != 1)
                throw new RLoopException("APPLY_STATE_VERSION_UNSUPPORTED", $"State file '{path}' has unsupported schemaVersion {state.SchemaVersion}.", ExitCodes.ValidationFailed);
            if (!state.OwnershipKey.Equals(ownershipKey, StringComparison.Ordinal))
                throw new RLoopException("APPLY_STATE_OWNERSHIP_MISMATCH", $"State file '{path}' belongs to '{state.OwnershipKey}', not '{ownershipKey}'.", ExitCodes.ValidationFailed);
            state.Slots = new Dictionary<string, ApplyStateSlot>(state.Slots, StringComparer.Ordinal);
            state.Components = new Dictionary<string, ApplyStateComponent>(state.Components, StringComparer.Ordinal);
            return state;
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new RLoopException("APPLY_STATE_INVALID", $"Could not read state file '{path}': {ex.Message}", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex);
        }
    }

    public static void Save(string path, ApplyState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options) + "\n", new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RLoopException("APPLY_STATE_WRITE_FAILED", $"Could not write apply checkpoint '{path}': {ex.Message}", ExitCodes.OperationFailed,
                new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex);
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "apply" : safe;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

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
