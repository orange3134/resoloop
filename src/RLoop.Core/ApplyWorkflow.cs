using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RLoop.Core;

public sealed record ApplyOwnershipSpec(string Key);

public sealed record ApplyDocument(
    string? SchemaVersion,
    ApplyOwnershipSpec? Ownership,
    ApplySlotSpec? Slot,
    IReadOnlyList<ApplyComponentSpec>? Components,
    IReadOnlyList<ApplyNodeSpec>? Children = null,
    IReadOnlyDictionary<string, ApplyAssetSpec>? Assets = null,
    IReadOnlyDictionary<string, ApplyCameraSpec>? Cameras = null,
    IReadOnlyList<ApplyTestSpec>? Tests = null)
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
            var expanded = ApplyDocumentCompiler.Compile(fullPath);
            return (JsonSerializer.Deserialize<ApplyDocument>(expanded.Json, JsonOptions)
                    ?? throw new JsonException("Document was empty.")) with
            {
                SourcePath = fullPath,
                Compilation = expanded.Summary
            };
        }
        catch (JsonException ex)
        {
            var unknown = Regex.Match(ex.Message, @"property '([^']+)'", RegexOptions.IgnoreCase).Groups[1].Value;
            var suggestion = string.IsNullOrWhiteSpace(unknown) ? null : KnownProperties
                .OrderBy(candidate => EditDistance(unknown, candidate)).ThenBy(candidate => candidate, StringComparer.Ordinal)
                .FirstOrDefault();
            throw new RLoopException("APPLY_DOCUMENT_INVALID", $"Invalid apply document: {ex.Message}",
                ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["jsonPath"] = ex.Path, ["unknownProperty"] = string.IsNullOrWhiteSpace(unknown) ? null : unknown },
                suggestion is null ? null : [$"Did you mean '{suggestion}'? Unknown properties are rejected to prevent silent no-ops."], ex);
        }
    }

    [JsonIgnore]
    public ApplyCompilationSummary? Compilation { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static int EditDistance(string left, string right)
    {
        var costs = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var previous = costs[0];
            costs[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var saved = costs[j];
                costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), previous +
                    (char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1));
                previous = saved;
            }
        }
        return costs[^1];
    }

    private static readonly string[] KnownProperties =
    [
        "schemaVersion", "ownership", "key", "slot", "parent", "name", "position", "rotation", "scale",
        "managedFields", "preserveWorldTransform", "migrateFrom", "components", "children", "type", "fields",
        "initialFields", "identityFields", "assets", "cameras", "tests", "assertions", "probe", "arguments",
        "method", "kind", "target", "value", "restore", "safe", "expected", "exists", "phase", "componentType",
        "count", "delta", "timeoutMs", "pollMs"
    ];
}

public sealed record ApplyCompilationSummary(
    int SourceFiles,
    int Prototypes,
    int Instances,
    int RepeatedNodes,
    int ExpandedNodes,
    long ExpandedBytes);

public sealed record ApplyAssetSpec(string Kind, string Source, IReadOnlyDictionary<string, JsonElement>? Options = null);

public sealed record ApplyCameraSpec(
    float[] Position,
    float[] Target,
    float FieldOfView = 60,
    int Width = 1280,
    int Height = 720,
    string? Output = null,
    bool Representative = false);

public sealed record ApplyTestSpec(
    string Name,
    IReadOnlyList<ApplyAssertionSpec>? Assertions,
    ApplyProbeSpec? Probe = null,
    int TimeoutMs = 2000,
    int PollMs = 100);

public sealed record ApplyAssertionSpec(
    string Target,
    JsonElement? Expected = null,
    bool? Exists = null,
    string? Phase = null,
    string Kind = "member",
    string? Name = null,
    string? ComponentType = null,
    int? Count = null,
    int? Delta = null);

public sealed record ApplyProbeSpec(
    string Target,
    string? Method = null,
    IReadOnlyDictionary<string, JsonElement>? Arguments = null,
    bool Safe = false,
    string Kind = "method",
    JsonElement? Value = null,
    bool Restore = true);

public sealed record ApplySlotSpec(
    string Name,
    string? Parent,
    float[]? Position,
    float[]? Rotation,
    float[]? Scale,
    string? Key = null,
    IReadOnlyList<string>? ManagedFields = null,
    bool PreserveWorldTransform = false,
    string? MigrateFrom = null);

public sealed record ApplyComponentSpec(
    string Type,
    IReadOnlyDictionary<string, JsonElement>? Fields,
    string? Key = null,
    string? MigrateFrom = null,
    IReadOnlyDictionary<string, JsonElement>? InitialFields = null,
    IReadOnlyList<string>? IdentityFields = null);

public sealed record ApplyNodeSpec(
    ApplySlotSpec Slot,
    IReadOnlyList<ApplyComponentSpec>? Components,
    IReadOnlyList<ApplyNodeSpec>? Children = null);

public sealed record ApplyOptions(
    string? StateFile = null,
    bool Adopt = false,
    bool Profile = false,
    Action<ApplyProgress>? Progress = null,
    bool Prune = false,
    bool ConfirmDeletes = false);

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
        var slotMigrations = new Dictionary<string, (string NewKey, string Path)>(StringComparer.Ordinal);
        var componentMigrations = new Dictionary<string, (string NewKey, string Path)>(StringComparer.Ordinal);
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

        foreach (var asset in document.Assets ?? new Dictionary<string, ApplyAssetSpec>())
        {
            var path = "$.assets." + asset.Key;
            if (string.IsNullOrWhiteSpace(asset.Value.Kind)) Issue("ASSET_KIND_MISSING", "Asset kind is required.", path + ".kind");
            if (string.IsNullOrWhiteSpace(asset.Value.Source)) { Issue("ASSET_SOURCE_MISSING", "Asset source is required.", path + ".source"); continue; }
            var hasAbsoluteUri = Uri.TryCreate(asset.Value.Source, UriKind.Absolute, out var uri);
            if (Path.IsPathFullyQualified(asset.Value.Source) || !hasAbsoluteUri || uri!.Scheme == Uri.UriSchemeFile)
            {
                var kind = asset.Value.Kind.ToLowerInvariant();
                if (kind is not ("texture" or "texture2d" or "audio" or "audioclip" or "mesh"))
                    Issue("ASSET_KIND_UNSUPPORTED", $"Local asset kind '{asset.Value.Kind}' is not supported.", path + ".kind");
                var baseDirectory = Path.GetDirectoryName(document.SourcePath) ?? Environment.CurrentDirectory;
                var sourcePath = uri?.Scheme == Uri.UriSchemeFile ? uri.LocalPath : Path.GetFullPath(asset.Value.Source, baseDirectory);
                if (!File.Exists(sourcePath)) Issue("ASSET_SOURCE_NOT_FOUND", $"Asset source '{sourcePath}' does not exist.", path + ".source");
            }
        }

        foreach (var camera in document.Cameras ?? new Dictionary<string, ApplyCameraSpec>())
        {
            var path = "$.cameras." + camera.Key;
            if (camera.Value.Position.Length != 3 || camera.Value.Target.Length != 3)
                Issue("CAPTURE_CAMERA_INVALID", "Camera position and target require three numbers.", path);
            if (camera.Value.Width is < 64 or > 8192 || camera.Value.Height is < 64 or > 8192)
                Issue("CAPTURE_RESOLUTION_INVALID", "Camera width and height must be between 64 and 8192.", path);
        }

        for (var i = 0; i < (document.Tests?.Count ?? 0); i++)
        {
            var test = document.Tests![i];
            var path = $"$.tests[{i}]";
            if (string.IsNullOrWhiteSpace(test.Name)) Issue("APPLY_TEST_NAME_MISSING", "Test name is required.", path + ".name");
            if (test.Assertions is null || test.Assertions.Count == 0) Issue("APPLY_TEST_ASSERTIONS_MISSING", "A test requires at least one assertion.", path + ".assertions");
            foreach (var assertion in test.Assertions ?? [])
            {
                if (string.IsNullOrWhiteSpace(assertion.Target)) Issue("APPLY_ASSERTION_TARGET_MISSING", "An assertion requires target.", path + ".assertions");
                if (assertion.Kind.Equals("child-count", StringComparison.OrdinalIgnoreCase))
                {
                    if (!assertion.Target.StartsWith("$slot:", StringComparison.Ordinal))
                        Issue("APPLY_ASSERTION_SLOT_TARGET_REQUIRED", "A child-count assertion target must use $slot:key.", path + ".assertions");
                    if (assertion.Count is null && assertion.Delta is null && assertion.Expected is null)
                        Issue("APPLY_ASSERTION_COUNT_MISSING", "A child-count assertion requires count, delta, or expected.", path + ".assertions");
                }
                else if (!assertion.Kind.Equals("member", StringComparison.OrdinalIgnoreCase))
                    Issue("APPLY_ASSERTION_KIND_UNSUPPORTED", "Assertion kind must be 'member' or 'child-count'.", path + ".assertions");
            }
            if (test.Probe is not { } probe) continue;
            if (!probe.Safe) Issue("UNSAFE_PROBE_REJECTED", "A probe must declare safe=true.", path + ".probe.safe");
            switch (probe.Kind?.ToLowerInvariant())
            {
                case "method":
                    if (string.IsNullOrWhiteSpace(probe.Method))
                        Issue("PROBE_METHOD_MISSING", "A method probe requires method.", path + ".probe.method");
                    break;
                case "set-member":
                    if (!(probe.Target.StartsWith("$component:", StringComparison.Ordinal) ||
                          probe.Target.StartsWith("$member:", StringComparison.Ordinal)) ||
                        probe.Target[(probe.Target.IndexOf(':') + 1)..].LastIndexOf('.') <= 0)
                        Issue("PROBE_MEMBER_TARGET_REQUIRED",
                            "A set-member probe target must use $component:key.MemberName or $member:key.MemberName.", path + ".probe.target");
                    if (probe.Value is null) Issue("PROBE_VALUE_MISSING", "A set-member probe requires value.", path + ".probe.value");
                    if (!probe.Restore) Issue("PROBE_RESTORE_REQUIRED", "A set-member probe requires restore=true.", path + ".probe.restore");
                    break;
                default:
                    Issue("PROBE_KIND_UNSUPPORTED", "Probe kind must be 'method' or 'set-member'.", path + ".probe.kind");
                    break;
            }
        }

        void ScanValue(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? string.Empty;
                if (text.StartsWith("$ref:", StringComparison.Ordinal) || text.StartsWith("$component:", StringComparison.Ordinal) ||
                    text.StartsWith("$slot:", StringComparison.Ordinal) || text.StartsWith("$asset:", StringComparison.Ordinal))
                {
                    references++;
                    var key = text[(text.IndexOf(':') + 1)..];
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
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) ScanValue(property.Value, path + "." + property.Name);
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
            foreach (var field in slot.ManagedFields ?? [])
                if (field is not ("position" or "rotation" or "scale"))
                    Issue("APPLY_MANAGED_FIELD_INVALID", $"Managed Slot field '{field}' is not supported.", path + ".slot.managedFields");
            if (!string.IsNullOrWhiteSpace(slot.MigrateFrom))
            {
                if (string.IsNullOrWhiteSpace(slot.Key))
                    Issue("APPLY_MIGRATION_KEY_REQUIRED", "slot.migrateFrom requires an explicit slot.key.", path + ".slot.migrateFrom");
                else if (slot.MigrateFrom == slot.Key)
                    Issue("APPLY_MIGRATION_SELF_REFERENCE", "slot.migrateFrom must differ from slot.key.", path + ".slot.migrateFrom");
                else if (!slotMigrations.TryAdd(slot.MigrateFrom, (slot.Key, path)))
                    Issue("APPLY_MIGRATION_SOURCE_DUPLICATE", $"Stable Slot key '{slot.MigrateFrom}' is used by multiple migrations.", path + ".slot.migrateFrom");
            }

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
                {
                    typeCounts[component.Type] = typeCounts.GetValueOrDefault(component.Type) + 1;
                    if (!GenericTypeSyntaxValid(component.Type))
                        Issue("APPLY_GENERIC_TYPE_INVALID", $"Generic component type '{component.Type}' has unbalanced type arguments.", componentPath + ".type");
                }
                if (!string.IsNullOrWhiteSpace(component.Key))
                {
                    if (!componentKeys.TryAdd(component.Key, (component, componentPath)))
                        Issue("APPLY_KEY_DUPLICATE", $"Component key '{component.Key}' is duplicated.", componentPath + ".key");
                }
                if (!string.IsNullOrWhiteSpace(component.MigrateFrom))
                {
                    if (string.IsNullOrWhiteSpace(component.Key))
                        Issue("APPLY_MIGRATION_KEY_REQUIRED", "component.migrateFrom requires an explicit component.key.", componentPath + ".migrateFrom");
                    else if (component.MigrateFrom == component.Key)
                        Issue("APPLY_MIGRATION_SELF_REFERENCE", "component.migrateFrom must differ from component.key.", componentPath + ".migrateFrom");
                    else if (!componentMigrations.TryAdd(component.MigrateFrom, (component.Key, componentPath)))
                        Issue("APPLY_MIGRATION_SOURCE_DUPLICATE", $"Stable Component key '{component.MigrateFrom}' is used by multiple migrations.", componentPath + ".migrateFrom");
                }
                var duplicateInitial = (component.InitialFields?.Keys ?? []).Intersect(component.Fields?.Keys ?? [], StringComparer.Ordinal).ToArray();
                if (duplicateInitial.Length > 0)
                    Issue("APPLY_COMPONENT_FIELD_POLICY_CONFLICT", $"Fields cannot be both managed and initial-only: {string.Join(", ", duplicateInitial)}.", componentPath);
                var availableIdentityFields = (component.Fields?.Keys ?? []).Concat(component.InitialFields?.Keys ?? [])
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var identityField in component.IdentityFields ?? [])
                {
                    if (string.IsNullOrWhiteSpace(identityField))
                        Issue("APPLY_IDENTITY_FIELD_INVALID", "identityFields cannot contain an empty member name.", componentPath + ".identityFields");
                    else if (!availableIdentityFields.Contains(identityField))
                        Issue("APPLY_IDENTITY_FIELD_UNMANAGED",
                            $"Identity field '{identityField}' must also be declared in fields or initialFields.", componentPath + ".identityFields");
                }
                foreach (var field in EnumerateComponentFields(component))
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

        foreach (var migration in slotMigrations.Where(migration => slotKeys.Contains(migration.Key)))
            Issue("APPLY_MIGRATION_SOURCE_DECLARED",
                $"Slot key '{migration.Key}' cannot be both declared and used as migrateFrom.", migration.Value.Path + ".slot.migrateFrom");
        foreach (var migration in componentMigrations.Where(migration => componentKeys.ContainsKey(migration.Key)))
            Issue("APPLY_MIGRATION_SOURCE_DECLARED",
                $"Component key '{migration.Key}' cannot be both declared and used as migrateFrom.", migration.Value.Path + ".migrateFrom");

        foreach (var (component, componentPath) in componentPaths)
        {
            foreach (var field in EnumerateComponentFields(component))
            {
                ValidateReferences(field.Value, componentKeys, slotKeys, document.Assets?.Keys.ToHashSet(StringComparer.Ordinal) ?? [],
                    issues, componentPath + ".fields." + field.Key);
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
                foreach (var field in EnumerateComponentFields(component))
                {
                    if (!members.TryGetValue(field.Key, out var member))
                        Issue("COMPONENT_MEMBER_NOT_FOUND", $"Member '{field.Key}' does not exist on '{component.Type}'.", componentPath + ".fields." + field.Key);
                    else if (member.Kind == "field" && MemberValueSyntax.IsStructuredTuple(member.ValueType, out _))
                    {
                        var raw = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() ?? string.Empty : field.Value.GetRawText();
                        if (!raw.StartsWith('$'))
                        {
                            try { _ = MemberValueSyntax.ParseTuple(member.ValueType, raw); }
                            catch (RLoopException ex) { issues.Add(new ApplyValidationIssue(ex.Code, ex.Message, componentPath + ".fields." + field.Key)); }
                        }
                    }
                }
            }

            foreach (var (component, componentPath) in componentPaths)
            {
                foreach (var field in EnumerateComponentFields(component))
                    await ValidateMemberReferencesStrict(field.Value, definitions, componentKeys, issues,
                        componentPath + ".fields." + field.Key, cancellationToken);
            }
        }

        return new ApplyValidationResult(issues.Count == 0, document.SchemaVersion, slots, components, references, strict, issues);
    }

    private static IEnumerable<KeyValuePair<string, JsonElement>> EnumerateComponentFields(ApplyComponentSpec component) =>
        (component.Fields ?? new Dictionary<string, JsonElement>()).Concat(component.InitialFields ?? new Dictionary<string, JsonElement>());

    public static void ThrowIfInvalid(ApplyValidationResult result)
    {
        if (result.Valid) return;
        throw new RLoopException("APPLY_VALIDATION_FAILED", $"Apply document has {result.Issues.Count} validation error(s).",
            ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["issues"] = result.Issues });
    }

    private static void ValidateReferences(JsonElement value,
        IReadOnlyDictionary<string, (ApplyComponentSpec Spec, string Path)> keys,
        IReadOnlySet<string> slotKeys,
        IReadOnlySet<string> assetKeys,
        List<ApplyValidationIssue> issues, string path)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            var key = text.StartsWith("$ref:", StringComparison.Ordinal) ? text[5..] :
                text.StartsWith("$component:", StringComparison.Ordinal) ? text[11..] :
                text.StartsWith("$member:", StringComparison.Ordinal) ? MemberKey(text[8..]) : null;
            if (key is not null && !keys.ContainsKey(key))
                issues.Add(new ApplyValidationIssue("APPLY_REFERENCE_NOT_FOUND", $"Symbolic reference '{text}' has no declared component key.", path));
            if (text.StartsWith("$slot:", StringComparison.Ordinal) && !slotKeys.Contains(text[6..]))
                issues.Add(new ApplyValidationIssue("APPLY_REFERENCE_NOT_FOUND", $"Symbolic reference '{text}' has no declared slot key.", path));
            if (text.StartsWith("$asset:", StringComparison.Ordinal) && !assetKeys.Contains(text[7..]))
                issues.Add(new ApplyValidationIssue("APPLY_REFERENCE_NOT_FOUND", $"Symbolic reference '{text}' has no declared asset key.", path));
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateReferences(item, keys, slotKeys, assetKeys, issues, $"{path}[{index++}]");
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) ValidateReferences(property.Value, keys, slotKeys, assetKeys, issues, path + "." + property.Name);
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
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                ValidateMemberReferencesStrict(property.Value, definitions, keys, issues, path + "." + property.Name, cancellationToken).GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    private static string MemberKey(string selector)
    {
        var separator = selector.LastIndexOf('.');
        return separator > 0 ? selector[..separator] : selector;
    }

    private static bool GenericTypeSyntaxValid(string type)
    {
        var angle = 0;
        var square = 0;
        foreach (var character in type)
        {
            if (character == '<') angle++;
            else if (character == '>' && --angle < 0) return false;
            else if (character == '[') square++;
            else if (character == ']' && --square < 0) return false;
        }
        return angle == 0 && square == 0 && !type.EndsWith("`1", StringComparison.Ordinal);
    }
}

internal sealed record ApplyStateSlot(string Id, string Path);
internal sealed record ApplyStateComponent(string Id, string SlotKey, string Type, int TypeOrdinal,
    int? ComponentIndex = null, IReadOnlyList<string>? MemberNames = null,
    IReadOnlyDictionary<string, string>? IdentityValues = null);
internal sealed record ApplyStateAsset(string Kind, string SourceHash, string Url);

internal sealed class ApplyState
{
    public int SchemaVersion { get; set; } = 1;
    public string OwnershipKey { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public Dictionary<string, ApplyStateSlot> Slots { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ApplyStateComponent> Components { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ApplyStateAsset> Assets { get; set; } = new(StringComparer.Ordinal);
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
            state.Assets = new Dictionary<string, ApplyStateAsset>(state.Assets ?? [], StringComparer.Ordinal);
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
