using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private const string SavedAssetWorld = """
        {"schemaVersion":"1","ownership":{"key":"saved-assets"},"slot":{"key":"root","name":"SavedAssets","parent":"Root"},
         "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
         "children":[
           {"slot":{"key":"crate-a","name":"CrateA"},"components":[{"key":"mesh-a","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]},
           {"slot":{"key":"crate-b","name":"CrateB"},"components":[{"key":"mesh-b","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]}]}
        """;

    [Fact]
    public async Task AssetUrlMigratedByWorldSaveIsAdoptedInsteadOfRewrittenToLocal()
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("saved-migrated");
        SaveAssetUrls(client, "resdb:///saved-crate", "resdb:///saved-crate");
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        var asset = Assert.Single(plan.Operations, operation => operation.Kind == "asset");
        Assert.Equal("no-op", asset.Action);
        Assert.Contains("migrated to resdb by a world save", asset.Reason);
        Assert.Empty(plan.Changes);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
        Assert.Equal(0, client.Writes);

        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, applied.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
        Assert.Equal(1, client.AssetImports);
        var saved = StateAsset(state, "mesh");
        Assert.Equal("resdb:///saved-crate", saved["url"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "crate.bin")))),
            saved["sourceHash"]!.GetValue<string>());
        Assert.Equal("mesh", saved["kind"]!.GetValue<string>());

        var again = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(0, again.ComponentsUpdated);
        Assert.Equal(0, client.Writes);
        Assert.Equal(1, client.AssetImports);
        var replanned = await service.PlanApplyAsync(document, new ApplyOptions(state));
        Assert.Empty(replanned.Changes);
    }

    [Fact]
    public async Task AssetUrlMigratedByWorldSaveIsAdoptedWhenItsOnlyReferenceIsRelocated()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var initial = ReloadDocument("saved-relocated", """
            {"schemaVersion":"1","ownership":{"key":"saved-relocated"},"slot":{"key":"root","name":"SavedAssets","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
             "children":[
               {"slot":{"key":"crate-a","name":"CrateA"},"components":[{"key":"mesh-a","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]},
               {"slot":{"key":"crate-b","name":"CrateB"}}]}
            """);
        var moved = ReloadDocument("saved-relocated-moved", """
            {"schemaVersion":"1","ownership":{"key":"saved-relocated"},"slot":{"key":"root","name":"SavedAssets","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}},
             "children":[
               {"slot":{"key":"crate-a","name":"CrateA"}},
               {"slot":{"key":"crate-b","name":"CrateB"},"components":[{"key":"mesh-a","type":"Test.AssetHolder","fields":{"URL":"$asset:mesh"}}]}]}
            """);
        var client = new FakeResoniteClient(initial) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, "saved-relocated.state.json");
        await service.ApplyAsync(initial, new ApplyOptions(state));
        var holder = Assert.Single(Holders(client));
        holder.Members["URL"] = holder.Members["URL"] with { Value = JsonValue.Create("resdb:///saved-crate") };
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var plan = await service.PlanApplyAsync(moved, new ApplyOptions(state));

        var asset = Assert.Single(plan.Operations, operation => operation.Kind == "asset");
        Assert.Equal("no-op", asset.Action);
        Assert.Contains("migrated to resdb by a world save", asset.Reason);
        Assert.Equal("relocate", Assert.Single(plan.Operations, operation => operation.Kind == "component").Action);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));

        var applied = await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Equal(1, applied.ComponentsDeleted);
        Assert.Equal(1, client.AssetImports);
        Assert.Equal("resdb:///saved-crate", Assert.Single(HolderUrls(client)));
        Assert.Equal("resdb:///saved-crate", StateAsset(state, "mesh")["url"]!.GetValue<string>());

        client.ResetWriteCounts();
        await service.ApplyAsync(moved, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        var replanned = await service.PlanApplyAsync(moved, new ApplyOptions(state));
        Assert.Empty(replanned.Changes);
    }

    [Fact]
    public async Task AssetUrlIsNotAdoptedFromAReferenceThatPointedToAnotherAssetBeforeTheSave()
    {
        var (client, service, state) = await ApplyTwoAssetWorldAsync("saved-repointed", "other");
        Assert.Equal("local://machine/asset-2", Assert.Single(HolderUrls(client)));
        var holder = Assert.Single(Holders(client));
        holder.Members["URL"] = holder.Members["URL"] with { Value = JsonValue.Create("resdb:///saved-other") };
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();
        var repointed = ReloadDocument("saved-repointed-mesh", TwoAssetWorld("saved-repointed", "mesh"));

        var plan = await service.PlanApplyAsync(repointed, new ApplyOptions(state));

        Assert.Equal("source hash and imported URL match state",
            Assert.Single(plan.Operations, operation => operation.Kind == "asset" && operation.Key == "mesh").Reason);
        Assert.Equal("update", Assert.Single(plan.Operations, operation => operation.Kind == "component").Action);
        await service.ApplyAsync(repointed, new ApplyOptions(state));
        Assert.Equal("local://machine/asset-1", Assert.Single(HolderUrls(client)));
        Assert.Equal("local://machine/asset-1", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal("local://machine/asset-2", StateAsset(state, "other")["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task AssetUrlIsAdoptedFromUnchangedReferencesWhenAnotherReferenceIsRepointed()
    {
        var (client, service, state) = await ApplyTwoAssetWorldAsync("saved-partly-repointed", "mesh", "other");
        SaveAssetUrls(client, "resdb:///saved-mesh", "resdb:///saved-other");
        var repointed = ReloadDocument("saved-partly-repointed-mesh", TwoAssetWorld("saved-partly-repointed", "mesh", "mesh"));

        var plan = await service.PlanApplyAsync(repointed, new ApplyOptions(state));

        Assert.Contains("migrated to resdb by a world save",
            Assert.Single(plan.Operations, operation => operation.Kind == "asset" && operation.Key == "mesh").Reason);
        Assert.Equal("no-op", Assert.Single(plan.Operations, operation => operation.Key == "holder-0").Action);
        Assert.Equal("update", Assert.Single(plan.Operations, operation => operation.Key == "holder-1").Action);
        await service.ApplyAsync(repointed, new ApplyOptions(state));
        Assert.All(HolderUrls(client), url => Assert.Equal("resdb:///saved-mesh", url));
        Assert.Equal("resdb:///saved-mesh", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal("local://machine/asset-2", StateAsset(state, "other")["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task LegacySavedAssetRequiresExplicitVerifiedSourceBeforeApply()
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("saved-legacy");
        SaveAssetUrls(client, "resdb:///saved-crate", "resdb:///saved-crate");
        RemoveRecordedAssetFields(state);

        var stateHash = SHA256.HashData(File.ReadAllBytes(state));
        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(document, new ApplyOptions(state)));
        var applyError = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
        Assert.Equal("APPLY_ASSET_MIGRATION_UNVERIFIED", planError.Code);
        Assert.Equal(planError.Code, applyError.Code);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
        Assert.Equal(0, client.Writes);
        // Recovery requires an explicitly verified source, never an inferred mapping from the new declaration.
        var verified = ReloadDocument("saved-legacy-verified", SavedAssetWorld.Replace("crate.bin", "resdb:///saved-crate"));
        await service.ApplyAsync(verified, new ApplyOptions(state));
        Assert.Equal(0, client.Writes);
        Assert.Equal("resdb:///saved-crate", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"URL":"$asset:mesh"}"""),
            JsonNode.Parse(File.ReadAllText(state))!["components"]!["mesh-a"]!["assetFields"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedReimportDoesNotAdoptTheSavedUrlOfThePreviousContent(bool legacyState)
    {
        var (client, service, state) = await ApplyTwoAssetWorldAsync("saved-interrupted-" + legacyState, "mesh", "other");
        if (legacyState) RemoveRecordedAssetFields(state);
        var document = ReloadDocument("saved-interrupted-" + legacyState, TwoAssetWorld("saved-interrupted-" + legacyState, "mesh", "other"));
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "second crate");
        // Stop after mesh is re-imported and checkpointed, before any field points at the new content.
        using var cancellation = new CancellationTokenSource();
        var stopped = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document,
            new ApplyOptions(state, Progress: progress => { if (progress.Stage == "assets") cancellation.Cancel(); }), cancellation.Token));
        Assert.Equal("APPLY_CANCELLED", stopped.Code);
        Assert.Equal("local://machine/asset-3", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        SaveAssetUrls(client, "resdb:///saved-old-mesh", "resdb:///saved-other");

        if (legacyState)
        {
            var stateHash = SHA256.HashData(File.ReadAllBytes(state));
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
            Assert.Equal("APPLY_ASSET_MIGRATION_UNVERIFIED", error.Code);
            Assert.Equal("other", error.Context!["assetKey"]);
            Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
            Assert.Equal(0, client.Writes);
            return;
        }

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("source hash and imported URL match state",
            Assert.Single(plan.Operations, operation => operation.Kind == "asset" && operation.Key == "mesh").Reason);
        Assert.Equal("update", Assert.Single(plan.Operations, operation => operation.Key == "holder-0").Action);
        Assert.Contains("migrated to resdb by a world save",
            Assert.Single(plan.Operations, operation => operation.Kind == "asset" && operation.Key == "other").Reason);
        Assert.Equal("no-op", Assert.Single(plan.Operations, operation => operation.Key == "holder-1").Action);
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(["local://machine/asset-3", "resdb:///saved-other"], HolderUrls(client));
        Assert.Equal("local://machine/asset-3", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal("resdb:///saved-other", StateAsset(state, "other")["url"]!.GetValue<string>());
        var replanned = await service.PlanApplyAsync(document, new ApplyOptions(state));
        Assert.Empty(replanned.Changes);
    }

    [Theory]
    [InlineData("resdb:///saved-a", "resdb:///saved-b")]
    [InlineData("resdb:///saved-a", null)]
    [InlineData("local://other/crate.meshx", "local://other/crate.meshx")]
    [InlineData("https://example.invalid/crate.meshx", "https://example.invalid/crate.meshx")]
    public async Task AssetUrlIsNotTreatedAsMigratedUnlessEveryLiveReferenceSharesOneResdbUri(string first, string? second)
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("saved-mixed-" + Guid.NewGuid().ToString("N")[..8]);
        SaveAssetUrls(client, first, second);

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        var asset = Assert.Single(plan.Operations, operation => operation.Kind == "asset");
        Assert.Equal("source hash and imported URL match state", asset.Reason);
        Assert.Contains(plan.Changes, operation => operation.Kind == "component" && operation.Action == "update");
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.True(applied.ComponentsUpdated > 0);
        Assert.Equal("local://machine/asset-1", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.All(HolderUrls(client), url => Assert.Equal("local://machine/asset-1", url));
    }

    [Fact]
    public async Task ChangedAssetSourceIsReimportedEvenAfterAWorldSaveMigratedItsUrl()
    {
        var (document, client, service, state) = await ApplySavedAssetWorldAsync("saved-changed");
        SaveAssetUrls(client, "resdb:///saved-crate", "resdb:///saved-crate");
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "second crate");

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("create", Assert.Single(plan.Operations, operation => operation.Kind == "asset").Action);
        var applied = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(2, client.AssetImports);
        Assert.Equal(2, applied.ComponentsUpdated);
        Assert.Equal("local://machine/asset-2", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.All(HolderUrls(client), url => Assert.Equal("local://machine/asset-2", url));
    }

    [Fact]
    public async Task AssetWithoutLiveReferencesKeepsItsStateUrl()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var document = ReloadDocument("saved-unreferenced", """
            {"schemaVersion":"1","ownership":{"key":"saved-unreferenced"},"slot":{"key":"root","name":"Unreferenced","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"}}}
            """);
        var client = new FakeResoniteClient(document) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, "saved-unreferenced.state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("source hash and imported URL match state", Assert.Single(plan.Operations, operation => operation.Kind == "asset").Reason);
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal("local://machine/asset-1", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task LegacyRepointDoesNotBindAnAssetToAnotherAssetsSavedContent()
    {
        var (client, service, state) = await ApplyTwoAssetWorldAsync("legacy-repoint", "other");
        RemoveRecordedAssetFields(state);
        var holder = Assert.Single(Holders(client));
        holder.Members["URL"] = holder.Members["URL"] with { Value = JsonValue.Create("resdb:///saved-other") };
        client.ReloadWorld("session-saved");
        client.ResetWriteCounts();
        var repointed = ReloadDocument("legacy-repoint-mesh", TwoAssetWorld("legacy-repoint", "mesh"));
        var stateHash = SHA256.HashData(File.ReadAllBytes(state));

        var planError = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(repointed, new ApplyOptions(state)));
        var applyError = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(repointed, new ApplyOptions(state)));

        Assert.Equal("APPLY_ASSET_MIGRATION_UNVERIFIED", planError.Code);
        Assert.Equal(planError.Code, applyError.Code);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(state)));
        Assert.Equal("local://machine/asset-1", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal("resdb:///saved-other", Assert.Single(HolderUrls(client)));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostFieldWriteResponseDoesNotLeaveAssetEvidenceForRevertedDeclaration(bool responseLost)
    {
        var (client, service, state) = await ApplyTwoAssetWorldAsync("lost-field-response", "mesh", "other");
        var repointed = ReloadDocument("lost-field-response-other", TwoAssetWorld("lost-field-response", "other", "other"));
        client.ResetWriteCounts();
        if (responseLost) client.LoseNextFieldWriteResponse = true;
        else client.FailOnWrite = 1;
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(repointed, new ApplyOptions(state)));
        Assert.Equal(responseLost ? "local://machine/asset-2" : "local://machine/asset-1", HolderUrls(client)[0]);
        Assert.Empty(JsonNode.Parse(File.ReadAllText(state))!["components"]!["holder-0"]!["assetFields"]!.AsObject());
        SaveAssetUrls(client, responseLost ? "resdb:///saved-other" : "resdb:///saved-mesh", "resdb:///saved-other");
        client.FailOnWrite = null;
        var reverted = ReloadDocument("lost-field-response-reverted", TwoAssetWorld("lost-field-response", "mesh", "other"));

        var plan = await service.PlanApplyAsync(reverted, new ApplyOptions(state));
        Assert.Contains(plan.Changes, operation => operation.Key == "holder-0" && operation.Action == "update");
        await service.ApplyAsync(reverted, new ApplyOptions(state));

        Assert.Equal("local://machine/asset-1", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal(["local://machine/asset-1", "resdb:///saved-other"], HolderUrls(client));
        // The untouched holder retains valid evidence for the other asset, and successful writes restore evidence.
        SaveAssetUrls(client, "resdb:///saved-mesh", "resdb:///saved-other");
        await service.ApplyAsync(reverted, new ApplyOptions(state));
        Assert.Equal("resdb:///saved-mesh", StateAsset(state, "mesh")["url"]!.GetValue<string>());
        Assert.Equal(0, client.Writes);
    }

    private async Task<(ApplyDocument Document, FakeResoniteClient Client, WorldService Service, string State)> ApplySavedAssetWorldAsync(string name)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        var document = ReloadDocument(name, SavedAssetWorld);
        var client = new FakeResoniteClient(document) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.All(HolderUrls(client), url => Assert.Equal("local://machine/asset-1", url));
        return (document, client, service, state);
    }

    private async Task<(FakeResoniteClient Client, WorldService Service, string State)> ApplyTwoAssetWorldAsync(string name,
        params string[] references)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "crate.bin"), "first crate");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.bin"), "other crate");
        var document = ReloadDocument(name, TwoAssetWorld(name, references));
        var client = new FakeResoniteClient(document) { ImportUrlPrefix = "local://machine/asset-" };
        var service = new WorldService(client);
        var state = Path.Combine(_root, name + ".state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        return (client, service, state);
    }

    // mesh is imported first (local://machine/asset-1) and other second (local://machine/asset-2).
    private static string TwoAssetWorld(string key, params string[] references)
    {
        var children = string.Join(",", references.Select((asset, index) =>
            $$$"""{"slot":{"key":"crate-{{{index}}}","name":"Crate{{{index}}}"},"components":[{"key":"holder-{{{index}}}","type":"Test.AssetHolder","fields":{"URL":"$asset:{{{asset}}}"}}]}"""));
        return $$$"""
            {"schemaVersion":"1","ownership":{"key":"{{{key}}}"},"slot":{"key":"root","name":"SavedAssets","parent":"Root"},
             "assets":{"mesh":{"kind":"mesh","source":"crate.bin"},"other":{"kind":"mesh","source":"other.bin"}},
             "children":[{{{children}}}]}
            """;
    }

    // Simulates Resonite moving the imported local asset into the saved world: live URLs change, state does not.
    private static void SaveAssetUrls(FakeResoniteClient client, string first, string? second)
    {
        var holders = Holders(client);
        if (first is not null) holders[0].Members["URL"] = holders[0].Members["URL"] with { Value = JsonValue.Create(first) };
        if (second is not null) holders[1].Members["URL"] = holders[1].Members["URL"] with { Value = JsonValue.Create(second) };
        // Every reload starts a different session; reusing the ID while changing Component IDs
        // would instead simulate an in-session deletion/replacement of managed content.
        client.ReloadWorld("session-saved-" + Guid.NewGuid().ToString("N"));
        client.ResetWriteCounts();
    }

    // A state written before asset fields were recorded has no record on any Component.
    private static void RemoveRecordedAssetFields(string state)
    {
        var legacy = JsonNode.Parse(File.ReadAllText(state))!;
        foreach (var (_, component) in legacy["components"]!.AsObject()) component!.AsObject().Remove("assetFields");
        File.WriteAllText(state, legacy.ToJsonString());
    }

    private static FakeResoniteClient.FakeComponent[] Holders(FakeResoniteClient client) =>
        client.Root.Children.Single().Children.SelectMany(slot => slot.Components)
            .Where(component => component.Type == "Test.AssetHolder").ToArray();

    private static string[] HolderUrls(FakeResoniteClient client) =>
        Holders(client).Select(component => component.Members["URL"].Value!.GetValue<string>()).ToArray();

    private static JsonNode StateAsset(string state, string key) =>
        JsonNode.Parse(File.ReadAllText(state))!["assets"]![key]!;
}
