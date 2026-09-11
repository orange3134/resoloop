using RLoop.Core;

namespace RLoop.Tests;

public sealed class ItemAuditTests
{
    [Fact]
    public void OwnedSlotFieldDriveIsInternalButAnotherSlotsFieldIsExternal()
    {
        var root = ShaderRoot() with {
            Members = new Dictionary<string, MemberValue> { ["Rotation"] = new("field", "RotationID", "floatQ") },
            Components = [new("Spinner", "FrooxEngine.Spinner", new Dictionary<string, MemberValue> {
                ["_target"] = new("reference", TargetId: "RotationID"),
                ["Other"] = new("reference", TargetId: "OutsideRotationID") })] };
        var report = ItemAuditService.Audit(root);
        Assert.Equal(1, report.InternalReferences);
        Assert.Equal(1, report.ExternalReferences);
        Assert.DoesNotContain(report.Issues, i => i.TargetId == "RotationID");
        Assert.Contains(report.Issues, i => i.TargetId == "OutsideRotationID" && i.Severity == "error");
    }

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

    [Theory]
    [InlineData("PBS_Metallic:_shader")]
    [InlineData("FrooxEngine.PBS_Metallic:_shader")]
    [InlineData("[FrooxEngine]FrooxEngine.PBS_Metallic:_shader")]
    [InlineData("C_Material:_shader")]
    public void RoleSpellingsMatchTheReviewedReference(string role)
    {
        var report = ItemAuditService.Audit(ShaderRoot(), allowedExternalRoles: [role]);
        var issue = Assert.Single(report.Issues, i => i.TargetId == "Shader");
        Assert.Equal("ITEM_EXTERNAL_ROLE_ALLOWED", issue.Code);
        Assert.Equal(role, issue.MatchedAllowEntry);
        Assert.Equal("[FrooxEngine]FrooxEngine.PBS_Metallic:_shader", issue.Role);
        Assert.Empty(report.UnmatchedExternalRoles!);
    }

    [Theory]
    [InlineData("Wrong.Namespace.PBS_Metallic:_shader")]
    [InlineData("[Wrong]FrooxEngine.PBS_Metallic:_shader")]
    [InlineData("C_Other:_shader")]
    [InlineData("PBS_Metallic:WrongMember")]
    public void UnmatchedRolesWarnWithoutBroadeningPermission(string role)
    {
        var report = ItemAuditService.Audit(ShaderRoot(), allowedExternalRoles: [role]);
        Assert.False(report.Portable);
        Assert.Contains(report.Issues, i => i.Code == "ITEM_EXTERNAL_REFERENCE");
        Assert.Contains(report.Issues, i => i.Code == "ITEM_ALLOW_ROLE_UNUSED");
        Assert.Equal([role], report.UnmatchedExternalRoles);
        Assert.Equal(["[FrooxEngine]FrooxEngine.PBS_Metallic:_shader"], report.ExternalRoleCandidates);
    }

    [Fact]
    public void ComponentIdPermissionDoesNotAllowAnotherMaterial()
    {
        var root = ShaderRoot();
        root = root with { Components = [..root.Components, root.Components[0] with { Id = "C_Other" }] };
        var report = ItemAuditService.Audit(root, allowedExternalRoles: ["C_Material:_shader"]);
        Assert.Equal("error", Assert.Single(report.Issues, i => i.ComponentId == "C_Other").Severity);
        Assert.Equal("info", Assert.Single(report.Issues, i => i.ComponentId == "C_Material").Severity);
    }

    [Fact]
    public void MalformedRoleIsAnArgumentError()
    {
        Assert.Equal("ITEM_ALLOW_ROLE_INVALID", Assert.Throws<RLoopException>(() =>
            ItemAuditService.Audit(ShaderRoot(), allowedExternalRoles: ["PBS_Metallic"])).Code);
    }

    [Fact]
    public void UnusedCaseMismatchedIdStillWarnsAndFailsStrictAudit()
    {
        var root = ShaderRoot();
        root = root with { Components = [..root.Components, new("C_Grab", "FrooxEngine.Grabbable")] };
        string[] roles = ["C_Material:_shader", "c_material:_shader"];
        var report = ItemAuditService.Audit(root, allowedExternalRoles: roles);
        Assert.True(report.Portable);
        Assert.Equal(["c_material:_shader"], report.UnmatchedExternalRoles);
        Assert.False(ItemAuditService.Audit(root, strict: true, allowedExternalRoles: roles).Portable);
    }

    private static SlotInfo ShaderRoot() => Slot("S_Root", [
        new ComponentSummary("C_Material", "[FrooxEngine]FrooxEngine.PBS_Metallic", new Dictionary<string, MemberValue>
        {
            ["_shader"] = new("reference", "M_Shader", TargetId: "Shader", TargetType: "FrooxEngine.IAssetProvider<Shader>")
        })]);
}
