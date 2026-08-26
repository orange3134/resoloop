using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed record Vector3Value(float X, float Y, float Z)
{
    public static Vector3Value Parse(string text, string optionName)
    {
        var values = NumberList.Parse(text, 3, optionName);
        return new Vector3Value(values[0], values[1], values[2]);
    }
}

public sealed record QuaternionValue(float X, float Y, float Z, float W)
{
    public static QuaternionValue Parse(string text, string optionName)
    {
        var values = NumberList.Parse(text, 4, optionName);
        return new QuaternionValue(values[0], values[1], values[2], values[3]);
    }
}

internal static class NumberList
{
    public static float[] Parse(string text, int expected, string optionName)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != expected || parts.Any(p => !float.TryParse(p, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            throw new RLoopException(
                "INVALID_VECTOR",
                $"{optionName} expects {expected} comma-separated numbers, but received '{text}'.",
                ExitCodes.InvalidArguments,
                suggestions: [$"Use {optionName} {string.Join(',', Enumerable.Range(0, expected).Select(_ => "0"))}"]);
        }

        return parts.Select(p => float.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }
}

public sealed record SessionInfo(
    string Url,
    bool Connected,
    string? ResoniteVersion,
    string? ResoniteLinkVersion,
    string? UniqueSessionId);

public sealed record ComponentSummary(
    string Id,
    string Type,
    IReadOnlyDictionary<string, MemberValue>? Members = null);

public sealed record MemberValue(
    string Kind,
    string? Id = null,
    string? Type = null,
    JsonNode? Value = null,
    string? TargetId = null,
    string? TargetType = null,
    IReadOnlyDictionary<string, MemberValue>? Members = null,
    IReadOnlyList<MemberValue>? Elements = null);

public sealed record ComponentInfo(
    string Id,
    string Type,
    IReadOnlyDictionary<string, MemberValue> Members);

public sealed record SlotInfo(
    string Id,
    string Name,
    string? ParentId,
    Vector3Value? Position,
    QuaternionValue? Rotation,
    Vector3Value? Scale,
    bool? IsActive,
    bool? IsPersistent,
    string? Tag,
    bool IsReferenceOnly,
    IReadOnlyList<ComponentSummary> Components,
    IReadOnlyList<SlotInfo> Children,
    string? Path = null);

public sealed record SlotMatch(
    string Id,
    string Name,
    string Path,
    IReadOnlyList<ComponentSummary> Components);

public sealed record SlotCreateRequest(
    string ParentId,
    string Name,
    Vector3Value? Position = null,
    QuaternionValue? Rotation = null,
    Vector3Value? Scale = null,
    string? RequestedId = null);

public sealed record SlotUpdateRequest(
    string Id,
    string? Name = null,
    Vector3Value? Position = null,
    QuaternionValue? Rotation = null,
    Vector3Value? Scale = null);

public sealed record MemberDefinitionInfo(
    string Name,
    string Kind,
    string? MemberType,
    string? ValueType,
    string? TargetType);

public sealed record ComponentTypeInfo(
    string FullTypeName,
    string? CategoryPath,
    string? BaseType,
    bool IsGeneric,
    IReadOnlyList<MemberDefinitionInfo> Members);

public sealed record TypeInfo(
    string FullTypeName,
    string? AssemblyName,
    string? Namespace,
    string Name,
    string? BaseType,
    bool IsAbstract,
    bool IsInterface,
    bool IsGeneric,
    bool IsEnum,
    bool IsComponent,
    bool IsSyncObject,
    bool IsWorldElement,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> Interfaces,
    IReadOnlyDictionary<string, long>? EnumValues = null,
    bool? IsFlags = null);

public sealed record ComponentCreateResult(string Id, string Type);

public sealed record ApplyResult(
    string SlotId,
    bool Created,
    int ComponentsAdded,
    int ComponentsUpdated,
    int SlotsCreated = 0,
    int SlotsUpdated = 0,
    int SlotsUnchanged = 0,
    int ComponentsUnchanged = 0,
    string? StateFile = null,
    string? SessionId = null,
    ApplyProfile? Profile = null);

public sealed record ClientOperationMetric(string Operation, int Requests, double ElapsedMs);

public sealed record ClientMetrics(
    int Requests,
    int CacheHits,
    double ElapsedMs,
    IReadOnlyList<ClientOperationMetric> Operations);

public sealed record ApplyProfile(
    double TotalElapsedMs,
    ClientMetrics Client,
    int PlannedOperations,
    int Mutations,
    int NoOps);

public sealed record ApplyProgress(
    string Stage,
    int Current,
    int Total,
    string? Path,
    string Message);

public sealed record ApplyPlanEntry(
    string Action,
    string Kind,
    string Path,
    string? Key = null,
    string? Type = null,
    IReadOnlyList<string>? Members = null,
    string? Reason = null);

public sealed record ApplyPlanResult(
    bool Valid,
    string SchemaVersion,
    string OwnershipKey,
    string StateFile,
    string? SessionId,
    IReadOnlyList<ApplyPlanEntry> Operations,
    int Creates,
    int Updates,
    int NoOps);

public sealed record ApplyValidationIssue(
    string Code,
    string Message,
    string Path,
    string Severity = "error");

public sealed record ApplyValidationResult(
    bool Valid,
    string? SchemaVersion,
    int Slots,
    int Components,
    int References,
    bool Strict,
    IReadOnlyList<ApplyValidationIssue> Issues);
