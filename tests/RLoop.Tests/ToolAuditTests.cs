using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ToolAuditTests
{
    [Fact]
    public void AcceptsLeftAndRightGripPosesWhoseLocalZPointsAtTip()
    {
        var report = ToolAuditService.Audit(ToolRoot(new QuaternionValue(0, 0, 0, 1)));

        Assert.True(report.Valid);
        Assert.True(report.StructuralOnly);
        Assert.Equal(2, report.GripPoses.Count);
        Assert.All(report.GripPoses, grip => Assert.True(grip.Aligned));
    }

    [Fact]
    public void RejectsGripPoseWhoseLocalZPointsAwayFromTip()
    {
        var report = ToolAuditService.Audit(ToolRoot(new QuaternionValue(0, 1, 0, 0)));

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "TOOL_GRIP_FORWARD_MISMATCH");
    }

    private static SlotInfo ToolRoot(QuaternionValue leftRotation)
    {
        var tool = new ComponentSummary("C_Tool", "FrooxEngine.RawDataTool", new Dictionary<string, MemberValue>
        {
            ["TipReference"] = new("reference", "M_Tip", TargetId: "S_Tip", TargetType: "FrooxEngine.Slot")
        });
        return new SlotInfo("S_Root", "Tool", "Root", null, null, null, true, true, null, false, [tool],
        [
            Pose("S_Left", "LeftGrip", "Left", leftRotation),
            Pose("S_Right", "RightGrip", "Right", new QuaternionValue(0, 0, 0, 1)),
            new SlotInfo("S_Tip", "Muzzle", "S_Root", new Vector3Value(0, 0, 1), null, null,
                true, true, null, false, [], [], "Root/Tool/Muzzle")
        ], "Root/Tool");
    }

    private static SlotInfo Pose(string id, string name, string hand, QuaternionValue rotation) =>
        new(id, name, "S_Root", new Vector3Value(0, 0, 0), rotation, null, true, true, null, false,
        [
            new ComponentSummary("C_" + hand, "FrooxEngine.GripPoseReference", new Dictionary<string, MemberValue>
            {
                ["HandSide"] = new("field", "M_" + hand, "FrooxEngine.Chirality", JsonValue.Create(hand))
            })
        ], [], "Root/Tool/" + name);
}
