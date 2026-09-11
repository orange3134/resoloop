namespace RLoop.Core;

public sealed record ItemAuditIssue(string Code, string Severity, string ComponentId, string ComponentType,
    string Member, string TargetId, string? TargetType, string Classification, string Message,
    string? Role = null, string? MatchedAllowEntry = null);

public sealed record ItemAuditReport(string RootId, string RootName, string? RootPath, bool Portable, bool Strict,
    bool HasGrabbable, int Slots, int Components, int Members, int References,
    int InternalReferences, int ExternalReferences, IReadOnlyList<ItemAuditIssue> Issues,
    IReadOnlyList<string>? ExternalRoleCandidates = null, IReadOnlyList<string>? UnmatchedExternalRoles = null);

public static class ItemAuditService
{
    public static ItemAuditReport Audit(SlotInfo root, IReadOnlyCollection<string>? allowedExternalIds = null, bool strict = false,
        IReadOnlyCollection<string>? allowedExternalRoles = null)
    {
        allowedExternalIds ??= [];
        allowedExternalRoles ??= [];
        foreach (var entry in allowedExternalRoles)
            if (entry.LastIndexOf(':') is var split && (split <= 0 || split == entry.Length - 1))
                throw new RLoopException("ITEM_ALLOW_ROLE_INVALID", $"Invalid role '{entry}'; use COMPONENT_TYPE:MEMBER_PATH or COMPONENT_ID:MEMBER_PATH.",
                    ExitCodes.InvalidArguments, suggestions: ["Inspect externalRoleCandidates in an unfiltered audit; review the target before allowing its role."]);
        var matchedRoles = new HashSet<string>(StringComparer.Ordinal);
        var slots = Flatten(root).ToArray();
        var components = slots.SelectMany(slot => slot.Components).ToArray();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            owned.Add(slot.Id);
            foreach (var member in slot.Members?.Values ?? []) CollectMemberIds(member, owned);
        }
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
                    var role = component.Type + ":" + path;
                    var roleEntries = allowedExternalRoles.Where(entry => MatchesRole(entry, component, path)).ToArray();
                    foreach (var entry in roleEntries) matchedRoles.Add(entry);
                    var allowedRole = roleEntries.Length > 0;
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
                            : allowedRole ? $"External target role '{role}' is covered by explicit allow-list entry '{roleEntries[0]}'."
                            : runtime ? "Reference depends on runtime context and must be reacquired after spawning."
                            : "Reference targets an element outside the saved item root and will not travel with the item.",
                        role, roleEntries.FirstOrDefault()));
                });
        }

        var candidates = issues.Select(issue => issue.Role!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unmatched = allowedExternalRoles.Where(entry => !matchedRoles.Contains(entry)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var entry in unmatched)
            issues.Add(new ItemAuditIssue("ITEM_ALLOW_ROLE_UNUSED", "warning", "", "", "", "", null, "audit-policy",
                $"Allow entry '{entry}' matched no external reference. Inspect externalRoleCandidates; type roles apply to all matching components, ID roles only to that session component."));
        var hasGrabbable = components.Any(component => SimpleType(component.Type) == "Grabbable");
        if (!hasGrabbable)
            issues.Add(new ItemAuditIssue("ITEM_GRABBABLE_MISSING", "warning", string.Empty, string.Empty,
                string.Empty, string.Empty, null, "item-root", "The audited root does not contain a Grabbable Component."));
        var portable = !issues.Any(issue => issue.Severity == "error" || strict && issue.Severity == "warning");
        return new ItemAuditReport(root.Id, root.Name, root.Path, portable, strict, hasGrabbable, slots.Length,
            components.Length, memberCount, references, internalReferences, references - internalReferences, issues, candidates, unmatched);
    }

    private static bool MatchesRole(string entry, ComponentSummary component, string memberPath)
    {
        var split = entry.LastIndexOf(':');
        var selector = entry[..split];
        if (!entry[(split + 1)..].Equals(memberPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (selector.Equals(component.Id, StringComparison.Ordinal)) return true;
        if (selector.StartsWith('[')) return selector.Equals(component.Type, StringComparison.OrdinalIgnoreCase);
        var type = component.Type.StartsWith('[') && component.Type.IndexOf(']') is var end && end >= 0
            ? component.Type[(end + 1)..] : component.Type;
        return selector.Equals(type, StringComparison.OrdinalIgnoreCase) ||
               !selector.Contains('.') && selector.Equals(SimpleType(component.Type), StringComparison.OrdinalIgnoreCase);
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
