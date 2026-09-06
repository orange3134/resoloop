using RLoop.Core;

namespace RLoop.Tests;

public sealed class ItemAuditTests
{
    [Fact]
    public void AuditAcceptsClosedReferencesInsideGrabbableRoot()
    {
        var target = new ComponentSummary("C_Target", "FrooxEngine.Grabbable", new Dictionary<string, MemberValue>
        {
            ["Enabled"] = new("field", "M_Enabled", "bool")
        });
        var source = new ComponentSummary("C_Source", "Test.ReferenceHolder", new Dictionary<string, MemberValue>
        {
            ["Target"] = new("reference", "M_Target", TargetId: "C_Target", TargetType: "FrooxEngine.Grabbable")
        });
        var root = Slot("S_Root", [target, source]);

        var report = ItemAuditService.Audit(root);

        Assert.True(report.Portable);
        Assert.True(report.HasGrabbable);
        Assert.Equal(1, report.InternalReferences);
        Assert.Equal(0, report.ExternalReferences);
    }

    [Fact]
    public void AuditRejectsFluxReferenceOutsideSavedRoot()
    {
        var root = Slot("S_Root", [
            new ComponentSummary("C_Grab", "FrooxEngine.Grabbable", new Dictionary<string, MemberValue>()),
            new ComponentSummary("C_Flux", "FrooxEngine.ProtoFlux.DynamicImpulseReceiver", new Dictionary<string, MemberValue>
            {
                ["Target"] = new("reference", "M_Target", TargetId: "C_Outside", TargetType: "Test.Runtime")
            })
        ]);

        var report = ItemAuditService.Audit(root);

        Assert.False(report.Portable);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("ITEM_FLUX_EXTERNAL_REFERENCE", issue.Code);
        Assert.Equal("flux-external", issue.Classification);
    }

    [Fact]
    public void RuntimeContextIsWarningUnlessStrictAndExplicitAllowIsPortable()
    {
        var root = Slot("S_Root", [
            new ComponentSummary("C_Grab", "FrooxEngine.Grabbable", new Dictionary<string, MemberValue>()),
            new ComponentSummary("C_Context", "Test.Context", new Dictionary<string, MemberValue>
            {
                ["User"] = new("reference", "M_User", TargetId: "U_Local", TargetType: "FrooxEngine.User")
            })
        ]);

        Assert.True(ItemAuditService.Audit(root).Portable);
        Assert.False(ItemAuditService.Audit(root, strict: true).Portable);
        var allowed = ItemAuditService.Audit(root, ["U_Local"], strict: true);
        Assert.True(allowed.Portable);
        Assert.Equal("ITEM_EXTERNAL_REFERENCE_ALLOWED", Assert.Single(allowed.Issues).Code);
    }

    [Fact]
    public void StableComponentMemberRoleCanReplaceSessionScopedExternalIdAllowList()
    {
        var root = Slot("S_Root", [
            new ComponentSummary("C_Grab", "FrooxEngine.Grabbable", new Dictionary<string, MemberValue>()),
            new ComponentSummary("C_Text", "FrooxEngine.TextRenderer", new Dictionary<string, MemberValue>
            {
                ["Font"] = new("reference", "M_Font", TargetId: "Reso_SessionOnly",
                    TargetType: "FrooxEngine.IAssetProvider<FrooxEngine.FontSet>")
            })
        ]);

        var report = ItemAuditService.Audit(root, strict: true, allowedExternalRoles: ["TextRenderer:Font"]);

        Assert.True(report.Portable);
        Assert.Equal("ITEM_EXTERNAL_ROLE_ALLOWED", Assert.Single(report.Issues).Code);
    }

    private static SlotInfo Slot(string id, IReadOnlyList<ComponentSummary> components) =>
        new(id, "Item", "Root", null, null, null, true, true, null, false, components, [], "Root/Item");
}
