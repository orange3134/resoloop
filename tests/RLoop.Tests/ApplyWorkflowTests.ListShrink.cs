using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("grow")]
    [InlineData("reorder")]
    public async Task AListThatDoesNotShrinkIsUpdatedInPlace(string change)
    {
        var name = "in-place-" + change;
        var (client, service, state, changed) = await ApplyRubbleAsync(name,
            change == "grow" ? RubbleWorld(name, "m1", "m2", "m3", "m4") : RubbleWorld(name, "m2", "m1"),
            RubbleWorld(name, "m1", "m2"));
        var original = RubbleRenderers(client).Single().Id;

        var plan = await service.PlanApplyAsync(changed, new ApplyOptions(state));
        await service.ApplyAsync(changed, new ApplyOptions(state));

        Assert.Equal("update", Assert.Single(plan.Operations, operation => operation.Key == "renderer").Action);
        Assert.Equal(original, RubbleRenderers(client).Single().Id);
        var expected = change == "grow" ? new[] { "m1", "m2", "m3", "m4" } : ["m2", "m1"];
        Assert.Equal(expected.Select(key => StateId(state, key)), MaterialTargets(RubbleRenderers(client).Single()));
    }

    // ResoniteLink 0.13.1 replaces only the leading elements of a list and cannot remove any. The fake client must do the
    // same, or the tests below could pass against an apply that only writes the shorter list.
    [Fact]
    public async Task TheFakeClientCannotShrinkAListLikeResoniteLink()
    {
        var client = new FakeResoniteClient();
        var renderer = await client.AddComponentAsync(client.Root.Id, "Test.Renderer", new Dictionary<string, string> { ["Materials"] = """["A","B"]""" });
        string?[] Targets() => client.Root.Components.Single(component => component.Id == renderer.Id).Members["Materials"].Elements!
            .Select(element => element.TargetId).ToArray();

        await client.SetComponentMembersAsync(renderer.Id, "Test.Renderer", new Dictionary<string, string> { ["Materials"] = """["A"]""" });
        Assert.Equal(new string?[] { "A", "B" }, Targets());
        await client.SetComponentMembersAsync(renderer.Id, "Test.Renderer", new Dictionary<string, string> { ["Materials"] = "[]" });
        Assert.Equal(new string?[] { "A", "B" }, Targets());
        await client.SetComponentMembersAsync(renderer.Id, "Test.Renderer", new Dictionary<string, string> { ["Materials"] = """["B"]""" });
        Assert.Equal(new string?[] { "B", "B" }, Targets());
    }

    [Fact]
    public async Task APlanShowsARecreateWhenADeclaredListShrinks()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("shrink-plan", RubbleWorld("shrink-plan", "m1", "m2", "m3"));

        var plan = await service.PlanApplyAsync(shrunk, new ApplyOptions(state));

        var recreate = Assert.Single(plan.Operations, operation => operation.Action == "recreate");
        Assert.Equal("renderer", recreate.Key);
        Assert.Contains("list member 'Materials' shrinks from 4 to 3 elements", recreate.Reason);
        Assert.Contains("initialFields return to their declared values and undeclared members to their type defaults", recreate.Reason);
        Assert.Equal(StateId(state, "m4"), Assert.Single(Assert.Single(recreate.Diffs!).Removed!)!.GetValue<string>());
        Assert.Equal(0, plan.Creates);
        Assert.Equal(2, plan.Updates); // the recreate and the Source that will point at the replacement
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task AShorterListInsideASyncObjectIsNotRecreated()
    {
        var document = ReloadDocument("nested-list", """
            {"schemaVersion":"1","ownership":{"key":"nested-list"},"slot":{"key":"root","name":"Nested","parent":"Root"},
             "components":[{"key":"holder","type":"Test.Holder","fields":{"Nested":{"Items":["a"]}}}]}
            """);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "nested-list.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        var holder = client.Root.Children.Single().Components.Single();
        holder.Members["Nested"] = new MemberValue("syncObject", holder.Id + ":Nested", Members: new Dictionary<string, MemberValue>
        {
            ["Items"] = new("list", holder.Id + ":Nested.Items",
                Elements: [new MemberValue("field", null, "string", JsonValue.Create("a")), new MemberValue("field", null, "string", JsonValue.Create("b"))])
        });
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("update", Assert.Single(plan.Operations, operation => operation.Key == "holder").Action);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task AComponentMovedWithAShorterListIsRelocatedNotRecreated()
    {
        var (client, service, state, moved) = await ApplyRubbleAsync("moved-shrink",
            RubbleWorld("moved-shrink", ["m1", "m2", "m3"], rendererSlot: "elsewhere"));

        var plan = await service.PlanApplyAsync(moved, new ApplyOptions(state));
        await service.ApplyAsync(moved, new ApplyOptions(state));

        Assert.Equal("relocate", Assert.Single(plan.Operations, operation => operation.Key == "renderer").Action);
        Assert.Empty(RubbleRenderers(client));
        var renderer = AllSlots(client.Root).Single(slot => slot.Name == "Elsewhere").Components.Single();
        Assert.Equal(new[] { "m1", "m2", "m3" }.Select(key => StateId(state, key)), MaterialTargets(renderer));
    }

    [Fact]
    public async Task AnotherKeyClaimingTheReplacedComponentStopsBeforeMutation()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("claimed", RubbleWorld("claimed", "m1", "m2", "m3"));
        var checkpoint = JsonNode.Parse(File.ReadAllText(state))!;
        checkpoint["components"]!["alias"] = checkpoint["components"]!["renderer"]!.DeepClone();
        File.WriteAllText(state, checkpoint.ToJsonString());

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_COMPONENT_OWNERSHIP_CONFLICT", error.Code);
        Assert.Contains("superseded", JsonSerializer.Serialize(error.Context["conflicts"]));
        Assert.Equal(0, client.Writes);
    }

    // A key whose create was interrupted has no saved ID, so apply matches it by type and position only. A longer list there
    // may belong to unmanaged content, so apply stops instead of recreating (and removing) it, in this session and after a
    // reload. Keeping the content and dropping the key from the state lets apply create the key's Component.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AKeyWithoutASavedIdNeverRecreatesAComponentMatchedByPosition(bool reload)
    {
        var name = "unsaved-id-" + reload;
        string World(bool extra) => RubbleWorld(name, "m1", "m2", "m3", "m4").Replace(
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""" +
            (extra ? """,{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m1"]}}""" : ""), StringComparison.Ordinal);
        var (client, service, state, withExtra) = await ApplyRubbleAsync(name, World(true), World(false));
        client.FailOnWrite = 1; // creating extra fails after the state saved the key without an ID
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Equal("", StateId(state, "extra"));
        var rubble = AllSlots(client.Root).Single(slot => slot.Name == "Rubble01");
        await client.AddComponentAsync(rubble.Id, "Test.Renderer", new Dictionary<string, string>
            { ["Materials"] = JsonSerializer.Serialize(new[] { "m1", "m2", "m3", "m4" }.Select(key => StateId(state, key))) });
        if (reload) client.ReloadWorld("session-reloaded");
        client.ResetWriteCounts();

        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(withExtra, new ApplyOptions(state)));
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(withExtra, new ApplyOptions(state)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", planError.Code);
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal("extra", error.Context["componentKey"]);
        var unmanaged = RubbleRenderers(client)[1];
        Assert.Equal(unmanaged.Id, Assert.Single(Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["candidateIds"])));
        Assert.Equal(4, unmanaged.Members["Materials"].Elements!.Count);
        Assert.Equal(0, client.Writes);
        var checkpoint = JsonNode.Parse(File.ReadAllText(state))!;
        checkpoint["components"]!.AsObject().Remove("extra");
        File.WriteAllText(state, checkpoint.ToJsonString());
        await service.ApplyAsync(withExtra, new ApplyOptions(state));
        Assert.Equal(3, RubbleRenderers(client).Length);
        Assert.Equal(4, RubbleRenderers(client)[1].Members["Materials"].Elements!.Count);
        client.ResetWriteCounts();
        await service.ApplyAsync(withExtra, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // Like a move, a recreate needs a saved record, so the first adopting apply updates existing content in place.
    [Fact]
    public async Task TheFirstAdoptingApplyDoesNotRecreate()
    {
        var (client, service, _, shrunk) = await ApplyRubbleAsync("adopt-shrink", RubbleWorld("adopt-shrink", "m1", "m2", "m3"));

        var plan = await service.PlanApplyAsync(shrunk, new ApplyOptions(Path.Combine(_root, "adopt-shrink.adopted.state.json"), Adopt: true));

        Assert.Equal("update", Assert.Single(plan.Operations, operation => operation.Key == "renderer").Action);
        Assert.DoesNotContain(plan.Operations, operation => operation.Action == "recreate");
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ShrinkingAListRecreatesTheComponentAndRepointsManagedReferences()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("shrink", RubbleWorld("shrink", "m1", "m2", "m3"));
        var original = RubbleRenderers(client).Single().Id;

        var applied = await service.ApplyAsync(shrunk, new ApplyOptions(state));

        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Equal(1, applied.ComponentsDeleted);
        Assert.DoesNotContain(RubbleRenderers(client), component => component.Id == original);
        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2", "m3"] });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARecreateKeepsAnAssetUrlThatAWorldSaveMigrated(bool legacyState)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var name = "recreate-asset-" + legacyState;
        string World(int materials) => $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{name}}}"},"slot":{"key":"root","name":"Assets","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
             "children":[
               {"slot":{"key":"materials","name":"Materials"},"components":[
                 {"key":"m1","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m2","type":"Test.Material","fields":{"Enabled":true}}]},
               {"slot":{"key":"crate","name":"Crate"},"components":[
                 {"key":"holder","type":"Test.AssetList","fields":{"URL":"$asset:mesh","Materials":[{{{string.Join(",", new[] { "\"$ref:m1\"", "\"$ref:m2\"" }.Take(materials))}}}]}}]}]}
            """;
        var initial = ReloadDocument(name, World(2));
        var shrunk = ReloadDocument(name + "-shrunk", World(1));
        var client = new FakeResoniteClient(initial) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(initial, new ApplyOptions(state));
        var holder = AllSlots(client.Root).Single(slot => slot.Name == "Crate").Components.Single();
        holder.Members["URL"] = holder.Members["URL"] with { Value = JsonValue.Create("resdb:///saved-crate") };
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();
        if (legacyState) RemoveRecordedAssetFields(state);

        if (legacyState)
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
            Assert.Equal("APPLY_ASSET_MIGRATION_UNVERIFIED", error.Code);
            Assert.Equal(0, client.Writes);
            return;
        }
        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        var replacement = AllSlots(client.Root).Single(slot => slot.Name == "Crate").Components.Single();
        Assert.NotEqual(holder.Id, replacement.Id);
        Assert.Equal("resdb:///saved-crate", replacement.Members["URL"].Value!.GetValue<string>());
        Assert.Equal("resdb:///saved-crate", StateAsset(state, "mesh")["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARecreatedComponentWithIdentityFieldsBindsByIdentityAfterAWorldReload()
    {
        static string World(params string[] first) => $$$"""
            {"schemaVersion":"1","ownership":{"key":"identity-recreate"},"slot":{"key":"root","name":"Rubble","parent":"Root"},
             "children":[
               {"slot":{"key":"materials","name":"Materials"},"components":[
                 {"key":"m1","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m2","type":"Test.Material","fields":{"Enabled":true}}]},
               {"slot":{"key":"rubble","name":"Rubble01"},"components":[
                 {"key":"r1","type":"Test.Renderer","fields":{"Label":"first","Materials":[{{{string.Join(",", first.Select(key => $"\"$ref:{key}\""))}}}]},"identityFields":["Label"]},
                 {"key":"r2","type":"Test.Renderer","fields":{"Label":"second","Materials":["$ref:m1"]},"identityFields":["Label"]},
                 {"key":"source","type":"Test.Source","fields":{"Target":"$ref:r1"}}]}]}
            """;
        var (client, service, state, shrunk) = await ApplyRubbleAsync("identity-recreate", World("m2"), World("m1", "m2"));

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        Assert.Equal("first", JsonNode.Parse(File.ReadAllText(state))!["components"]!["r1"]!["identityValues"]!["Label"]!.GetValue<string>());
        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "r1",
            new Dictionary<string, string[]> { ["r1"] = ["m2"], ["r2"] = ["m1"] });
    }

    [Fact]
    public async Task AMemberReferenceToARecreatedComponentMovesToTheReplacement()
    {
        static string World(params string[] materials) => RubbleWorld("member-recreate", materials).Replace(
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}},{"key":"probe","type":"Test.Source","fields":{"Target":"$member:renderer.Materials"}}""",
            StringComparison.Ordinal);
        var (client, service, state, shrunk) = await ApplyRubbleAsync("member-recreate", World("m1", "m2", "m3"), World("m1", "m2", "m3", "m4"));

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        var replacement = RubbleRenderers(client).Single();
        Assert.Equal(replacement.Members["Materials"].Id,
            Sources(client).Single(source => source.Id == StateId(state, "probe")).Members["Target"].TargetId);
        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2", "m3"] });
    }

    // The index save after removing the replaced Component is covered by ShrinkingAListRecreatesTheComponentAndRepointsManagedReferences
    // and ARecreateInterruptedAtAnyWriteConvergesInTheSameSession; the later prune also rewrites these indexes.
    [Fact]
    public async Task APruneAndARecreateOnOneSlotKeepTheIndexesBindable()
    {
        static string World(bool extra, params string[] materials) => RubbleWorld("prune-recreate", materials).Replace(
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
            (extra ? """{"key":"extra","type":"Test.Renderer","fields":{"Materials":["$ref:m4"]}},""" : "") +
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
            StringComparison.Ordinal);
        var (client, service, state, shrunk) = await ApplyRubbleAsync("prune-recreate", World(false, "m1", "m2", "m3"),
            World(true, "m1", "m2", "m3", "m4"));

        await service.ApplyAsync(shrunk, new ApplyOptions(state, Prune: true, ConfirmDeletes: true));

        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2", "m3"] });
    }

    [Fact]
    public async Task ARecreateSetsInitialFieldsAgain()
    {
        static string World(params string[] materials) => RubbleWorld("initial-recreate", materials).Replace(
            """{"key":"renderer","type":"Test.Renderer","fields":{""",
            """{"key":"renderer","type":"Test.Renderer","initialFields":{"Enabled":true},"fields":{""", StringComparison.Ordinal);
        var (client, service, state, shrunk) = await ApplyRubbleAsync("initial-recreate", World("m1", "m2", "m3"),
            World("m1", "m2", "m3", "m4"));
        var original = RubbleRenderers(client).Single();
        // Runtime logic changed an initial-only member after the Component was created.
        original.Members["Enabled"] = original.Members["Enabled"] with { Value = JsonValue.Create(false) };

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        Assert.True(RubbleRenderers(client).Single().Members["Enabled"].Value!.GetValue<bool>());
    }

    // Each recreated renderer points at the other. No replacement is a reference target until both pass, so the fields
    // phase writes both references, and the apply converges at once.
    [Fact]
    public async Task RecreatedComponentsThatReferenceEachOtherConvergeInOneApply()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("mutual", PointingRenderersWorld("mutual", 3, 2, mutual: true),
            PointingRenderersWorld("mutual", 4, 3, mutual: true));

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "r1",
            new Dictionary<string, string[]> { ["r1"] = ["m1", "m2", "m3"], ["r2"] = ["m1", "m2"] });
        var renderers = RubbleRenderers(client).ToDictionary(component => component.Id);
        Assert.Equal(StateId(state, "r2"), renderers[StateId(state, "r1")].Members["Target"].TargetId);
        Assert.Equal(StateId(state, "r1"), renderers[StateId(state, "r2")].Members["Target"].TargetId);
    }

    [Fact]
    public async Task AReplacementThatTheRuntimeRefillsIsUndone()
    {
        // 'extra' is created after the replacement in the same apply, so undoing the replacement moves it, and the undo must
        // save the indexes on its Slot again.
        var (client, service, state, shrunk) = await ApplyRubbleAsync("refilled",
            RubbleWorld("refilled", ["m1", "m2", "m3"], "rubble", watcher: true).Replace(
                """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
                """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}},{"key":"extra","type":"Test.Indexed","fields":{"Value":3}}""",
                StringComparison.Ordinal));
        var original = RubbleRenderers(client).Single().Id;
        client.RefillListsTo = 4;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal(ExitCodes.OperationFailed, error.ExitCode);
        Assert.Equal(["renderer"], Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["undone"]));
        var renderer = Assert.Single(RubbleRenderers(client));
        Assert.Equal(original, renderer.Id);
        Assert.Equal(4, renderer.Members["Materials"].Elements!.Count);
        Assert.Equal(original, StateId(state, "renderer"));
        Assert.DoesNotContain("supersededId", File.ReadAllText(state));
        Assert.Equal(original, Sources(client).Single(source => source.Id == StateId(state, "source")).Members["Target"].TargetId);
        // The watcher created in the same apply never pointed at the removed replacement.
        Assert.All(Sources(client), source => Assert.True(source.Members["Target"].TargetId is null || source.Members["Target"].TargetId == original));
        AssertSavedIndexesMatchLayout(client, state);

        client.RefillListsTo = null;
        await service.ApplyAsync(shrunk, new ApplyOptions(state));
        // The watcher was created with its initial values, which apply never writes again.
        Assert.True(Sources(client).Single(source => source.Id == StateId(state, "watcher")).Members["Enabled"].Value!.GetValue<bool>());
        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2", "m3"] });
    }

    [Fact]
    public async Task EveryReplacementThisApplyCreatedIsUndoneWhenOneDoesNotConverge()
    {
        var first = ReloadDocument("undo-all", TwoRendererWorld("undo-all", ["m1", "m2", "m3", "m4"], ["m1", "m2", "m3"]));
        var shrinkBoth = ReloadDocument("undo-all-both", TwoRendererWorld("undo-all", ["m1", "m2", "m3"], ["m1", "m2"]));
        var client = new FakeResoniteClient(first);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "undo-all.state.json");
        await service.ApplyAsync(first, new ApplyOptions(state));
        var originals = RubbleRenderers(client).Select(component => component.Id).ToArray();
        client.RefillListsTo = 3; // r1's replacement keeps 3 as declared, r2's replacement cannot shrink to 2

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrinkBoth, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal(["r1", "r2"], Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["undone"]));
        Assert.Equal(originals, RubbleRenderers(client).Select(component => component.Id));
        Assert.Equal(originals, new[] { StateId(state, "r1"), StateId(state, "r2") });
        Assert.Equal(originals[0], Sources(client).Single(source => source.Id == StateId(state, "source")).Members["Target"].TargetId);
        Assert.DoesNotContain("supersededId", File.ReadAllText(state));
        AssertSavedIndexesMatchLayout(client, state);
    }

    // An undo restores the record from just before the replacement was created. Evidence that the asset phase forgot for a
    // re-imported asset stays forgotten, so a world save cannot make a later apply adopt the old asset's URL; evidence that
    // nothing forgot stays, so the saved URL is adopted.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUndoKeepsTheAssetEvidenceThatThisApplyForgot(bool reimported)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var name = "undo-asset-" + reimported;
        string World(int materials) => $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{name}}}"},"slot":{"key":"root","name":"Assets","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
             "children":[
               {"slot":{"key":"materials","name":"Materials"},"components":[
                 {"key":"m1","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m2","type":"Test.Material","fields":{"Enabled":true}}]},
               {"slot":{"key":"crate","name":"Crate"},"components":[
                 {"key":"holder","type":"Test.AssetList","fields":{"URL":"$asset:mesh","Materials":[{{{string.Join(",", new[] { "\"$ref:m1\"", "\"$ref:m2\"" }.Take(materials))}}}]}}]}]}
            """;
        var initial = ReloadDocument(name, World(2));
        var shrunk = ReloadDocument(name + "-shrunk", World(1));
        var client = new FakeResoniteClient(initial) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(initial, new ApplyOptions(state));
        if (reimported) await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "second crate");
        client.RefillListsTo = 2;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        var assetFields = JsonNode.Parse(File.ReadAllText(state))!["components"]!["holder"]!["assetFields"]!.AsObject();
        Assert.Equal(!reimported, assetFields.ContainsKey("URL"));
        // A world save moves the original Component's URL to resdb.
        var holder = AllSlots(client.Root).Single(slot => slot.Name == "Crate").Components.Single();
        holder.Members["URL"] = holder.Members["URL"] with { Value = JsonValue.Create("resdb:///saved-crate") };
        client.ReloadWorld("session-saved");
        client.RefillListsTo = null;
        await service.ApplyAsync(shrunk, new ApplyOptions(state));
        var expected = reimported ? "local://machine/asset-2" : "resdb:///saved-crate";
        Assert.Equal(expected, AllSlots(client.Root).Single(slot => slot.Name == "Crate").Components.Single().Members["URL"].Value!.GetValue<string>());
        Assert.Equal(expected, StateAsset(state, "mesh")["url"]!.GetValue<string>());
    }

    // ResoniteLink applies a list of values the same way as a list of references, so a shorter list of values is recreated
    // too, and a replacement whose values the runtime refills is undone.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AShorterListOfValuesIsRecreatedLikeAListOfReferences(bool refilled)
    {
        var name = "value-list-" + refilled;
        string World(params int[] weights) => $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{name}}}"},"slot":{"key":"root","name":"Weights","parent":"Root"},
             "components":[{"key":"blend","type":"Test.Blend","fields":{"Weights":[{{{string.Join(",", weights)}}}]}}]}
            """;
        var initial = ReloadDocument(name, World(1, 2, 3));
        var shrunk = ReloadDocument(name + "-shrunk", World(1, 2));
        var client = new FakeResoniteClient(initial);
        client.ListMembers.Add("Weights");
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(initial, new ApplyOptions(state));
        var original = client.Root.Children.Single().Components.Single().Id;
        client.RefillListsTo = refilled ? 3 : null;

        var plan = await service.PlanApplyAsync(shrunk, new ApplyOptions(state));
        var error = await Record.ExceptionAsync(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("recreate", Assert.Single(plan.Operations, operation => operation.Key == "blend").Action);
        var blend = client.Root.Children.Single().Components.Single();
        if (refilled)
        {
            Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", Assert.IsType<RLoopException>(error).Code);
            Assert.Equal(original, blend.Id);
            return;
        }
        Assert.Null(error);
        Assert.NotEqual(original, blend.Id);
        Assert.Equal(new[] { 1.0, 2.0 }, blend.Members["Weights"].Elements!.Select(element => element.Value!.GetValue<double>()));
        client.ResetWriteCounts();
        await service.ApplyAsync(shrunk, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
    }

    // An interrupted recreate resumes only while the document declares the key on the same Slot. It never matches the key
    // by type or position on its old Slot, even beside a same-type sibling. Declaring the key there again finishes it.
    [Theory]
    [InlineData("undeclared")]
    [InlineData("moved")]
    [InlineData("moved-beside-a-sibling")]
    public async Task ARecreateInterruptedBeforeTheKeyLeavesItsSlotStops(string change)
    {
        var name = "leaves-" + change;
        var sibling = change == "moved-beside-a-sibling";
        var key = sibling ? "r1" : "renderer";
        var (client, service, state, shrunk) = sibling
            ? await ApplyRubbleAsync(name, TwoRendererWorld(name, ["m1", "m2", "m3"], ["m1", "m2", "m3"]),
                TwoRendererWorld(name, ["m1", "m2", "m3", "m4"], ["m1", "m2", "m3"]))
            : await ApplyRubbleAsync(name, RubbleWorld(name, "m1", "m2", "m3"));
        client.FailOnWrite = 1; // creating the replacement fails after state saved supersededId
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailOnWrite = null;
        const string r1 = """{"key":"r1","type":"Test.Renderer","fields":{"Materials":["$ref:m1","$ref:m2","$ref:m3"]}}""";
        var changed = ReloadDocument(name + "-changed", change switch
        {
            "undeclared" => RubbleWorld(name, [], rendererSlot: null),
            "moved" => RubbleWorld(name, ["m1", "m2", "m3"], rendererSlot: "elsewhere"),
            _ => TwoRendererWorld(name, ["m1", "m2", "m3"], ["m1", "m2", "m3"]).Replace(r1 + ",", "", StringComparison.Ordinal).Replace(
                """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:r1"}}]}""",
                """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:r1"}}]},{"slot":{"key":"elsewhere","name":"Elsewhere"},"components":[""" + r1 + "]}",
                StringComparison.Ordinal)
        });
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(changed, new ApplyOptions(state)));

        Assert.Equal("APPLY_RECREATE_INTERRUPTED", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal("key-not-declared", error.Context["reason"]);
        Assert.Equal(key, error.Context["componentKey"]);
        Assert.Equal(0, client.Writes);
        // The suggested recovery: declare the key on its Slot again to finish the recreate, then remove it with --prune.
        await service.ApplyAsync(shrunk, new ApplyOptions(state));
        Assert.DoesNotContain("supersededId", File.ReadAllText(state));
        if (sibling) return;
        var removed = ReloadDocument(name + "-removed", RubbleWorld(name, [], rendererSlot: null));
        await service.ApplyAsync(removed, new ApplyOptions(state, Prune: true, ConfirmDeletes: true));
        Assert.Empty(RubbleRenderers(client));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("remove")]
    [InlineData("removed")]
    public async Task APlanDuringAnInterruptedRecreateExplainsHowItResumes(string failedStep)
    {
        var name = "resume-plan-" + failedStep;
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name, RubbleWorld(name, "m1", "m2", "m3"));
        var original = RubbleRenderers(client).Single().Id;
        if (failedStep == "create") client.FailOnWrite = 1;
        else if (failedStep == "remove") client.FailRemovingComponent = original;
        else client.LoseResponseOnWrite = 3; // removing the replaced Component lands, and its response is lost
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailOnWrite = null;
        client.FailRemovingComponent = null;
        client.LoseResponseOnWrite = null;
        client.ResetWriteCounts();
        var checkpoint = File.ReadAllBytes(state);

        var plan = await service.PlanApplyAsync(shrunk, new ApplyOptions(state));

        var recreate = Assert.Single(plan.Operations, operation => operation.Action == "recreate");
        Assert.StartsWith("resumes an interrupted recreate:", recreate.Reason);
        Assert.Contains(failedStep switch
        {
            "create" => "creates the replacement",
            "remove" => $"removes the replaced Component '{original}'",
            _ => "the replaced Component is already removed"
        }, recreate.Reason);
        Assert.Equal(checkpoint, File.ReadAllBytes(state));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task AResumedReplacementThatPassesStaysWhenAnotherRecreateIsUndone()
    {
        var first = ReloadDocument("resumed-stays", TwoRendererWorld("resumed-stays", ["m1", "m2", "m3", "m4"], ["m1", "m2", "m3"]));
        var shrinkFirst = ReloadDocument("resumed-stays-first", TwoRendererWorld("resumed-stays", ["m1", "m2", "m3"], ["m1", "m2", "m3"]));
        var shrinkBoth = ReloadDocument("resumed-stays-both", TwoRendererWorld("resumed-stays", ["m1", "m2", "m3"], ["m1", "m2"]));
        var client = new FakeResoniteClient(first);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "resumed-stays.state.json");
        await service.ApplyAsync(first, new ApplyOptions(state));
        var r1 = StateId(state, "r1");
        var r2 = StateId(state, "r2");
        client.FailRemovingComponent = r1; // r1's recreate stops after the references moved to its replacement
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrinkFirst, new ApplyOptions(state)));
        client.FailRemovingComponent = null;
        var replacement = StateId(state, "r1");
        Assert.NotEqual(r1, replacement);
        client.RefillListsTo = 3; // r1's replacement keeps 3 as declared, r2's replacement cannot shrink to 2

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrinkBoth, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal(["r2"], Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["undone"]));
        Assert.Equal(["r1"], Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["inProgress"]));
        var checkpoint = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal(replacement, checkpoint["components"]!["r1"]!["id"]!.GetValue<string>());
        Assert.Equal(r1, checkpoint["components"]!["r1"]!["supersededId"]!.GetValue<string>());
        Assert.Equal(r2, checkpoint["components"]!["r2"]!["id"]!.GetValue<string>());
        Assert.Null(checkpoint["components"]!["r2"]!["supersededId"]);
        Assert.Equal(new[] { r1, r2, replacement }.Order(StringComparer.Ordinal),
            RubbleRenderers(client).Select(component => component.Id).Order(StringComparer.Ordinal));

        client.RefillListsTo = null;
        await service.ApplyAsync(shrinkFirst, new ApplyOptions(state));
        await AssertListShrinkConvergedAsync(client, service, shrinkFirst, state, "r1",
            new Dictionary<string, string[]> { ["r1"] = ["m1", "m2", "m3"], ["r2"] = ["m1", "m2", "m3"] });
    }

    // A replacement that something already references cannot shrink in place and must not be undone, so a declaration that
    // became shorter during the recreate stops before mutation. Finishing the recreate first, then shortening, converges.
    [Fact]
    public async Task AShorterDeclarationStopsWhileSomethingReferencesTheResumedReplacement()
    {
        var (client, service, state, three) = await ApplyRubbleAsync("shrank-again", RubbleWorld("shrank-again", "m1", "m2", "m3"));
        client.FailRemovingComponent = RubbleRenderers(client).Single().Id; // the Source already points at the replacement
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.FailRemovingComponent = null;
        var two = ReloadDocument("shrank-again-two", RubbleWorld("shrank-again", "m1", "m2"));
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(two, new ApplyOptions(state)));

        Assert.Equal("APPLY_RECREATE_INTERRUPTED", error.Code);
        Assert.Equal("declaration-shrank", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        var target = Sources(client).Single(source => source.Id == StateId(state, "source")).Members["Target"].TargetId;
        Assert.Contains(RubbleRenderers(client), renderer => renderer.Id == target);
        await service.ApplyAsync(three, new ApplyOptions(state));
        await service.ApplyAsync(two, new ApplyOptions(state));
        await AssertListShrinkConvergedAsync(client, service, two, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2"] });
    }

    // When the replaced Component is already gone, a declaration that shrank again starts a new recreate from the replacement.
    [Fact]
    public async Task AShorterDeclarationAfterTheReplacedComponentIsGoneRecreatesAgain()
    {
        var (client, service, state, three) = await ApplyRubbleAsync("shrank-after-removal", RubbleWorld("shrank-after-removal", "m1", "m2", "m3"));
        client.LoseResponseOnWrite = 3; // writes: the replacement, the Source, then the removal of the replaced Component
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(three, new ApplyOptions(state)));
        client.LoseResponseOnWrite = null;
        Assert.Single(RubbleRenderers(client));
        Assert.Contains("supersededId", File.ReadAllText(state));
        var two = ReloadDocument("shrank-after-removal-two", RubbleWorld("shrank-after-removal", "m1", "m2"));

        await service.ApplyAsync(two, new ApplyOptions(state));

        await AssertListShrinkConvergedAsync(client, service, two, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2"] });
    }

    // r2 points at r1. r2's recreate resumes after its replaced Component failed to go and its replacement passes again;
    // r1's new replacement does not converge and is undone. r2's replacement never pointed at r1's replacement.
    [Fact]
    public async Task AResumedReplacementNeverPointsAtAnUndoneReplacement()
    {
        var (client, service, state, shrinkSecond) = await ApplyRubbleAsync("resumed-target", PointingRenderersWorld("resumed-target", 4, 3),
            PointingRenderersWorld("resumed-target", 4, 4));
        var r1 = StateId(state, "r1");
        client.FailRemovingComponent = StateId(state, "r2"); // r2's recreate stops after its replacement passed
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrinkSecond, new ApplyOptions(state)));
        client.FailRemovingComponent = null;
        var shrinkBoth = ReloadDocument("resumed-target-both", PointingRenderersWorld("resumed-target", 2, 3));
        client.RefillListsTo = 3; // r1's new replacement cannot shrink to 2; r2's replacement keeps 3 as declared

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrinkBoth, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Equal(["r1"], Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["undone"]));
        Assert.Equal(["r2"], Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["inProgress"]));
        Assert.Equal(r1, RubbleRenderers(client).Single(renderer => renderer.Id == StateId(state, "r2")).Members["Target"].TargetId);
        client.RefillListsTo = null;
        await service.ApplyAsync(shrinkBoth, new ApplyOptions(state));
        await AssertListShrinkConvergedAsync(client, service, shrinkBoth, state, "r1",
            new Dictionary<string, string[]> { ["r1"] = ["m1", "m2"], ["r2"] = ["m1", "m2", "m3"] });
    }

    // r1 references a Component that the same apply creates later, so its replacement is created without initial values and
    // first written when a later apply resumes it; that apply must read it back too.
    [Fact]
    public async Task AResumedReplacementIsReadBackAfterItsFieldsAreFirstWritten()
    {
        var (client, service, state, shrunk, _, _) = await ApplyScenarioAsync("written-on-resume", "shared-slot");
        var originals = RubbleRenderers(client).Select(component => component.Id).Order(StringComparer.Ordinal).ToArray();
        client.FailOnWrite = 4; // writes: the Elsewhere and Late Slots, r1's replacement, then r2's replacement
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Empty(RubbleRenderers(client).Single(component => component.Id == StateId(state, "r1")).Members["Materials"].Elements!);
        client.RefillListsTo = 4;

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
        Assert.Contains("r1", Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["undone"]));
        Assert.Equal(originals, RubbleRenderers(client).Select(component => component.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("supersededId", File.ReadAllText(state));
    }

    // Stops the undo at each of its two removals. The next apply resumes what it can, undoes r2 again, and once the runtime
    // stops refilling the list, the recreates finish.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUndoInterruptedAtEitherRemovalResumesOnTheNextApply(bool responseLost)
    {
        for (var removal = 1; removal <= 2; removal++)
        {
            var name = $"undo-interrupted-{responseLost}-{removal}";
            var first = ReloadDocument(name, TwoRendererWorld(name, ["m1", "m2", "m3", "m4"], ["m1", "m2", "m3"]));
            var shrinkBoth = ReloadDocument(name + "-both", TwoRendererWorld(name, ["m1", "m2", "m3"], ["m1", "m2"]));
            var client = new FakeResoniteClient(first);
            var service = new WorldService(client);
            var state = Path.Combine(_root, name + ".state.json");
            await service.ApplyAsync(first, new ApplyOptions(state));
            client.RefillListsTo = 3; // r1's replacement keeps 3 as declared, r2's replacement cannot shrink to 2
            client.ResetWriteCounts();
            // Writes 1 and 2 create the replacements, and the undo removes them with writes 3 and 4.
            if (responseLost) client.LoseResponseOnWrite = 2 + removal;
            else client.FailOnWrite = 2 + removal;
            await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrinkBoth, new ApplyOptions(state)));
            client.LoseResponseOnWrite = null;
            client.FailOnWrite = null;

            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrinkBoth, new ApplyOptions(state)));

            Assert.Equal("APPLY_LIST_SHRINK_NOT_CONVERGED", error.Code);
            Assert.Contains("r2", Assert.IsAssignableFrom<IEnumerable<string>>(error.Context["undone"]));
            AssertSavedIndexesMatchLayout(client, state);
            client.RefillListsTo = null;
            await service.ApplyAsync(shrinkBoth, new ApplyOptions(state));
            await AssertListShrinkConvergedAsync(client, service, shrinkBoth, state, "r1",
                new Dictionary<string, string[]> { ["r1"] = ["m1", "m2", "m3"], ["r2"] = ["m1", "m2"] }, $"removal {removal}: ");
        }
    }

    // A Component that no key owns may be the replacement whose create response was lost, so apply stops without changing
    // it, even when the replacement never landed. The lists do not tell them apart either: an empty one also looks like a
    // replacement created without initial values.
    [Fact]
    public async Task AnUnmanagedSameTypeComponentStopsAResumeWithoutBeingChanged()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("unmanaged-candidate", RubbleWorld("unmanaged-candidate", "m1", "m2", "m3"));
        var rubble = AllSlots(client.Root).Single(slot => slot.Name == "Rubble01");
        var unmanaged = await client.AddComponentAsync(rubble.Id, "Test.Renderer", new Dictionary<string, string>());
        client.ResetWriteCounts();
        client.FailOnWrite = 1; // creating the replacement fails before it lands
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailOnWrite = null;
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("replacement-unverified", error.Context["reason"]);
        var candidate = Assert.Single(JsonSerializer.SerializeToNode(error.Context["candidates"])!.AsArray())!;
        Assert.Equal(unmanaged.Id, candidate["id"]!.GetValue<string>());
        Assert.Equal(0, candidate["lists"]!["Materials"]!.GetValue<int>());
        Assert.Equal(0, client.Writes);
        Assert.Contains(RubbleRenderers(client), component => component.Id == unmanaged.Id);
    }

    // A same-type Component moving to another Slot leaves its old instance on the Slot until apply removes it. Its key owns
    // that instance, so it is never taken for a replacement whose create did not land.
    [Fact]
    public async Task AMovingSameTypeComponentIsNotTakenForALostReplacement()
    {
        const string wanderer = """{"key":"wanderer","type":"Test.Renderer","fields":{"Materials":["$ref:m1"]}}""";
        const string source = """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""";
        static string World(bool moved, params string[] materials) => moved
            ? RubbleWorld("moving-sibling", materials).Replace(source + "]}",
                source + """]},{"slot":{"key":"elsewhere","name":"Elsewhere"},"components":[""" + wanderer + "]}", StringComparison.Ordinal)
            : RubbleWorld("moving-sibling", materials).Replace(source, source + "," + wanderer, StringComparison.Ordinal);
        var (client, service, state, shrunk) = await ApplyRubbleAsync("moving-sibling", World(true, "m1", "m2", "m3"),
            World(false, "m1", "m2", "m3", "m4"));
        client.FailOnWrite = 2; // writes: the Elsewhere Slot, then the replacement, whose create fails before it lands
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.FailOnWrite = null;
        Assert.Equal(2, RubbleRenderers(client).Length);

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2", "m3"] });
        Assert.Single(AllSlots(client.Root).Single(slot => slot.Name == "Elsewhere").Components);
    }

    // The watcher, created in the same apply, points at a replacement, so it is created without that reference and its
    // checkpoint keeps only the references its create wrote. After its create response is lost, the re-run binds it
    // instead of creating another.
    [Fact]
    public async Task ALostCreateOfAComponentPointingAtAReplacementIsNotDuplicated()
    {
        var (client, service, state, shrunk) = await ApplyRubbleAsync("lost-watcher",
            RubbleWorld("lost-watcher", ["m1", "m2", "m3"], "rubble", watcher: true));
        client.LoseResponseOnWrite = 3; // writes: the Late Slot, the replacement, then the watcher
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
        client.LoseResponseOnWrite = null;
        Assert.Single(AllSlots(client.Root).Single(slot => slot.Name == "Late").Components);
        Assert.Equal("", StateId(state, "watcher"));

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        var watcher = Assert.Single(AllSlots(client.Root).Single(slot => slot.Name == "Late").Components);
        Assert.Equal(StateId(state, "watcher"), watcher.Id);
        Assert.True(watcher.Members["Enabled"].Value!.GetValue<bool>());
        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2", "m3"] });
    }

    // Recreating gives the Component and its members new IDs, so apply stops for every reference that it does not re-point:
    // one to the Component, to one of its members or to a list element, and one from a member that a managed Component
    // leaves undeclared.
    [Theory]
    [InlineData("component")]
    [InlineData("member")]
    [InlineData("element")]
    [InlineData("undeclared-member")]
    public async Task AnUnmanagedReferenceToTheReplacedComponentStopsBeforeMutation(string reference)
    {
        var name = "referenced-" + reference;
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name, RubbleWorld(name, "m1", "m2", "m3"));
        var rubble = AllSlots(client.Root).Single(slot => slot.Name == "Rubble01");
        var renderer = RubbleRenderers(client).Single();
        var target = reference switch
        {
            "member" => renderer.Members["Materials"].Id!,
            "element" => renderer.Members["Materials"].Elements![1].Id!,
            _ => renderer.Id
        };
        string referencedBy;
        if (reference == "undeclared-member")
        {
            // The managed Source declares only Target; its undeclared member Spare points at the renderer too.
            var source = Sources(client).Single(component => component.Id == StateId(state, "source"));
            source.Members["Spare"] = new MemberValue("reference", source.Id + ":Spare", TargetId: target);
            referencedBy = source.Id;
        }
        else
        {
            referencedBy = (await client.AddComponentAsync(rubble.Id, "Test.Source", new Dictionary<string, string> { ["Target"] = target })).Id;
        }
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        Assert.Equal("APPLY_LIST_SHRINK_REFERENCED", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        var found = Assert.Single(JsonSerializer.SerializeToNode(error.Context["references"])!.AsArray())!;
        Assert.Equal(referencedBy, found["referencedBy"]!.GetValue<string>());
        Assert.Equal(target, found["targetId"]!.GetValue<string>());
        Assert.Equal(0, client.Writes);
    }

    // A partial syncObject declaration writes only its declared children, so apply re-points a reference there only when the
    // declaration puts a selector at the same path; otherwise it stops.
    [Theory]
    [InlineData("undeclared-child")]
    [InlineData("declared-child")]
    [InlineData("no-reference")]
    public async Task APartlyDeclaredSyncObjectIsCheckedChildByChild(string child)
    {
        var name = "partial-" + child;
        var snapPositions = child == "declared-child"
            ? "{\"LocalSpace\":\"$slot:rubble\",\"Target\":\"$ref:renderer\"}"
            : "{\"LocalSpace\":\"$slot:rubble\"}";
        string World(params string[] materials) => RubbleWorld(name, materials).Replace(
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
            """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}},{"key":"consumer","type":"Test.Slider","fields":{"SnapPositions":""" +
            snapPositions + "}}", StringComparison.Ordinal);
        var (client, service, state, shrunk) = await ApplyRubbleAsync(name, World("m1", "m2", "m3"), World("m1", "m2", "m3", "m4"));
        var rubble = AllSlots(client.Root).Single(slot => slot.Name == "Rubble01");
        var renderer = RubbleRenderers(client).Single();
        var consumer = rubble.Components.Single(component => component.Type == "Test.Slider");
        // The fake writes a JSON object as one field value, so give the consumer the runtime's syncObject shape.
        var children = new Dictionary<string, MemberValue> { ["LocalSpace"] = new("reference", consumer.Id + ":LocalSpace", TargetId: rubble.Id) };
        if (child != "no-reference") children["Target"] = new("reference", consumer.Id + ":Target", TargetId: renderer.Id);
        consumer.Members["SnapPositions"] = new MemberValue("syncObject", consumer.Id + ":SnapPositions", Members: children);
        client.ResetWriteCounts();

        var error = await Record.ExceptionAsync(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));

        if (child == "undeclared-child")
        {
            Assert.Equal("APPLY_LIST_SHRINK_REFERENCED", Assert.IsType<RLoopException>(error).Code);
            Assert.Equal(0, client.Writes);
            Assert.Contains(RubbleRenderers(client), component => component.Id == renderer.Id);
            return;
        }
        Assert.Null(error);
        Assert.DoesNotContain(RubbleRenderers(client), component => component.Id == renderer.Id);
        if (child == "declared-child")
            Assert.Contains(StateId(state, "renderer"), consumer.Members["SnapPositions"].Value!.ToJsonString());
    }

    // A Source moving to another Slot leaves its old instance behind until apply removes it, so that instance's reference to
    // the replaced Component does not stop the recreate.
    [Fact]
    public async Task ASourceMovingAwayDoesNotStopTheRecreateItPointsAt()
    {
        const string source = """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""";
        static string World(bool moved, params string[] materials) => moved
            ? RubbleWorld("moving-source", materials).Replace("," + source + "]}",
                """]},{"slot":{"key":"elsewhere","name":"Elsewhere"},"components":[""" + source + "]}", StringComparison.Ordinal)
            : RubbleWorld("moving-source", materials);
        var (client, service, state, shrunk) = await ApplyRubbleAsync("moving-source", World(true, "m1", "m2", "m3"),
            World(false, "m1", "m2", "m3", "m4"));

        await service.ApplyAsync(shrunk, new ApplyOptions(state));

        await AssertListShrinkConvergedAsync(client, service, shrunk, state, "renderer",
            new Dictionary<string, string[]> { ["renderer"] = ["m1", "m2", "m3"] });
    }

    // Stops the shrinking apply at each write in turn, before the write lands or after it lands with its response lost.
    // The next apply in the same session must converge, or stop on replacement-unverified before any write until the
    // recovery in its suggestions. Convergence covers the renderers, the Sources, the references and the saved indexes; a
    // moved Component's source that an interrupted move leaves behind is an existing upstream issue and not checked.
    [Theory]
    [InlineData("one-recreate", false)]
    [InlineData("one-recreate", true)]
    [InlineData("shared-slot", false)]
    [InlineData("shared-slot", true)]
    public async Task ARecreateInterruptedAtAnyWriteConvergesInTheSameSession(string scenario, bool responseLost)
    {
        for (var write = 1; ; write++)
        {
            var (client, service, state, shrunk, referenced, expected) = await ApplyScenarioAsync($"{scenario}-resume-{responseLost}-{write}", scenario);
            var before = RubbleRenderers(client).Select(component => component.Id).ToHashSet();
            if (responseLost) client.LoseResponseOnWrite = write;
            else client.FailOnWrite = write;

            var interrupted = await InterruptedAsync(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
            client.LoseResponseOnWrite = null;
            client.FailOnWrite = null;
            var created = RubbleRenderers(client).Select(component => component.Id).Where(id => !before.Contains(id)).ToArray();
            client.ResetWriteCounts();
            try
            {
                await service.ApplyAsync(shrunk, new ApplyOptions(state));
            }
            catch (RLoopException error) when (error.Code == "APPLY_RECREATE_INTERRUPTED" && Equals(error.Context["reason"], "replacement-unverified"))
            {
                Assert.True(responseLost, $"write {write}: only a lost create response can leave an unverified replacement.");
                Assert.True(client.Writes == 0, $"write {write}: the stopped apply wrote {client.Writes} time(s).");
                // The recovery in the suggestions: remove the candidate whose lists match the declaration, or whose lists are
                // all empty when the declaration references a Component that the same apply creates later.
                var declared = expected[(string)error.Context["componentKey"]!];
                var candidate = Assert.Single(JsonSerializer.SerializeToNode(error.Context["candidates"])!.AsArray(), candidate =>
                    candidate!["lists"]!["Materials"]!.GetValue<int>() is var count &&
                    (count == declared.Length || declared.Contains("late") && count == 0))!["id"]!.GetValue<string>();
                Assert.Contains(candidate, created);
                await client.RemoveComponentAsync(candidate);
                await service.ApplyAsync(shrunk, new ApplyOptions(state));
            }

            await AssertListShrinkConvergedAsync(client, service, shrunk, state, referenced, expected, $"write {write}: ");
            if (!interrupted) break;
        }
    }

    // A world reload while a recreate is in progress must stop plan and apply before any write and keep the state, or the
    // apply converges. With no other key of the same type on the Slot, the recovery in the suggestions converges; with one,
    // the suggestions say that apply cannot tell the Components apart.
    [Theory]
    [InlineData("one-recreate", false)]
    [InlineData("one-recreate", true)]
    [InlineData("one-recreate-unmanaged", false)]
    [InlineData("one-recreate-unmanaged", true)]
    [InlineData("shared-slot", false)]
    [InlineData("shared-slot", true)]
    public async Task AWorldReloadDuringARecreateStopsBeforeAnyWrite(string scenario, bool responseLost)
    {
        for (var write = 1; ; write++)
        {
            var (client, service, state, shrunk, referenced, expected) = await ApplyScenarioAsync($"{scenario}-reload-{responseLost}-{write}", scenario);
            if (scenario == "one-recreate-unmanaged")
            {
                var slot = AllSlots(client.Root).Single(slot => slot.Name == "Rubble01");
                await client.AddComponentAsync(slot.Id, "Test.Indexed", new Dictionary<string, string> { ["Value"] = "999" });
            }
            client.ResetWriteCounts();
            if (responseLost) client.LoseResponseOnWrite = write;
            else client.FailOnWrite = write;

            var interrupted = await InterruptedAsync(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
            client.LoseResponseOnWrite = null;
            client.FailOnWrite = null;
            var inProgress = File.ReadAllText(state).Contains("\"supersededId\"", StringComparison.Ordinal);
            client.ReloadWorld("session-reloaded");
            client.ResetWriteCounts();

            if (inProgress)
            {
                var checkpoint = File.ReadAllBytes(state);
                var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(shrunk, new ApplyOptions(state)));
                Assert.Equal("session-changed", planError.Context["reason"]);
                RLoopException error = null!;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
                    Assert.Equal("APPLY_RECREATE_INTERRUPTED", error.Code);
                    Assert.Equal("session-changed", error.Context["reason"]);
                }
                Assert.True(client.Writes == 0, $"write {write}: the stopped apply wrote {client.Writes} time(s).");
                Assert.Equal(checkpoint, File.ReadAllBytes(state));
                var candidates = JsonSerializer.SerializeToNode(error.Context["candidates"])!.AsArray();
                if (scenario == "shared-slot")
                {
                    Assert.Contains("cannot tell the recreate's Components apart", Assert.Single(error.Suggestions));
                }
                else
                {
                    // The recovery in the suggestions: keep the candidate that something references, remove the other, and
                    // clear the recreate in the state.
                    await RecoverRecreateAfterReloadAsync(client, state, error);
                    var saved = JsonNode.Parse(File.ReadAllText(state))!;
                    var unverified = saved["components"]!.AsObject().Where(pair => pair.Value!["componentIndex"]?.GetValue<int>() == -1)
                        .Select(pair => pair.Key).ToArray();
                    if (unverified.Length > 0)
                    {
                        client.ResetWriteCounts();
                        var stopped = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(shrunk, new ApplyOptions(state)));
                        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", stopped.Code);
                        Assert.Equal(0, client.Writes);
                        if (scenario == "one-recreate-unmanaged") AssertUnmanagedIndexedValue(client);
                        var slot = AllSlots(client.Root).Single(slot => slot.Name == "Rubble01");
                        foreach (var key in unverified)
                        {
                            Assert.Contains(key, new[] { "source", "v1" });
                            var identified = key == "source"
                                ? Assert.Single(slot.Components, component => component.Type == "Test.Source")
                                : Assert.Single(slot.Components, component => component.Type == "Test.Indexed" &&
                                    component.Members["Value"].Value!.GetValue<int>() == 1);
                            if (key == "source")
                            {
                                var kept = candidates.FirstOrDefault(candidate => candidate!["referencedBy"]!.AsArray().Count > 0) ?? candidates[0]!;
                                Assert.Equal(kept["id"]!.GetValue<string>(), identified.Members["Target"].TargetId);
                            }
                            await ConfirmRecoveryIndexAsync(client, state, key, identified);
                        }
                    }
                    await service.ApplyAsync(shrunk, new ApplyOptions(state));
                    await AssertListShrinkConvergedAsync(client, service, shrunk, state, referenced, expected, $"write {write}: ");
                }
            }
            else
            {
                await service.ApplyAsync(shrunk, new ApplyOptions(state));
                await AssertListShrinkConvergedAsync(client, service, shrunk, state, referenced, expected, $"write {write}: ");
            }
            if (scenario == "one-recreate-unmanaged") AssertUnmanagedIndexedValue(client);
            if (!interrupted) break;
        }
    }

    // Cancelling at each progress notification leaves states that the two injected failures cannot, because a cancel can land
    // right after a checkpoint. The next apply in the same session must converge.
    [Fact]
    public async Task ARecreateCancelledAtAnyProgressConvergesInTheSameSession()
    {
        for (var notification = 1; ; notification++)
        {
            var (client, service, state, shrunk, referenced, expected) = await ApplyScenarioAsync($"cancel-{notification}", "one-recreate");
            using var cancellation = new CancellationTokenSource();
            var seen = 0;
            await Record.ExceptionAsync(() => service.ApplyAsync(shrunk,
                new ApplyOptions(state, Progress: _ => { if (++seen == notification) cancellation.Cancel(); }), cancellation.Token));

            await service.ApplyAsync(shrunk, new ApplyOptions(state));

            await AssertListShrinkConvergedAsync(client, service, shrunk, state, referenced, expected, $"notification {notification}: ");
            if (seen < notification) break;
        }
    }

    // A Materials slot with m1..m4, and Rubble01 whose renderer declares its Materials and whose managed Source points at
    // the renderer. With watcher, a Source on a later Slot also points at the renderer and sets an initial-only member.
    private static string RubbleWorld(string ownership, params string[] materials) =>
        RubbleWorld(ownership, materials, "rubble");

    private static string RubbleWorld(string ownership, string[] materials, string? rendererSlot, bool watcher = false)
    {
        var list = string.Join(",", materials.Select(material => $"\"$ref:{material}\""));
        var renderer = rendererSlot is null ? "" :
            $$$"""{"key":"renderer","type":"Test.Renderer","fields":{"Materials":[{{{list}}}]}},""";
        var source = rendererSlot is null ? "" : """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""";
        var rubble = rendererSlot == "elsewhere"
            ? $$$"""{"slot":{"key":"rubble","name":"Rubble01"},"components":[{{{source}}}]},{"slot":{"key":"elsewhere","name":"Elsewhere"},"components":[{{{renderer.TrimEnd(',')}}}]}"""
            : $$$"""{"slot":{"key":"rubble","name":"Rubble01"},"components":[{{{renderer}}}{{{source}}}]}""";
        var late = watcher ? """,{"slot":{"key":"late","name":"Late"},"components":[{"key":"watcher","type":"Test.Source","initialFields":{"Enabled":true},"fields":{"Target":"$ref:renderer"}}]}""" : "";
        return $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{ownership}}}"},"slot":{"key":"root","name":"Rubble","parent":"Root"},
             "children":[
               {"slot":{"key":"materials","name":"Materials"},"components":[
                 {"key":"m1","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m2","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m3","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m4","type":"Test.Material","fields":{"Enabled":true}}]},
               {{{rubble}}}{{{late}}}]}
            """;
    }

    // Two same-type renderers on one Slot without identityFields, so a world reload binds them by their saved indexes.
    // In the shared-slot scenario a sibling moves away, a same-type sibling is no longer declared, and r1 also gets a material
    // that is created later in the same apply.
    private static string TwoRendererWorld(string ownership, string[] first, string[] second, bool shared = false, bool shrunk = false)
    {
        static string List(IEnumerable<string> keys) => string.Join(",", keys.Select(key => $"\"$ref:{key}\""));
        var siblings = !shared ? "" : shrunk ? "" :
            """,{"key":"mover","type":"Test.Indexed","fields":{"Value":7}},{"key":"leftover","type":"Test.Renderer","fields":{"Materials":["$ref:m4"]}}""";
        var extra = !shared || !shrunk ? "" :
            """,{"slot":{"key":"elsewhere","name":"Elsewhere"},"components":[{"key":"mover","type":"Test.Indexed","fields":{"Value":7}}]},{"slot":{"key":"late-slot","name":"Late"},"components":[{"key":"late","type":"Test.Material","fields":{"Enabled":true}}]}""";
        return $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{ownership}}}"},"slot":{"key":"root","name":"Rubble","parent":"Root"},
             "children":[
               {"slot":{"key":"materials","name":"Materials"},"components":[
                 {"key":"m1","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m2","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m3","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m4","type":"Test.Material","fields":{"Enabled":true}}]},
               {"slot":{"key":"rubble","name":"Rubble01"},"components":[
                 {"key":"r1","type":"Test.Renderer","fields":{"Materials":[{{{List(first)}}}]}},
                 {"key":"r2","type":"Test.Renderer","fields":{"Materials":[{{{List(second)}}}]}}{{{siblings}}},
                 {"key":"source","type":"Test.Source","fields":{"Target":"$ref:r1"}}]}{{{extra}}}]}
            """;
    }

    // Two renderers on Rubble01 with the given numbers of materials; r2 points at r1, and with mutual r1 points at r2 too.
    private static string PointingRenderersWorld(string ownership, int first, int second, bool mutual = false)
    {
        static string Materials(int count) => string.Join(",", new[] { "m1", "m2", "m3", "m4" }.Take(count).Select(key => $"\"$ref:{key}\""));
        var target = mutual ? ",\"Target\":\"$ref:r2\"" : "";
        return $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{ownership}}}"},"slot":{"key":"root","name":"Rubble","parent":"Root"},
             "children":[
               {"slot":{"key":"materials","name":"Materials"},"components":[
                 {"key":"m1","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m2","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m3","type":"Test.Material","fields":{"Enabled":true}},
                 {"key":"m4","type":"Test.Material","fields":{"Enabled":true}}]},
               {"slot":{"key":"rubble","name":"Rubble01"},"components":[
                 {"key":"r1","type":"Test.Renderer","fields":{"Materials":[{{{Materials(first)}}}]{{{target}}} }},
                 {"key":"r2","type":"Test.Renderer","fields":{"Materials":[{{{Materials(second)}}}],"Target":"$ref:r1"}},
                 {"key":"source","type":"Test.Source","fields":{"Target":"$ref:r1"}}]}]}
            """;
    }

    // Applies the initial world (four materials unless given), then returns the changed document for the test to apply.
    private async Task<(FakeResoniteClient Client, WorldService Service, string State, ApplyDocument Changed)> ApplyRubbleAsync(
        string name, string changed, string? initial = null)
    {
        var first = ReloadDocument(name, initial ?? RubbleWorld(name, "m1", "m2", "m3", "m4"));
        var second = ReloadDocument(name + "-changed", changed);
        var client = new FakeResoniteClient(second);
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(first, new ApplyOptions(state));
        client.ResetWriteCounts();
        return (client, service, state, second);
    }

    private async Task<(FakeResoniteClient Client, WorldService Service, string State, ApplyDocument Shrunk, string Referenced,
        Dictionary<string, string[]> Expected)> ApplyScenarioAsync(string name, string scenario)
    {
        if (scenario is "one-recreate" or "one-recreate-unmanaged")
        {
            string World(bool watcher, params string[] materials)
            {
                var world = RubbleWorld(name, materials, "rubble", watcher);
                return scenario == "one-recreate-unmanaged"
                    ? world.Replace("""{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}}""",
                        """{"key":"source","type":"Test.Source","fields":{"Target":"$ref:renderer"}},{"key":"v1","type":"Test.Indexed","fields":{"Value":1}}""", StringComparison.Ordinal)
                    : world;
            }
            var (client, service, state, shrunk) = await ApplyRubbleAsync(name,
                World(true, "m1", "m2", "m3"), World(false, "m1", "m2", "m3", "m4"));
            return (client, service, state, shrunk, "renderer", new() { ["renderer"] = ["m1", "m2", "m3"] });
        }
        var initial = ReloadDocument(name, TwoRendererWorld(name, ["m1", "m2", "m3", "m4"], ["m1", "m2", "m3"], shared: true));
        var shrunkShared = ReloadDocument(name + "-shrunk", TwoRendererWorld(name, ["m1", "m2", "late"], ["m1", "m2"], shared: true, shrunk: true));
        var sharedClient = new FakeResoniteClient(shrunkShared);
        var sharedService = new WorldService(sharedClient);
        var sharedState = Path.Combine(_root, name + ".state.json");
        await sharedService.ApplyAsync(initial, new ApplyOptions(sharedState));
        sharedClient.ResetWriteCounts();
        return (sharedClient, sharedService, sharedState, shrunkShared, "r1", new() { ["r1"] = ["m1", "m2", "late"], ["r2"] = ["m1", "m2"] });
    }

    // Converged: no recreate is in progress, every renderer and Source in the world is bound to exactly one key, each
    // declared renderer holds the declared Materials, every Source points at a live Component or member and the managed
    // Source at the referenced key, every saved index matches the layout, and neither another apply nor an apply after a
    // world reload writes anything. Other types are not checked: a moved Component's source that an interrupted move leaves
    // behind is an existing upstream issue.
    private async Task AssertListShrinkConvergedAsync(FakeResoniteClient client, WorldService service, ApplyDocument document,
        string state, string referenced, IReadOnlyDictionary<string, string[]> expected, string context = "")
    {
        Assert.False(File.ReadAllText(state).Contains("supersededId", StringComparison.Ordinal), context + "a recreate is still in progress.");
        var keys = JsonNode.Parse(File.ReadAllText(state))!["components"]!.AsObject()
            .GroupBy(pair => pair.Value!["id"]!.GetValue<string>(), pair => pair.Key).ToDictionary(group => group.Key, group => group.Count());
        var live = AllSlots(client.Root).SelectMany(slot => slot.Components).ToDictionary(component => component.Id);
        foreach (var component in live.Values.Where(component => component.Type is "Test.Renderer" or "Test.Source"))
            Assert.True(keys.GetValueOrDefault(component.Id) == 1,
                $"{context}{component.Type} {component.Id} is bound to {keys.GetValueOrDefault(component.Id)} key(s).");
        foreach (var (key, materials) in expected)
            Assert.Equal(materials.Select(material => StateId(state, material)), MaterialTargets(live[StateId(state, key)]));
        var targets = live.Values.SelectMany(component => component.Members.Values.Select(member => member.Id).Append(component.Id))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var source in live.Values.Where(component => component.Type == "Test.Source"))
            Assert.True(source.Members["Target"].TargetId is { } target && targets.Contains(target),
                $"{context}Source {source.Id} points at a missing target.");
        Assert.Equal(StateId(state, referenced), live[StateId(state, "source")].Members["Target"].TargetId);
        AssertSavedIndexesMatchLayout(client, state);
        client.ResetWriteCounts();
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.True(client.Writes == 0, $"{context}the next apply wrote {client.Writes} time(s).");
        client.ReloadWorld("session-after-convergence");
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.True(client.Writes == 0, $"{context}the apply after a world reload wrote {client.Writes} time(s).");
    }

    private static FakeResoniteClient.FakeComponent[] RubbleRenderers(FakeResoniteClient client) =>
        AllSlots(client.Root).Single(slot => slot.Name == "Rubble01").Components
            .Where(component => component.Type == "Test.Renderer").ToArray();

    private static FakeResoniteClient.FakeComponent[] Sources(FakeResoniteClient client) =>
        AllSlots(client.Root).SelectMany(slot => slot.Components).Where(component => component.Type == "Test.Source").ToArray();

    private static string?[] MaterialTargets(FakeResoniteClient.FakeComponent renderer) =>
        renderer.Members["Materials"].Elements!.Select(element => element.TargetId).ToArray();

    private static string StateId(string state, string key) =>
        JsonNode.Parse(File.ReadAllText(state))!["components"]![key]!["id"]!.GetValue<string>();
}
