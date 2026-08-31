using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ApplyWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rloop-workflow-" + Guid.NewGuid().ToString("N"));

    public ApplyWorkflowTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task OfflineValidationAcceptsForwardReferencesAndRejectsUnknownKeys()
    {
        var valid = Document("forward", """
            [
              { "key": "source", "type": "Test.Source", "fields": { "Target": "$ref:target" } },
              { "key": "target", "type": "Test.Target", "fields": { "Enabled": true } }
            ]
            """);
        var result = await ApplyDocumentValidator.ValidateAsync(valid);
        Assert.True(result.Valid);
        Assert.Equal(1, result.References);

        var invalid = Document("invalid", """
            [{ "key": "source", "type": "Test.Source", "fields": { "Target": "$ref:missing" } }]
            """);
        var invalidResult = await ApplyDocumentValidator.ValidateAsync(invalid);
        Assert.False(invalidResult.Valid);
        Assert.Contains(invalidResult.Issues, x => x.Code == "APPLY_REFERENCE_NOT_FOUND");
    }

    [Fact]
    public async Task ApplyResolvesForwardReferenceAndSecondRunHasNoWrites()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var document = Document("forward", """
            [
              { "key": "source", "type": "Test.Source", "fields": { "Target": "$ref:target" } },
              { "key": "target", "type": "Test.Target", "fields": { "Enabled": true } }
            ]
            """);
        var state = Path.Combine(_root, "forward.state.json");

        var first = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(1, first.SlotsCreated);
        Assert.Equal(2, first.ComponentsAdded);
        Assert.True(File.Exists(state));
        Assert.Equal(1, client.BatchUpdates);
        Assert.NotNull(first.Profile);

        client.ResetWriteCounts();
        var second = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(0, second.SlotsCreated);
        Assert.Equal(0, second.SlotsUpdated);
        Assert.Equal(0, second.ComponentsAdded);
        Assert.Equal(0, second.ComponentsUpdated);
        Assert.Equal(1, second.SlotsUnchanged);
        Assert.Equal(2, second.ComponentsUnchanged);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task InitialFieldsAreAppliedOnlyWhenComponentIsCreated()
    {
        var path = Path.Combine(_root, "initial-fields.json");
        File.WriteAllText(path, """
            {
              "schemaVersion":"1", "ownership":{"key":"initial-fields"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"state","type":"Test.Target","fields":{"Enabled":true},"initialFields":{"Count":0}}]
            }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "initial-fields.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        var component = Assert.Single(Assert.Single(client.Root.Children).Components);
        await client.SetComponentMemberAsync(component.Id, "Count", "5");
        client.ResetWriteCounts();

        var second = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(0, client.Writes);
        Assert.Equal(5, component.Members["Count"].Value!.GetValue<int>());
    }

    [Fact]
    public async Task TestSupportsComponentExistenceAndFilteredChildCount()
    {
        var path = Path.Combine(_root, "existence-tests.json");
        File.WriteAllText(path, """
            {
              "schemaVersion":"1", "ownership":{"key":"existence-tests"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"target","type":"Test.Target","fields":{"Enabled":true}}],
              "children":[{"slot":{"key":"output","name":"Output"},"children":[
                {"slot":{"key":"generated","name":"Image"},"components":[{"key":"metadata","type":"Test.Metadata","fields":{}}]}
              ]}],
              "tests":[{"name":"structure","assertions":[
                {"target":"$component:target","exists":true},
                {"kind":"child-count","target":"$slot:output","name":"Image","componentType":"Test.Metadata","count":1}
              ]}]
            }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "existence-tests.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));

        var report = await service.TestAsync(document, new ApplyOptions(state));

        Assert.True(report.Passed);
        Assert.All(Assert.Single(report.Tests).Assertions, assertion => Assert.True(assertion.Passed));
    }

    [Fact]
    public async Task TupleCompatibilityStringConvergesAgainstRuntimeObject()
    {
        var path = Path.Combine(_root, "tuple-convergence.json");
        File.WriteAllText(path, """
            {
              "schemaVersion":"1", "ownership":{"key":"tuple-convergence"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"target","type":"Test.Target","fields":{"Offset":"1,2,3"}}]
            }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "tuple-convergence.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        var component = Assert.Single(Assert.Single(client.Root.Children).Components);
        component.Members["Offset"] = new MemberValue("field", component.Id + ":Offset", "float3",
            new JsonObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 });
        client.ResetWriteCounts();

        var second = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task SyncObjectListConvergesWhenRuntimeMembersMatchDeclaredStructure()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var document = Document("sync-list", """
            [{ "key": "slider", "type": "Test.Slider", "fields": {
              "SnapPositions": [{ "Position": [0, 1.14, -0.1], "MaxDistance": 10.0 }]
            } }]
            """);
        var state = Path.Combine(_root, "sync-list.state.json");

        await service.ApplyAsync(document, new ApplyOptions(state));
        var component = Assert.Single(Assert.Single(client.Root.Children).Components);
        component.Members["SnapPositions"] = new MemberValue("list", Elements:
        [
            new MemberValue("syncObject", Members: new Dictionary<string, MemberValue>
            {
                ["Position"] = new("field", Type: "ResoniteLink.float3", Value: JsonNode.Parse("{\"x\":0,\"y\":1.14,\"z\":-0.1}")),
                ["MaxDistance"] = new("field", Type: "System.Single", Value: JsonValue.Create(10f))
            })
        ]);
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.DoesNotContain(plan.Changes, operation => operation.Key == "slider");
        Assert.Equal("no-op", Assert.Single(plan.Operations, operation => operation.Kind == "component").Action);
        Assert.Equal(0, applied.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ExistingRootRequiresExplicitAdoption()
    {
        var client = new FakeResoniteClient();
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "Managed"));
        client.ResetWriteCounts();
        var service = new WorldService(client);
        var document = Document("adopt", "[]");
        var state = Path.Combine(_root, "adopt.state.json");

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(document, new ApplyOptions(state)));
        Assert.Equal("APPLY_OWNERSHIP_UNVERIFIED", error.Code);
        Assert.Equal(0, client.Writes);

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state, Adopt: true));
        Assert.Single(plan.Operations);
        Assert.Equal("update", plan.Operations[0].Action);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task CheckpointAllowsResumeWithoutDuplicateSlot()
    {
        var client = new FakeResoniteClient { CancelAfterWrites = 1 };
        using var cancellation = new CancellationTokenSource();
        client.Cancellation = cancellation;
        var service = new WorldService(client);
        var document = Document("resume", """
            [{ "key": "target", "type": "Test.Target", "fields": { "Enabled": true } }]
            """);
        var state = Path.Combine(_root, "resume.state.json");

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state), cancellation.Token));
        Assert.Equal("APPLY_CANCELLED", error.Code);
        Assert.True((int)error.Context["remaining"]! > 0);
        Assert.True(File.Exists(state));

        client.CancelAfterWrites = null;
        client.Cancellation = null;
        var resumed = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, resumed.SlotsCreated);
        Assert.Equal(1, resumed.ComponentsAdded);
        Assert.Single(client.Root.Children);
    }

    [Fact]
    public async Task PendingCheckpointRecoversWhenCreateResponseIsLost()
    {
        var client = new FakeResoniteClient { LoseNextSlotCreateResponse = true };
        var service = new WorldService(client);
        var state = Path.Combine(_root, "lost-response.state.json");
        var document = Document("lost-response", "[]");

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
        Assert.Equal("APPLY_CANCELLED", error.Code);
        Assert.Single(client.Root.Children);

        var resumed = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, resumed.SlotsCreated);
        Assert.Single(client.Root.Children);
    }

    [Fact]
    public async Task StrictValidationChecksRuntimeMembersBeforeMutation()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var document = Document("strict", """
            [{ "key": "target", "type": "Test.Target", "fields": { "Missing": true } }]
            """);

        var validation = await service.ValidateApplyAsync(document, true);
        Assert.False(validation.Valid);
        Assert.Contains(validation.Issues, x => x.Code == "COMPONENT_MEMBER_NOT_FOUND");
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document,
            new ApplyOptions(Path.Combine(_root, "strict.state.json"))));
        Assert.Equal("APPLY_VALIDATION_FAILED", error.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task StableKeysResolveDuplicateTypesAfterSessionChange()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var document = Document("duplicates", """
            [
              { "key": "first", "type": "Test.Target", "fields": { "Enabled": true } },
              { "key": "second", "type": "Test.Target", "fields": { "Enabled": false } }
            ]
            """);
        var state = Path.Combine(_root, "duplicates.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));

        client.SessionId = "session-2";
        client.ResetWriteCounts();
        var reapplied = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(0, reapplied.ComponentsAdded);
        Assert.Equal(2, reapplied.ComponentsUnchanged);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task IdentityFieldsResolveStableComponentAfterSameTypeInsertionAndSessionChange()
    {
        var path = Path.Combine(_root, "identity-fields.json");
        File.WriteAllText(path, """
            { "schemaVersion":"1", "ownership":{"key":"identity-fields"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"target","type":"Test.Target","fields":{"Enabled":false},"identityFields":["Enabled"]}] }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "identity-fields.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        var managed = Assert.Single(client.Root.Children);
        var originalId = Assert.Single(managed.Components).Id;
        client.PrependComponent(managed, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "true" });
        client.SessionId = "session-2";
        client.ResetWriteCounts();

        var resolved = await service.ResolveStableReferenceAsync(state, "$component:target", client.SessionId);
        var reapplied = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(originalId, resolved.Id);
        Assert.Equal(0, reapplied.ComponentsAdded);
        Assert.Equal(1, reapplied.ComponentsUnchanged);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ExplicitSlotKeySupportsRenameAfterSessionChange()
    {
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var state = Path.Combine(_root, "rename.state.json");
        await service.ApplyAsync(Document("rename", "[]"), new ApplyOptions(state));

        client.SessionId = "session-2";
        client.ResetWriteCounts();
        var renamedDocument = Document("rename", "[]", "ManagedRenamed");
        var renamePlan = await service.PlanApplyAsync(renamedDocument, new ApplyOptions(state));
        Assert.Contains(renamePlan.Operations, operation => operation.Action == "rename" && operation.Key == "root");
        var renamed = await service.ApplyAsync(renamedDocument, new ApplyOptions(state));

        Assert.Equal(1, renamed.SlotsUpdated);
        Assert.Equal("ManagedRenamed", Assert.Single(client.Root.Children).Name);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task PreserveWorldTransformKeepsAnExistingUserPlacement()
    {
        var path = Path.Combine(_root, "preserve-transform.json");
        File.WriteAllText(path, """
            { "schemaVersion":"1", "ownership":{"key":"preserve-transform"},
              "slot":{"key":"root","name":"Managed","parent":"Root","position":[0,0,0],"preserveWorldTransform":true} }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var state = Path.Combine(_root, "preserve-transform.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        var managed = Assert.Single(client.Root.Children);
        managed.Position = new Vector3Value(5, 6, 7);
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("no-op", Assert.Single(plan.Operations, operation => operation.Kind == "slot").Action);
        Assert.Equal(new Vector3Value(5, 6, 7), managed.Position);
        Assert.Equal(0, applied.SlotsUpdated);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ManagedFieldsUpdatesOnlySelectedTransforms()
    {
        var path = Path.Combine(_root, "managed-fields.json");
        File.WriteAllText(path, """
            { "schemaVersion":"1", "ownership":{"key":"managed-fields"},
              "slot":{"key":"root","name":"Managed","parent":"Root","position":[0,0,0],"scale":[1,1,1],
                      "managedFields":["scale"]} }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var state = Path.Combine(_root, "managed-fields.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        var managed = Assert.Single(client.Root.Children);
        managed.Position = new Vector3Value(5, 6, 7);
        managed.Scale = new Vector3Value(2, 2, 2);
        client.ResetWriteCounts();

        var applied = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(new Vector3Value(5, 6, 7), managed.Position);
        Assert.Equal(new Vector3Value(1, 1, 1), managed.Scale);
        Assert.Equal(1, applied.SlotsUpdated);
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task ValidationRejectsUnknownManagedFieldsAndAmbiguousMigrationSources()
    {
        var path = Path.Combine(_root, "invalid-management-policy.json");
        File.WriteAllText(path, """
            { "schemaVersion":"1", "ownership":{"key":"invalid-management-policy"},
              "slot":{"key":"root","name":"Managed","parent":"Root","managedFields":["name"]},
              "children":[
                {"slot":{"key":"old","name":"Old"}},
                {"slot":{"key":"new","migrateFrom":"old","name":"New"}}
              ] }
            """);

        var result = await ApplyDocumentValidator.ValidateAsync(ApplyDocument.Load(path));

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "APPLY_MANAGED_FIELD_INVALID");
        Assert.Contains(result.Issues, issue => issue.Code == "APPLY_MIGRATION_SOURCE_DECLARED");
    }

    [Fact]
    public async Task MigrateFromRenamesStableSlotAndComponentKeysWithoutWorldWrites()
    {
        var initial = Path.Combine(_root, "migration-initial.json");
        File.WriteAllText(initial, """
            { "schemaVersion":"1", "ownership":{"key":"migration"},
              "slot":{"key":"old-root","name":"Managed","parent":"Root"},
              "components":[{"key":"old-target","type":"Test.Target","fields":{"Enabled":true}}] }
            """);
        var desired = Path.Combine(_root, "migration-desired.json");
        File.WriteAllText(desired, """
            { "schemaVersion":"1", "ownership":{"key":"migration"},
              "slot":{"key":"new-root","migrateFrom":"old-root","name":"Managed","parent":"Root"},
              "components":[{"key":"new-target","migrateFrom":"old-target","type":"Test.Target","fields":{"Enabled":true}}] }
            """);
        var client = new FakeResoniteClient(ApplyDocument.Load(initial));
        var service = new WorldService(client);
        var state = Path.Combine(_root, "migration.state.json");
        await service.ApplyAsync(ApplyDocument.Load(initial), new ApplyOptions(state));
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(ApplyDocument.Load(desired), new ApplyOptions(state));
        var applied = await service.ApplyAsync(ApplyDocument.Load(desired), new ApplyOptions(state));

        Assert.DoesNotContain(plan.Operations, operation => operation.Action is "create" or "delete");
        Assert.Contains(plan.Operations, operation => operation.Key == "new-root" && operation.Reason?.Contains("migrated from 'old-root'") == true);
        Assert.Contains(plan.Operations, operation => operation.Key == "new-target" && operation.Reason?.Contains("migrated from 'old-target'") == true);
        Assert.Equal(0, client.Writes);
        Assert.Equal(0, applied.SlotsCreated);
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(state));
        Assert.True(checkpoint.RootElement.GetProperty("slots").TryGetProperty("new-root", out _));
        Assert.False(checkpoint.RootElement.GetProperty("slots").TryGetProperty("old-root", out _));
        Assert.True(checkpoint.RootElement.GetProperty("components").TryGetProperty("new-target", out _));
    }

    [Fact]
    public async Task PruneRequiresConfirmationAndDeletesOnlyStaleOwnedTargets()
    {
        var state = Path.Combine(_root, "prune.state.json");
        var initialPath = Path.Combine(_root, "prune-initial.json");
        File.WriteAllText(initialPath, """
            { "schemaVersion":"1", "ownership":{"key":"prune"}, "slot":{"key":"root","name":"Managed","parent":"Root"},
              "children":[{"slot":{"key":"old-slot","name":"Old"},"components":[{"key":"old-component","type":"Test.Target","fields":{"Enabled":true}}]}] }
            """);
        var desiredPath = Path.Combine(_root, "prune-desired.json");
        File.WriteAllText(desiredPath, """
            { "schemaVersion":"1", "ownership":{"key":"prune"}, "slot":{"key":"root","name":"Managed","parent":"Root"}, "children":[] }
            """);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));

        var plan = await service.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        Assert.Contains(plan.Operations, operation => operation.Action == "delete" && operation.Key == "old-slot");
        Assert.DoesNotContain(plan.Operations, operation => operation.Action == "delete" && operation.Kind == "component");
        Assert.Single(Assert.Single(client.Root.Children).Children);
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state, Prune: true)));

        var applied = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state, Prune: true, ConfirmDeletes: true));
        Assert.Equal(1, applied.SlotsDeleted);
        Assert.Equal(0, applied.ComponentsDeleted);
        Assert.Empty(Assert.Single(client.Root.Children).Children);
        Assert.False(applied.Atomic);
        Assert.NotNull(applied.Recovery);
    }

    [Fact]
    public async Task ParentPruneKeepsStateForAStableChildMovedOutOfTheDeletedParent()
    {
        var state = Path.Combine(_root, "prune-move.state.json");
        var initialPath = Path.Combine(_root, "prune-move-initial.json");
        File.WriteAllText(initialPath, """
            { "schemaVersion":"1", "ownership":{"key":"prune-move"}, "slot":{"key":"root","name":"Managed","parent":"Root"},
              "children":[{"slot":{"key":"old-parent","name":"OldParent"},"children":[
                {"slot":{"key":"kept-child","name":"Kept"},"components":[
                  {"key":"kept-component","type":"Test.Target","fields":{"Enabled":true}}
                ]}
              ]}] }
            """);
        var desiredPath = Path.Combine(_root, "prune-move-desired.json");
        File.WriteAllText(desiredPath, """
            { "schemaVersion":"1", "ownership":{"key":"prune-move"}, "slot":{"key":"root","name":"Managed","parent":"Root"},
              "children":[{"slot":{"key":"kept-child","name":"Kept"},"components":[
                {"key":"kept-component","type":"Test.Target","fields":{"Enabled":true}}
              ]}] }
            """);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        var managed = Assert.Single(client.Root.Children);
        var oldParent = Assert.Single(managed.Children);
        var originalChild = Assert.Single(oldParent.Children);
        var originalSlotId = originalChild.Id;
        var originalComponentId = Assert.Single(originalChild.Components).Id;

        var applied = await service.ApplyAsync(ApplyDocument.Load(desiredPath),
            new ApplyOptions(state, Prune: true, ConfirmDeletes: true));
        client.ResetWriteCounts();
        var reapplied = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));

        Assert.Equal(1, applied.SlotsDeleted);
        Assert.Equal(1, applied.SlotsUpdated);
        Assert.Equal(0, applied.SlotsCreated);
        Assert.Equal(0, applied.ComponentsAdded);
        Assert.Equal(0, applied.ComponentsDeleted);
        var movedChild = Assert.Single(Assert.Single(client.Root.Children).Children);
        Assert.Equal(originalSlotId, movedChild.Id);
        Assert.Equal(originalComponentId, Assert.Single(movedChild.Components).Id);
        Assert.Equal(0, reapplied.SlotsCreated);
        Assert.Equal(0, reapplied.ComponentsAdded);
        Assert.Equal(0, client.Writes);
        using var checkpoint = JsonDocument.Parse(File.ReadAllText(state));
        Assert.True(checkpoint.RootElement.GetProperty("slots").TryGetProperty("kept-child", out _));
        Assert.True(checkpoint.RootElement.GetProperty("components").TryGetProperty("kept-component", out _));
    }

    [Fact]
    public async Task StableComponentMoveRecreatesAtDestinationAndRemovesExactSource()
    {
        var state = Path.Combine(_root, "component-move.state.json");
        var initialPath = Path.Combine(_root, "component-move-initial.json");
        File.WriteAllText(initialPath, """
            { "schemaVersion":"1", "ownership":{"key":"component-move"}, "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"moved-component","type":"Test.Target","fields":{"Enabled":true}}],
              "children":[{"slot":{"key":"destination","name":"Destination"}}] }
            """);
        var desiredPath = Path.Combine(_root, "component-move-desired.json");
        File.WriteAllText(desiredPath, """
            { "schemaVersion":"1", "ownership":{"key":"component-move"}, "slot":{"key":"root","name":"Managed","parent":"Root"},
              "children":[{"slot":{"key":"destination","name":"Destination"},"components":[
                {"key":"moved-component","type":"Test.Target","fields":{"Enabled":true}}
              ]}] }
            """);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        var managed = Assert.Single(client.Root.Children);
        var originalId = Assert.Single(managed.Components).Id;

        var plan = await service.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        Assert.Contains(plan.Operations, operation => operation.Action == "relocate" && operation.Key == "moved-component");
        var applied = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        client.ResetWriteCounts();
        var reapplied = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));

        Assert.Empty(managed.Components);
        var replacement = Assert.Single(Assert.Single(managed.Children).Components);
        Assert.NotEqual(originalId, replacement.Id);
        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Equal(1, applied.ComponentsDeleted);
        Assert.Equal(0, reapplied.ComponentsAdded);
        Assert.Equal(0, reapplied.ComponentsDeleted);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task StableComponentMoveDoesNotAdoptAnUnrelatedSameTypeDestinationComponent()
    {
        var state = Path.Combine(_root, "component-move-collision.state.json");
        var initialPath = Path.Combine(_root, "component-move-collision-initial.json");
        File.WriteAllText(initialPath, """
            { "schemaVersion":"1", "ownership":{"key":"component-move-collision"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"moved","type":"Test.Target","fields":{"Enabled":true}}],
              "children":[{"slot":{"key":"destination","name":"Destination"},"components":[
                {"key":"resident","type":"Test.Target","fields":{"Enabled":false}}
              ]}] }
            """);
        var desiredPath = Path.Combine(_root, "component-move-collision-desired.json");
        File.WriteAllText(desiredPath, """
            { "schemaVersion":"1", "ownership":{"key":"component-move-collision"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "children":[{"slot":{"key":"destination","name":"Destination"},"components":[
                {"key":"resident","type":"Test.Target","fields":{"Enabled":false}},
                {"key":"moved","type":"Test.Target","fields":{"Enabled":true}}
              ]}] }
            """);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        var managed = Assert.Single(client.Root.Children);
        var sourceId = Assert.Single(managed.Components).Id;
        var residentId = Assert.Single(Assert.Single(managed.Children).Components).Id;

        var applied = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));

        Assert.Empty(managed.Components);
        var destinationComponents = Assert.Single(managed.Children).Components;
        Assert.Equal(2, destinationComponents.Count);
        Assert.Contains(destinationComponents, component => component.Id == residentId);
        Assert.Contains(destinationComponents, component => component.Id != residentId && component.Id != sourceId);
        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Equal(1, applied.ComponentsDeleted);
    }

    [Fact]
    public async Task StableOwnershipRootMovesBetweenParentsWithoutChangingId()
    {
        var initialPath = Path.Combine(_root, "root-move-initial.json");
        File.WriteAllText(initialPath, """
            { "schemaVersion":"1", "ownership":{"key":"root-move"},
              "slot":{"key":"root","name":"Managed","parent":"Root/ParentA"} }
            """);
        var desiredPath = Path.Combine(_root, "root-move-desired.json");
        File.WriteAllText(desiredPath, """
            { "schemaVersion":"1", "ownership":{"key":"root-move"},
              "slot":{"key":"root","name":"Managed","parent":"Root/ParentB"} }
            """);
        var client = new FakeResoniteClient();
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "ParentA"));
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "ParentB"));
        var service = new WorldService(client);
        var state = Path.Combine(_root, "root-move.state.json");
        var initial = await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        var moved = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        client.ResetWriteCounts();
        var reapplied = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));

        Assert.Contains(plan.Operations, operation => operation.Action == "relocate" && operation.Key == "root");
        Assert.Equal(1, plan.Updates);
        Assert.Equal(initial.SlotId, moved.SlotId);
        Assert.Equal(initial.SlotId, Assert.Single(client.Root.Children.Single(slot => slot.Name == "ParentB").Children).Id);
        Assert.Empty(client.Root.Children.Single(slot => slot.Name == "ParentA").Children);
        Assert.Equal(0, reapplied.SlotsUpdated);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task StableOwnershipRootMoveCanPruneAStaleChildFromItsPreviousLocation()
    {
        var initialPath = Path.Combine(_root, "root-move-prune-initial.json");
        File.WriteAllText(initialPath, """
            { "schemaVersion":"1", "ownership":{"key":"root-move-prune"},
              "slot":{"key":"root","name":"Managed","parent":"Root/ParentA"},
              "children":[{"slot":{"key":"stale","name":"Stale"}}] }
            """);
        var desiredPath = Path.Combine(_root, "root-move-prune-desired.json");
        File.WriteAllText(desiredPath, """
            { "schemaVersion":"1", "ownership":{"key":"root-move-prune"},
              "slot":{"key":"root","name":"Managed","parent":"Root/ParentB"}, "children":[] }
            """);
        var client = new FakeResoniteClient();
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "ParentA"));
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "ParentB"));
        var service = new WorldService(client);
        var state = Path.Combine(_root, "root-move-prune.state.json");
        var initial = await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));

        var plan = await service.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        var applied = await service.ApplyAsync(ApplyDocument.Load(desiredPath),
            new ApplyOptions(state, Prune: true, ConfirmDeletes: true));

        Assert.Contains(plan.Operations, operation => operation.Action == "relocate" && operation.Key == "root");
        Assert.Contains(plan.Operations, operation => operation.Action == "delete" && operation.Key == "stale");
        Assert.Equal(initial.SlotId, applied.SlotId);
        Assert.Equal(1, applied.SlotsUpdated);
        Assert.Equal(1, applied.SlotsDeleted);
        Assert.Empty(client.Root.Children.Single(slot => slot.Name == "ParentA").Children);
        Assert.Empty(Assert.Single(client.Root.Children.Single(slot => slot.Name == "ParentB").Children).Children);
    }

    [Fact]
    public async Task AssetImportIsContentAddressedAndAssetReferenceUpdatesOnChange()
    {
        var asset = Path.Combine(_root, "texture.bin");
        await File.WriteAllTextAsync(asset, "first");
        var source = Path.Combine(_root, "assets.json");
        await File.WriteAllTextAsync(source, """
            { "schemaVersion":"1", "ownership":{"key":"assets"}, "slot":{"key":"root","name":"Managed","parent":"Root"},
              "assets":{"surface":{"kind":"texture","source":"texture.bin"}},
              "components":[{"key":"holder","type":"Test.AssetHolder","fields":{"Uri":"$asset:surface"}}] }
            """);
        var document = ApplyDocument.Load(source);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "assets.state.json");

        await service.ApplyAsync(document, new ApplyOptions(state));
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(1, client.AssetImports);

        await File.WriteAllTextAsync(asset, "second");
        var changed = await service.ApplyAsync(ApplyDocument.Load(source), new ApplyOptions(state));
        Assert.Equal(2, client.AssetImports);
        Assert.Equal(1, changed.ComponentsUpdated);
    }

    [Fact]
    public async Task ClosedGenericComponentTypeIsPassedIntactToRuntimeReflection()
    {
        const string generic = "Test.Generic<System.Boolean>";
        var document = Document("generic", $$"""
            [{ "key": "generic", "type": "{{generic}}", "fields": { "Value": true } }]
            """);
        var client = new FakeResoniteClient(document);

        var validation = await new WorldService(client).ValidateApplyAsync(document, true);

        Assert.True(validation.Valid);
        Assert.Contains(generic, client.DescribedTypes);
    }

    [Fact]
    public async Task HouseFixtureHasBoundedRequestsAndZeroWriteSecondRun()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "house-world.json"));
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "house.state.json");

        var first = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(69, first.SlotsCreated);
        Assert.Equal(148, first.ComponentsAdded);
        Assert.Equal(217, client.Writes);

        client.ResetWriteCounts();
        var second = await service.ApplyAsync(document, new ApplyOptions(state, Profile: true));
        Assert.Equal(217, second.Profile!.NoOps);
        Assert.InRange(second.Profile.Client.Requests, 1, 12);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task HouseMirrorFixtureVerifiesWiringAndReportsUnavailableProbeAsStructuralOnly()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "house-world.json"));
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "house-test.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));

        var report = await service.TestAsync(document, new ApplyOptions(state));

        Assert.True(report.Passed);
        Assert.True(report.StructuralOnly);
        var test = Assert.Single(report.Tests);
        Assert.Contains(test.Assertions, assertion => assertion.Target == "$component:mirrorToggle.TargetValue" && assertion.Passed);
        Assert.Contains(test.Assertions, assertion => assertion.Phase == "after" && !assertion.Evaluated);
    }

    [Fact]
    public async Task SetMemberProbePollsTemporaryValueAndAlwaysRestoresOriginal()
    {
        var path = Path.Combine(_root, "transactional-probe.json");
        File.WriteAllText(path, """
            {
              "schemaVersion":"1",
              "ownership":{"key":"transactional-probe"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"target","type":"Test.Target","fields":{"Enabled":false}}],
              "tests":[{
                "name":"temporary toggle",
                "assertions":[
                  {"target":"$component:target.Enabled","expected":false},
                  {"target":"$component:target.Enabled","expected":true,"phase":"after"}
                ],
                "probe":{
                  "kind":"set-member",
                  "target":"$component:target.Enabled",
                  "value":true,
                  "restore":true,
                  "safe":true
                }
              }]
            }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "transactional-probe.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ResetWriteCounts();

        var report = await service.TestAsync(document, new ApplyOptions(state), allowProbe: true);

        Assert.True(report.Passed);
        Assert.False(report.StructuralOnly);
        var test = Assert.Single(report.Tests);
        Assert.True(test.ProbeExecuted);
        Assert.All(test.Assertions, assertion => Assert.True(assertion.Passed));
        Assert.Equal(2, client.Writes);
        var component = Assert.Single(Assert.Single(client.Root.Children).Components);
        Assert.False(component.Members["Enabled"].Value!.GetValue<bool>());
    }

    [Fact]
    public async Task SetMemberProbeRestoresOriginalWhenAfterAssertionFails()
    {
        var path = Path.Combine(_root, "failing-transactional-probe.json");
        File.WriteAllText(path, """
            {
              "schemaVersion":"1", "ownership":{"key":"failing-probe"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "components":[{"key":"target","type":"Test.Target","fields":{"Enabled":false}}],
              "tests":[{
                "name":"intentional mismatch",
                "assertions":[{"target":"$component:target.Enabled","expected":false,"phase":"after"}],
                "probe":{"kind":"set-member","target":"$component:target.Enabled","value":true,"restore":true,"safe":true},
                "timeoutMs":10, "pollMs":10
              }]
            }
            """);
        var document = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "failing-transactional-probe.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));

        var report = await service.TestAsync(document, new ApplyOptions(state), allowProbe: true);

        Assert.False(report.Passed);
        var component = Assert.Single(Assert.Single(client.Root.Children).Components);
        Assert.False(component.Members["Enabled"].Value!.GetValue<bool>());
    }

    private ApplyDocument Document(string ownership, string components, string name = "Managed")
    {
        var path = Path.Combine(_root, ownership + ".json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "1",
              "ownership": { "key": "{{ownership}}" },
              "slot": { "key": "root", "name": "{{name}}", "parent": "Root", "position": [0, 1, 2] },
              "components": {{components}}
            }
            """);
        return ApplyDocument.Load(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeResoniteClient : IResoniteClient, IResoniteClientDiagnostics
    {
        private int _nextSlot = 1;
        private int _nextComponent = 1;
        private readonly Dictionary<string, FakeSlot> _slots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeComponent> _components = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _knownMembers = new(StringComparer.Ordinal);
        private int _requests;
        public FakeSlot Root { get; }
        public int Writes { get; private set; }
        public int BatchUpdates { get; private set; }
        public int? CancelAfterWrites { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public string SessionId { get; set; } = "session-1";
        public bool LoseNextSlotCreateResponse { get; set; }
        public int AssetImports { get; private set; }
        public List<string> DescribedTypes { get; } = [];

        public FakeResoniteClient(ApplyDocument? definitions = null)
        {
            Root = new FakeSlot("Root", "Root", null, null, null, null);
            _slots[Root.Id] = Root;
            _knownMembers["Test.Source"] = ["Target"];
            _knownMembers["Test.Target"] = ["Enabled"];
            _knownMembers["Test.Slider"] = ["SnapPositions"];
            if (definitions is not null) RegisterDefinitions(definitions);
        }

        public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(new SessionInfo("ws://fake", true, "test", "test", SessionId)));

        public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests++;
            if (!_slots.TryGetValue(id, out var slot)) throw new RLoopException("SLOT_NOT_FOUND", id, ExitCodes.NotFound);
            return Task.FromResult(Map(slot, depth, includeComponentData));
        }

        public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
        {
            _requests++;
            var component = _components[id];
            return Task.FromResult(new ComponentInfo(component.Id, component.Type, component.Members));
        }

        public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default)
        {
            Write();
            var id = "S" + _nextSlot++;
            var slot = new FakeSlot(id, request.Name, request.ParentId, request.Position, request.Rotation, request.Scale);
            _slots[id] = slot;
            _slots[request.ParentId].Children.Add(slot);
            if (LoseNextSlotCreateResponse)
            {
                LoseNextSlotCreateResponse = false;
                throw new OperationCanceledException("Simulated lost create response.");
            }
            return Task.FromResult(id);
        }

        public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Write();
            var slot = _slots[request.Id];
            if (request.ParentId is not null && request.ParentId != slot.ParentId)
            {
                if (slot.ParentId is not null) _slots[slot.ParentId].Children.Remove(slot);
                _slots[request.ParentId].Children.Add(slot);
                slot.ParentId = request.ParentId;
            }
            if (request.Name is not null) slot.Name = request.Name;
            if (request.Position is not null) slot.Position = request.Position;
            if (request.Rotation is not null) slot.Rotation = request.Rotation;
            if (request.Scale is not null) slot.Scale = request.Scale;
            return Task.CompletedTask;
        }

        public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default)
        {
            Write();
            var slot = _slots[id];
            _slots[slot.ParentId!].Children.Remove(slot);
            RemoveSlotTree(slot);
            return Task.CompletedTask;
        }

        public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
        {
            Write();
            var id = "C" + _nextComponent++;
            var component = new FakeComponent(id, componentType);
            foreach (var member in _knownMembers.GetValueOrDefault(componentType) ?? [])
                component.Members[member] = new MemberValue("field", id + ":" + member, "bool", JsonValue.Create(false));
            SetFields(component, fields);
            _components[id] = component;
            _slots[slotId].Components.Add(component);
            return Task.FromResult(new ComponentCreateResult(id, componentType));
        }

        public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) =>
            SetComponentMembersAsync(componentId, _components[componentId].Type, new Dictionary<string, string> { [member] = rawValue }, cancellationToken);

        public Task SetComponentMembersAsync(string componentId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
        {
            Write();
            BatchUpdates++;
            SetFields(_components[componentId], fields);
            return Task.CompletedTask;
        }

        public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default)
        {
            Write();
            var component = _components[componentId];
            foreach (var slot in _slots.Values) slot.Components.Remove(component);
            _components.Remove(componentId);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Read(_knownMembers.Keys.Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray()));

        public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default)
        {
            _requests++;
            DescribedTypes.Add(type);
            if (!_knownMembers.TryGetValue(type, out var known))
                throw new RLoopException("COMPONENT_TYPE_NOT_FOUND", type, ExitCodes.NotFound);
            IReadOnlyList<MemberDefinitionInfo> members = known.Select(name =>
                new MemberDefinitionInfo(name, name is "Target" or "Mesh" or "TargetValue" ? "reference" : "field",
                    null, "bool", null)).ToArray();
            return Task.FromResult(new ComponentTypeInfo(type, null, null, false, members));
        }

        public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource, CancellationToken cancellationToken = default)
        {
            AssetImports++;
            return Task.FromResult("resdb:///asset-" + AssetImports);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void ResetMetrics() => _requests = 0;
        public ClientMetrics SnapshotMetrics() => new(_requests, 0, 0,
            [new ClientOperationMetric("fake", _requests, 0)]);
        public void ResetWriteCounts() { Writes = 0; BatchUpdates = 0; }

        public FakeComponent PrependComponent(FakeSlot slot, string type, IReadOnlyDictionary<string, string> fields)
        {
            var component = new FakeComponent("C" + _nextComponent++, type);
            foreach (var member in _knownMembers.GetValueOrDefault(type) ?? [])
                component.Members[member] = new MemberValue("field", component.Id + ":" + member, "bool", JsonValue.Create(false));
            SetFields(component, fields);
            _components[component.Id] = component;
            slot.Components.Insert(0, component);
            return component;
        }

        private void Write()
        {
            Writes++;
            if (CancelAfterWrites == Writes) Cancellation?.Cancel();
        }

        private void RemoveSlotTree(FakeSlot slot)
        {
            foreach (var child in slot.Children.ToArray()) RemoveSlotTree(child);
            foreach (var component in slot.Components) _components.Remove(component.Id);
            _slots.Remove(slot.Id);
        }

        private static void SetFields(FakeComponent component, IReadOnlyDictionary<string, string> fields)
        {
            foreach (var field in fields)
            {
                var id = component.Id + ":" + field.Key;
                if (field.Value.StartsWith("C", StringComparison.Ordinal))
                    component.Members[field.Key] = new MemberValue("reference", id, TargetId: field.Value);
                else
                {
                    JsonNode? value;
                    try { value = JsonNode.Parse(field.Value); }
                    catch (JsonException) { value = JsonValue.Create(field.Value); }
                    component.Members[field.Key] = new MemberValue("field", id, "value", value);
                }
            }
        }

        private T Read<T>(T value)
        {
            _requests++;
            return value;
        }

        private void RegisterDefinitions(ApplyDocument document)
        {
            var keyedTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            void Visit(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children)
            {
                foreach (var component in components ?? [])
                {
                    if (!_knownMembers.TryGetValue(component.Type, out var members))
                        _knownMembers[component.Type] = members = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in component.Fields?.Keys ?? []) members.Add(field);
                    foreach (var field in component.InitialFields?.Keys ?? []) members.Add(field);
                    if (!string.IsNullOrWhiteSpace(component.Key)) keyedTypes[component.Key] = component.Type;
                }
                foreach (var child in children ?? []) Visit(child.Slot, child.Components, child.Children);
            }
            Visit(document.Slot!, document.Components, document.Children);

            void Scan(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.String && (value.GetString() ?? string.Empty).StartsWith("$member:", StringComparison.Ordinal))
                {
                    var selector = value.GetString()![8..];
                    var separator = selector.LastIndexOf('.');
                    if (separator > 0 && keyedTypes.TryGetValue(selector[..separator], out var type))
                        _knownMembers[type].Add(selector[(separator + 1)..]);
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) Scan(item);
            }
            void ScanNodes(IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children)
            {
                foreach (var component in components ?? []) foreach (var value in component.Fields?.Values ?? []) Scan(value);
                foreach (var child in children ?? []) ScanNodes(child.Components, child.Children);
            }
            ScanNodes(document.Components, document.Children);
        }

        private static SlotInfo Map(FakeSlot slot, int depth, bool members) => new(slot.Id, slot.Name, slot.ParentId,
            slot.Position, slot.Rotation, slot.Scale, true, true, null, false,
            slot.Components.Select(x => new ComponentSummary(x.Id, x.Type, members ? x.Members : null)).ToArray(),
            depth == 0 ? [] : slot.Children.Select(x => Map(x, depth < 0 ? -1 : depth - 1, members)).ToArray());

        public sealed class FakeSlot(string id, string name, string? parentId, Vector3Value? position,
            QuaternionValue? rotation, Vector3Value? scale)
        {
            public string Id { get; } = id;
            public string Name { get; set; } = name;
            public string? ParentId { get; set; } = parentId;
            public Vector3Value? Position { get; set; } = position;
            public QuaternionValue? Rotation { get; set; } = rotation;
            public Vector3Value? Scale { get; set; } = scale;
            public List<FakeSlot> Children { get; } = [];
            public List<FakeComponent> Components { get; } = [];
        }

        public sealed class FakeComponent(string id, string type)
        {
            public string Id { get; } = id;
            public string Type { get; } = type;
            public Dictionary<string, MemberValue> Members { get; } = new(StringComparer.Ordinal);
        }
    }
}
