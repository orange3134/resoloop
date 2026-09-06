namespace RLoop.Core;

public sealed record ItemAuditIssue(string Code, string Severity, string ComponentId, string ComponentType,
    string Member, string TargetId, string? TargetType, string Classification, string Message);

public sealed record ItemAuditReport(string RootId, string RootName, string? RootPath, bool Portable, bool Strict,
    bool HasGrabbable, int Slots, int Components, int Members, int References,
    int InternalReferences, int ExternalReferences, IReadOnlyList<ItemAuditIssue> Issues);

public static class ItemAuditService
{
    public static ItemAuditReport Audit(SlotInfo root, IReadOnlyCollection<string>? allowedExternalIds = null, bool strict = false,
        IReadOnlyCollection<string>? allowedExternalRoles = null)
    {
        allowedExternalIds ??= [];
        allowedExternalRoles ??= [];
        var slots = Flatten(root).ToArray();
        var components = slots.SelectMany(slot => slot.Components).ToArray();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots) owned.Add(slot.Id);
        foreach (var component in components)
        {
            owned.Add(component.Id);
            foreach (var member in component.Members ?? new Dictionary<string, MemberValue>()) CollectMemberIds(member.Value, owned);
        }

        var issues = new List<ItemAuditIssue>();
        var references = 0;
        var internalReferences = 0;
        var memberCount = 0;
        foreach (var component in components)
        {
            foreach (var member in component.Members ?? new Dictionary<string, MemberValue>())
                VisitMember(member.Value, member.Key, (value, path) =>
                {
                    memberCount++;
                    if (value.Kind != "reference" || string.IsNullOrWhiteSpace(value.TargetId)) return;
                    references++;
                    if (owned.Contains(value.TargetId)) { internalReferences++; return; }
                    var allowed = allowedExternalIds.Contains(value.TargetId, StringComparer.Ordinal);
                    var role = SimpleType(component.Type) + ":" + path;
                    var allowedRole = allowedExternalRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
                    var runtime = IsRuntimeContext(value.TargetType);
                    var protoFlux = component.Type.Contains("ProtoFlux", StringComparison.OrdinalIgnoreCase);
                    var classification = allowed ? "explicitly-allowed" : allowedRole ? "explicitly-allowed-role" : runtime ? "runtime-context" :
                        protoFlux ? "flux-external" : "required-world-element";
                    var severity = allowed || allowedRole ? "info" : runtime ? "warning" : "error";
                    var code = allowed ? "ITEM_EXTERNAL_REFERENCE_ALLOWED" : allowedRole ? "ITEM_EXTERNAL_ROLE_ALLOWED" : runtime ? "ITEM_RUNTIME_CONTEXT_REFERENCE" :
                        protoFlux ? "ITEM_FLUX_EXTERNAL_REFERENCE" : "ITEM_EXTERNAL_REFERENCE";
                    issues.Add(new ItemAuditIssue(code, severity, component.Id, component.Type, path, value.TargetId,
                        value.TargetType, classification, allowed
                            ? "External target is covered by an explicit allow-list entry."
                            : allowedRole ? $"External target role '{role}' is covered by an explicit stable allow-list entry."
                            : runtime ? "Reference depends on runtime context and must be reacquired after spawning."
                            : "Reference targets an element outside the saved item root and will not travel with the item."));
                });
        }

        var hasGrabbable = components.Any(component => SimpleType(component.Type) == "Grabbable");
        if (!hasGrabbable)
            issues.Add(new ItemAuditIssue("ITEM_GRABBABLE_MISSING", "warning", string.Empty, string.Empty,
                string.Empty, string.Empty, null, "item-root", "The audited root does not contain a Grabbable Component."));
        var portable = !issues.Any(issue => issue.Severity == "error" || strict && issue.Severity == "warning");
        return new ItemAuditReport(root.Id, root.Name, root.Path, portable, strict, hasGrabbable, slots.Length,
            components.Length, memberCount, references, internalReferences, references - internalReferences, issues);
    }

    private static IEnumerable<SlotInfo> Flatten(SlotInfo root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in Flatten(child)) yield return descendant;
    }

    private static void CollectMemberIds(MemberValue member, HashSet<string> ids)
    {
        if (!string.IsNullOrWhiteSpace(member.Id)) ids.Add(member.Id);
        foreach (var child in member.Members?.Values ?? []) CollectMemberIds(child, ids);
        foreach (var child in member.Elements ?? []) CollectMemberIds(child, ids);
    }

    private static void VisitMember(MemberValue member, string path, Action<MemberValue, string> visitor)
    {
        visitor(member, path);
        foreach (var child in member.Members ?? new Dictionary<string, MemberValue>())
            VisitMember(child.Value, path + "." + child.Key, visitor);
        for (var index = 0; index < (member.Elements?.Count ?? 0); index++)
            VisitMember(member.Elements![index], $"{path}[{index}]", visitor);
    }

    private static bool IsRuntimeContext(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var name = SimpleType(type);
        return name is "User" or "World" or "Session" or "CloudUser" or "LocalUser" or "UserRoot" ||
               name.EndsWith("User", StringComparison.Ordinal);
    }

    private static string SimpleType(string type)
    {
        var bracket = type.LastIndexOf(']');
        var value = bracket >= 0 ? type[(bracket + 1)..] : type;
        var dot = value.LastIndexOf('.');
        return dot >= 0 ? value[(dot + 1)..] : value;
    }
}
