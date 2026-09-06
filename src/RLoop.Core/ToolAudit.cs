using System.Numerics;

namespace RLoop.Core;

public sealed record ToolAuditIssue(string Code, string Severity, string Message, string? SlotId = null,
    string? ComponentId = null);

public sealed record GripPoseAlignment(string SlotId, string SlotName, string? SlotPath, string HandSide,
    Vector3Value Position, Vector3Value Forward, Vector3Value DirectionToTip, float Dot, bool Aligned);

public sealed record ToolAuditReport(string RootId, string RootName, string? RootPath, bool Valid,
    bool StructuralOnly, string? ToolComponentId, string? TipSlotId, string? TipSlotPath,
    float MinimumAlignmentDot, IReadOnlyList<GripPoseAlignment> GripPoses, IReadOnlyList<ToolAuditIssue> Issues);

public static class ToolAuditService
{
    public static ToolAuditReport Audit(SlotInfo root, float minimumAlignmentDot = 0.8f)
    {
        if (minimumAlignmentDot is < -1f or > 1f)
            throw new RLoopException("TOOL_AUDIT_THRESHOLD_INVALID", "Tool audit alignment threshold must be between -1 and 1.",
                ExitCodes.InvalidArguments);

        var slots = FlattenWithTransforms(root).ToArray();
        var issues = new List<ToolAuditIssue>();
        var tools = slots.SelectMany(entry => entry.Slot.Components.Select(component => (entry.Slot, Component: component)))
            .Where(entry => SimpleType(entry.Component.Type) == "RawDataTool").ToArray();
        if (tools.Length == 0)
            issues.Add(new ToolAuditIssue("TOOL_RAW_DATA_TOOL_MISSING", "error",
                "The item does not contain a RawDataTool Component."));
        else if (tools.Length > 1)
            issues.Add(new ToolAuditIssue("TOOL_RAW_DATA_TOOL_AMBIGUOUS", "error",
                "The item contains multiple RawDataTool Components; audit a narrower item root."));

        var tool = tools.Length == 1 ? tools[0] : default;
        var tipId = tool.Component?.Members?.FirstOrDefault(member =>
            member.Key.Equals("TipReference", StringComparison.OrdinalIgnoreCase)).Value?.TargetId;
        var tip = string.IsNullOrWhiteSpace(tipId) ? default : slots.FirstOrDefault(entry => entry.Slot.Id == tipId);
        if (tool.Component is not null && string.IsNullOrWhiteSpace(tipId))
            issues.Add(new ToolAuditIssue("TOOL_TIP_REFERENCE_MISSING", "error",
                "RawDataTool.TipReference is empty or was not included in the observation.", tool.Slot.Id, tool.Component.Id));
        else if (!string.IsNullOrWhiteSpace(tipId) && tip.Slot is null)
            issues.Add(new ToolAuditIssue("TOOL_TIP_OUTSIDE_ROOT", "error",
                "RawDataTool.TipReference targets a Slot outside the audited item root.", tool.Slot?.Id, tool.Component?.Id));

        var gripEntries = slots.SelectMany(entry => entry.Slot.Components.Select(component => (entry.Slot, entry.Transform, Component: component)))
            .Where(entry => SimpleType(entry.Component.Type) == "GripPoseReference").ToArray();
        if (gripEntries.Length == 0)
            issues.Add(new ToolAuditIssue("TOOL_GRIP_POSE_MISSING", "error",
                "The item does not contain any GripPoseReference Components."));

        var alignments = new List<GripPoseAlignment>();
        foreach (var grip in gripEntries)
        {
            var hand = ReadFieldText(grip.Component, "HandSide") ?? "unknown";
            if (hand == "unknown")
                issues.Add(new ToolAuditIssue("TOOL_GRIP_HAND_SIDE_MISSING", "error",
                    "GripPoseReference.HandSide is unavailable; re-inspect with member data.", grip.Slot.Id, grip.Component.Id));
            if (tip.Slot is null) continue;
            var position = Vector3.Transform(Vector3.Zero, grip.Transform);
            var tipPosition = Vector3.Transform(Vector3.Zero, tip.Transform);
            var forward = Vector3.TransformNormal(Vector3.UnitZ, grip.Transform);
            var towardTip = tipPosition - position;
            if (forward.LengthSquared() < 1e-10f || towardTip.LengthSquared() < 1e-10f)
            {
                issues.Add(new ToolAuditIssue("TOOL_GRIP_DIRECTION_DEGENERATE", "error",
                    "Grip forward or the vector toward the tip has zero length.", grip.Slot.Id, grip.Component.Id));
                continue;
            }
            forward = Vector3.Normalize(forward);
            towardTip = Vector3.Normalize(towardTip);
            var dot = Vector3.Dot(forward, towardTip);
            var aligned = dot >= minimumAlignmentDot;
            alignments.Add(new GripPoseAlignment(grip.Slot.Id, grip.Slot.Name, grip.Slot.Path, hand,
                Value(position), Value(forward), Value(towardTip), dot, aligned));
            if (!aligned)
                issues.Add(new ToolAuditIssue("TOOL_GRIP_FORWARD_MISMATCH", "error",
                    $"GripPose local +Z does not point toward RawDataTool.TipReference (dot={dot:F3}, required>={minimumAlignmentDot:F3}).",
                    grip.Slot.Id, grip.Component.Id));
        }

        foreach (var requiredHand in new[] { "Left", "Right" })
            if (!gripEntries.Any(grip => string.Equals(ReadFieldText(grip.Component, "HandSide"), requiredHand,
                    StringComparison.OrdinalIgnoreCase)))
                issues.Add(new ToolAuditIssue("TOOL_GRIP_HAND_MISSING", "error",
                    $"No GripPoseReference declares HandSide={requiredHand}."));

        return new ToolAuditReport(root.Id, root.Name, root.Path, !issues.Any(issue => issue.Severity == "error"),
            true, tool.Component?.Id, tip.Slot?.Id, tip.Slot?.Path, minimumAlignmentDot, alignments, issues);
    }

    private static IEnumerable<(SlotInfo Slot, Matrix4x4 Transform)> FlattenWithTransforms(SlotInfo root)
    {
        var rootTransform = Matrix4x4.Identity;
        foreach (var entry in Visit(root, rootTransform)) yield return entry;

        static IEnumerable<(SlotInfo Slot, Matrix4x4 Transform)> Visit(SlotInfo slot, Matrix4x4 transform)
        {
            yield return (slot, transform);
            foreach (var child in slot.Children)
            {
                var childTransform = LocalTransform(child) * transform;
                foreach (var descendant in Visit(child, childTransform)) yield return descendant;
            }
        }
    }

    private static Matrix4x4 LocalTransform(SlotInfo slot)
    {
        var position = slot.Position ?? new Vector3Value(0, 0, 0);
        var rotation = slot.Rotation ?? new QuaternionValue(0, 0, 0, 1);
        var scale = slot.Scale ?? new Vector3Value(1, 1, 1);
        return Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z) *
               Matrix4x4.CreateFromQuaternion(new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W)) *
               Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
    }

    private static string? ReadFieldText(ComponentSummary component, string name)
    {
        var member = component.Members?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        if (member?.Value is null) return null;
        if (member.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return member.Value.ToJsonString().Trim('"');
    }

    private static Vector3Value Value(Vector3 value) => new(value.X, value.Y, value.Z);

    private static string SimpleType(string type)
    {
        var bracket = type.LastIndexOf(']');
        var value = bracket >= 0 ? type[(bracket + 1)..] : type;
        var dot = value.LastIndexOf('.');
        return dot >= 0 ? value[(dot + 1)..] : value;
    }
}
