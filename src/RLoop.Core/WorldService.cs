using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed partial class WorldService(IResoniteClient client, string? generatedContentSource = null)
{
    public async Task<string> ResolveSlotIdAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new RLoopException("SLOT_SELECTOR_MISSING", "A Slot ID or path is required.", ExitCodes.InvalidArguments);
        if (selector.Equals("Root", StringComparison.OrdinalIgnoreCase) || selector is "/" or "/Root") return "Root";

        if (!selector.StartsWith("path:", StringComparison.Ordinal) && !selector.Contains('/') && !selector.StartsWith("Root", StringComparison.OrdinalIgnoreCase))
        {
            try { return (await client.GetSlotAsync(selector, 0, false, cancellationToken)).Id; }
            catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
            {
                var matches = await FindAsync(selector, true, null, 8, cancellationToken);
                return matches.Count switch
                {
                    1 => matches[0].Id,
                    0 => throw new RLoopException("SLOT_NOT_FOUND", $"Slot '{selector}' was not found as an ID or exact name.", ExitCodes.NotFound),
                    _ => throw new RLoopException("SLOT_AMBIGUOUS", $"Slot name '{selector}' matched {matches.Count} Slots; use an ID or path.", ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["matches"] = matches.Select(x => new { x.Id, x.Path }).ToArray() })
                };
            }
        }

        var parts = SlotPaths.ParseSelector(selector).Skip(1);
        var currentId = "Root";
        var currentPath = "Root";
        foreach (var part in parts)
        {
            var current = await client.GetSlotAsync(currentId, 1, false, cancellationToken);
            var matches = current.Children.Where(x => x.Name.Equals(part, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
                throw new RLoopException("SLOT_PATH_NOT_FOUND", $"Path segment '{part}' was not found below '{currentPath}'.", ExitCodes.NotFound,
                    new Dictionary<string, object?> { ["path"] = selector, ["resolvedPrefix"] = currentPath });
            if (matches.Length > 1)
                throw new RLoopException("SLOT_PATH_AMBIGUOUS", $"Path segment '{part}' matched multiple Slots below '{currentPath}'. Use a Slot ID.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["ids"] = matches.Select(x => x.Id).ToArray() });
            currentId = matches[0].Id;
            currentPath += "/" + part;
        }
        return currentId;
    }

    public async Task<string> ResolveSlotSelectorAsync(string selector, string? stateFile = null,
        CancellationToken cancellationToken = default)
    {
        if (!selector.StartsWith('$')) return await ResolveSlotIdAsync(selector, cancellationToken);
        var syntax = StableSelectorSyntax.Parse(selector);
        if (syntax.Kind != "slot")
            throw new RLoopException("STABLE_SELECTOR_KIND_MISMATCH",
                $"Selector '{selector}' does not identify a Slot.", ExitCodes.InvalidArguments);
        var state = RequireStateFile(stateFile, selector);
        var session = await client.GetSessionInfoAsync(cancellationToken);
        return (await ResolveStableReferenceAsync(state, selector, session.UniqueSessionId, cancellationToken)).Id;
    }

    public async Task<string> ResolveComponentSelectorAsync(string selector, string? stateFile = null,
        CancellationToken cancellationToken = default)
    {
        if (!selector.StartsWith('$'))
            return (await client.GetComponentAsync(selector, cancellationToken)).Id;
        var syntax = StableSelectorSyntax.Parse(selector);
        if (syntax.Kind != "component")
            throw new RLoopException("STABLE_SELECTOR_KIND_MISMATCH",
                $"Selector '{selector}' does not identify a Component.", ExitCodes.InvalidArguments);
        var state = RequireStateFile(stateFile, selector);
        var session = await client.GetSessionInfoAsync(cancellationToken);
        return (await ResolveStableReferenceAsync(state, selector, session.UniqueSessionId, cancellationToken)).Id;
    }

    public async Task<SlotInfo> InspectAsync(string selector, int depth, bool includeComponentData,
        CancellationToken cancellationToken = default, bool excludeReferenceOnly = false)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, depth, includeComponentData, cancellationToken);
        var withPaths = AddPaths(slot, selector.Contains('/') ? NormalizePath(selector) : slot.Name);
        return excludeReferenceOnly ? RemoveReferenceOnlyChildren(withPaths) : withPaths;
    }

    public async Task<IReadOnlyList<SlotMatch>> FindAsync(string? name, bool exact, string? componentType,
        int depth, CancellationToken cancellationToken = default, FindOptions? options = null)
    {
        options ??= new FindOptions();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(componentType))
            throw new RLoopException("FIND_FILTER_MISSING", "find requires --name or --component.", ExitCodes.InvalidArguments);
        var rootId = string.IsNullOrWhiteSpace(options.Under)
            ? "Root"
            : await ResolveSlotIdAsync(options.Under, cancellationToken);
        var requestedDepth = options.DirectChildren ? 1 : depth;
        var root = await client.GetSlotAsync(rootId, requestedDepth, false, cancellationToken);
        var rootPath = string.IsNullOrWhiteSpace(options.Under)
            ? "Root"
            : options.Under!.Contains('/') ? NormalizePath(options.Under) : root.Name;
        var results = new List<SlotMatch>();
        Visit(root, rootPath, slot =>
        {
            if (options.DirectChildren && slot.Id == root.Id) return;
            if (options.ExcludeReferenceOnly && slot.IsReferenceOnly) return;
            var nameMatches = string.IsNullOrWhiteSpace(name) || (exact
                ? slot.Name.Equals(name, StringComparison.Ordinal)
                : slot.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            var componentMatches = string.IsNullOrWhiteSpace(componentType) || slot.Components.Any(c =>
                c.Type.Contains(componentType, StringComparison.OrdinalIgnoreCase));
            if (nameMatches && componentMatches)
                results.Add(new SlotMatch(slot.Id, slot.Name, slot.Path!, slot.Components));
        });
        return results;
    }

    public async Task<IReadOnlyList<InspectedComponent>> InspectComponentsAsync(string selector, int depth,
        string? componentType = null, string? memberName = null, bool excludeReferenceOnly = false,
        CancellationToken cancellationToken = default)
    {
        var slot = await InspectAsync(selector, depth, true, cancellationToken);
        var results = new List<InspectedComponent>();
        Visit(slot, slot.Path ?? slot.Name, current =>
        {
            if (excludeReferenceOnly && current.IsReferenceOnly) return;
            foreach (var summary in current.Components)
            {
                if (!string.IsNullOrWhiteSpace(componentType) &&
                    !summary.Type.Contains(componentType, StringComparison.OrdinalIgnoreCase)) continue;
                var members = summary.Members ?? new Dictionary<string, MemberValue>();
                if (!string.IsNullOrWhiteSpace(memberName))
                {
                    var matches = members.Where(pair => pair.Key.Equals(memberName, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    if (matches.Count == 0) continue;
                    members = matches;
                }
                results.Add(new InspectedComponent(current.Id, current.Path ?? current.Name,
                    new ComponentInfo(summary.Id, summary.Type, members)));
            }
        });
        return results;
    }

    public async Task<IReadOnlyList<ComponentInfo>> ListComponentsAsync(string selector, CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, 0, false, cancellationToken);
        var result = new List<ComponentInfo>();
        foreach (var component in slot.Components) result.Add(await client.GetComponentAsync(component.Id, cancellationToken));
        return result;
    }

    public Task<ResolvedWorldReference> ResolveStableReferenceAsync(string stateFile, string selector,
        string? currentConnectionId, CancellationToken cancellationToken = default) =>
        ResolveStableReferenceCoreAsync(stateFile, selector, currentConnectionId, new HashSet<string>(StringComparer.Ordinal), cancellationToken);

    private async Task<ResolvedWorldReference> ResolveStableReferenceCoreAsync(string stateFile, string selector,
        string? currentConnectionId, HashSet<string> resolvingComponents, CancellationToken cancellationToken,
        Dictionary<string, ComponentInfo>? observedComponents = null)
    {
        async Task<ComponentInfo> ReadComponent(string id)
        {
            if (observedComponents is not null && observedComponents.TryGetValue(id, out var observed)) return observed;
            var result = await client.GetComponentAsync(id, cancellationToken);
            if (observedComponents is not null) observedComponents[id] = result;
            return result;
        }
        var syntax = StableSelectorSyntax.Parse(selector);
        if (syntax.Kind == "slot-member")
        {
            var target = await ResolveStableReferenceCoreAsync(stateFile, "$slot:" + syntax.Key,
                currentConnectionId, resolvingComponents, cancellationToken, observedComponents);
            var slot = await client.GetSlotAsync(target.Id, 0, false, cancellationToken);
            var member = RequireSlotMember(slot, syntax.MemberName!, selector);
            return new ResolvedWorldReference(selector, member.Id!, "member", member.Type ?? member.TargetType, target.Path);
        }
        if (syntax.Kind == "slot")
        {
            var stable = StableReferenceResolver.ResolveSlot(stateFile, selector);
            string id;
            if (stable.SessionId == currentConnectionId && !string.IsNullOrWhiteSpace(stable.Id))
            {
                try { id = (await client.GetSlotAsync(stable.Id, 0, false, cancellationToken)).Id; }
                catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
                {
                    try { id = await ResolveSlotIdAsync(SlotPaths.Selector(stable.Path, stable.PathSegments), cancellationToken); }
                    catch (RLoopException pathError) when (stable.RuntimeRelocatable &&
                        pathError.Code is "SLOT_NOT_FOUND" or "SLOT_PATH_NOT_FOUND")
                    { id = (await ResolveRelocatableSlotAsync(stateFile, stable, cancellationToken)).Id; }
                }
            }
            else
            {
                id = stable.RuntimeRelocatable
                    ? (await ResolveRelocatableSlotAsync(stateFile, stable, cancellationToken)).Id
                    : await ResolveSlotIdAsync(SlotPaths.Selector(stable.Path, stable.PathSegments), cancellationToken);
            }
            return new ResolvedWorldReference(selector, id, "slot", "[FrooxEngine]FrooxEngine.Slot", stable.Path);
        }

        var componentSelector = "$component:" + syntax.Key;
        var memberName = syntax.MemberName;

        var stableComponent = StableReferenceResolver.ResolveComponent(stateFile, componentSelector);
        var firstVisit = resolvingComponents.Add(stableComponent.Key);
        try
        {
            ComponentInfo? component = null;
            if (stableComponent.SessionId == currentConnectionId && !string.IsNullOrWhiteSpace(stableComponent.Id))
            {
                try { component = await ReadComponent(stableComponent.Id); }
                catch (RLoopException ex) when (ex.Code is "COMPONENT_NOT_FOUND" or "RESONITE_OPERATION_FAILED") { }
            }
            if (component is null)
            {
                var stableSlot = StableReferenceResolver.ResolveSlot(stateFile, "$slot:" + stableComponent.SlotKey);
                var slotId = (await ResolveStableReferenceCoreAsync(stateFile, "$slot:" + stableComponent.SlotKey,
                    currentConnectionId, resolvingComponents, cancellationToken, observedComponents)).Id;
                var slot = await client.GetSlotAsync(slotId, 0, true, cancellationToken);
                Dictionary<string, string>? referenceTargets = null;
                if (firstVisit && stableComponent.ReferenceSelectors is { Count: > 0 })
                {
                    referenceTargets = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var reference in stableComponent.ReferenceSelectors)
                    {
                        var targetSyntax = StableSelectorSyntax.Parse(reference.Value);
                        if (targetSyntax.Kind is not ("slot" or "slot-member") && resolvingComponents.Contains(targetSyntax.Key)) continue;
                        var target = await ResolveStableReferenceCoreAsync(stateFile, reference.Value, currentConnectionId,
                            resolvingComponents, cancellationToken, observedComponents);
                        referenceTargets[reference.Key] = target.Id;
                    }
                }
                var matching = StableComponentCandidates(slot.Components, stableComponent.Type, stableComponent.ComponentIndex,
                    stableComponent.MemberNames, stableComponent.IdentityValues, referenceTargets);
                if (matching.Length == 0 && stableComponent.MemberNames is null && stableComponent.IdentityValues is null)
                {
                    var legacy = slot.Components.Where(summary => TypeNamesEquivalent(summary.Type, stableComponent.Type)).ToArray();
                    matching = stableComponent.TypeOrdinal >= 0 && stableComponent.TypeOrdinal < legacy.Length
                        ? [legacy[stableComponent.TypeOrdinal]] : [];
                }
                if (matching.Length == 0)
                    throw new RLoopException("FLUX_BINDING_COMPONENT_NOT_FOUND",
                        $"Stable component '{stableComponent.Key}' could not be re-resolved on '{stableSlot.Path}'.", ExitCodes.NotFound,
                        new Dictionary<string, object?> { ["selector"] = selector, ["slotPath"] = stableSlot.Path,
                            ["type"] = stableComponent.Type, ["typeOrdinal"] = stableComponent.TypeOrdinal });
                if (matching.Length > 1)
                    throw new RLoopException("STABLE_COMPONENT_AMBIGUOUS",
                        $"Stable component '{stableComponent.Key}' matches multiple Components on '{stableSlot.Path}'.",
                        ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["selector"] = selector,
                            ["slotPath"] = stableSlot.Path, ["candidateIds"] = matching.Select(candidate => candidate.Id).ToArray() },
                        ["Inspect candidateIds and preserve the existing state. Adding identityFields to a manifest does not populate an older checkpoint's identity values.",
                         "For new content use one named provider Slot per Component or initialize immutable identityFields at creation. Recover existing content only after verifying ownership and exact candidates; do not select by ordinal or discard state blindly."]);
                component = await ReadComponent(matching[0].Id);
            }

            if (memberName is null)
                return new ResolvedWorldReference(selector, component.Id, "component", component.Type);
            var member = component.Members.FirstOrDefault(pair => pair.Key.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(member.Key) || string.IsNullOrWhiteSpace(member.Value.Id))
                throw new RLoopException("FLUX_BINDING_MEMBER_NOT_FOUND",
                    $"Member '{memberName}' was not found on stable component '{stableComponent.Key}'.", ExitCodes.NotFound,
                    suggestions: component.Members.Keys.Take(30).ToArray());
            return new ResolvedWorldReference(selector, member.Value.Id!, "member", member.Value.Type ?? member.Value.TargetType);
        }
        finally
        {
            if (firstVisit) resolvingComponents.Remove(stableComponent.Key);
        }
    }

    private async Task<SlotInfo> ResolveRelocatableSlotAsync(string stateFile, StableSlotReference stable,
        CancellationToken cancellationToken)
    {
        var state = ApplyStateStore.Load(Path.GetFullPath(stateFile), stable.OwnershipKey);
        var evidence = state.Components.Values.Where(component => component.SlotKey == stable.Key).ToArray();
        if (evidence.Length == 0)
            throw new RLoopException("STABLE_RELOCATABLE_EVIDENCE_MISSING",
                $"Runtime-relocatable Slot '{stable.Key}' has no managed Component evidence for a safe world-wide search.",
                ExitCodes.ValidationFailed, suggestions:
                ["Declare at least one keyed Component on the runtimeRelocatable Slot and apply it before moving the item."]);
        var name = (stable.PathSegments ?? SlotPaths.LegacySegments(stable.Path)).LastOrDefault() ?? string.Empty;
        var world = AddPaths(await client.GetSlotAsync("Root", 64, true, cancellationToken), "Root");
        var candidates = new List<SlotInfo>();
        Visit(world, "Root", slot =>
        {
            if (slot.IsReferenceOnly || !slot.Name.Equals(name, StringComparison.Ordinal)) return;
            if (evidence.All(component => StableComponentCandidates(slot.Components, component.Type,
                    component.ComponentIndex, component.MemberNames, component.IdentityValues).Length == 1))
                candidates.Add(slot);
        });
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0)
            throw new RLoopException("STABLE_RELOCATABLE_SLOT_NOT_FOUND",
                $"Runtime-relocatable Slot '{stable.Key}' could not be uniquely re-resolved outside its saved path.",
                ExitCodes.NotFound, new Dictionary<string, object?> { ["savedPath"] = stable.Path, ["name"] = name });
        throw new RLoopException("STABLE_RELOCATABLE_SLOT_AMBIGUOUS",
            $"Runtime-relocatable Slot '{stable.Key}' matches multiple Slots outside its saved path.",
            ExitCodes.ValidationFailed, new Dictionary<string, object?>
            {
                ["savedPath"] = stable.Path,
                ["candidateIds"] = candidates.Select(candidate => candidate.Id).ToArray(),
                ["candidatePaths"] = candidates.Select(candidate => candidate.Path).ToArray()
            }, ["Add identityFields with immutable values to a managed Component on the relocatable Slot."]);
    }

    private static string RequireStateFile(string? stateFile, string selector) =>
        !string.IsNullOrWhiteSpace(stateFile) ? stateFile : throw new RLoopException(
            "WORLD_STATE_REQUIRED", $"Selector '{selector}' requires --state WORLD_STATE.", ExitCodes.InvalidArguments,
            suggestions: ["Pass the apply world-state file that contains this stable key."]);

    public async Task<ItemAuditReport> AuditItemAsync(string selector, IReadOnlyCollection<string>? allowedExternalIds = null,
        bool strict = false, IReadOnlyCollection<string>? allowedExternalRoles = null,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var root = AddPaths(RemoveReferenceOnlyChildren(await client.GetSlotAsync(id, 64, true, cancellationToken)),
            selector.StartsWith("Root", StringComparison.OrdinalIgnoreCase) ? NormalizePath(selector) : selector);
        return ItemAuditService.Audit(root, allowedExternalIds, strict, allowedExternalRoles);
    }

    public async Task<ToolAuditReport> AuditToolAsync(string selector, int depth = 16,
        float minimumAlignmentDot = 0.8f, CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var root = AddPaths(await client.GetSlotAsync(id, Math.Clamp(depth, 0, 64), true, cancellationToken), selector);
        return ToolAuditService.Audit(root, minimumAlignmentDot);
    }

    public Task<ApplyValidationResult> ValidateApplyAsync(ApplyDocument document, bool strict,
        CancellationToken cancellationToken = default) =>
        ApplyDocumentValidator.ValidateAsync(GeneratedContentMetadata.AddToGeneratedRoots(document, generatedContentSource),
            strict ? client : null, cancellationToken);

    public async Task<string?> EnsureGeneratedContentTagAsync(string slotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(generatedContentSource)) return null;
        var slot = await client.GetSlotAsync(slotId, 0, true, cancellationToken);
        var marker = slot.Components.FirstOrDefault(component =>
            TypeNamesEquivalent(component.Type, GeneratedContentMetadata.ComponentType));
        if (marker is null)
        {
            return (await client.AddComponentAsync(slotId, GeneratedContentMetadata.ComponentType,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GeneratedContentMetadata.SourceMember] = generatedContentSource
                }, cancellationToken)).Id;
        }

        var members = marker.Members ?? (await client.GetComponentAsync(marker.Id, cancellationToken)).Members;
        if (!members.TryGetValue(GeneratedContentMetadata.SourceMember, out var source) ||
            !MemberMatchesRaw(source, generatedContentSource))
            await client.SetComponentMemberAsync(marker.Id, GeneratedContentMetadata.SourceMember,
                generatedContentSource, cancellationToken);
        return marker.Id;
    }

    public async Task<ApplyPlanResult> PlanApplyAsync(ApplyDocument document, ApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        options.Progress?.Invoke(new ApplyProgress("validate", 0, 1, document.SourcePath, "Validating the complete document."));
        var prepared = await PrepareAsync(document, options, cancellationToken);
        options.Progress?.Invoke(new ApplyProgress("plan", prepared.Entries.Count, prepared.Entries.Count, null, "Plan is ready; no world changes were made."));
        return new ApplyPlanResult(true, document.SchemaVersion!, document.Ownership!.Key, prepared.StatePath,
            prepared.Session.UniqueSessionId, prepared.Entries,
            prepared.Entries.Count(x => x.Action == "create"),
            prepared.Entries.Count(x => x.Action is "update" or "rename" or "relocate" or "recreate"),
            prepared.Entries.Count(x => x.Action == "no-op"),
            prepared.Entries.Count(x => x.Action == "rename"),
            prepared.Entries.Count(x => x.Action == "delete"), false,
            $"Non-atomic preview. State checkpoint: {prepared.StatePath}. Re-run apply to converge; deletion requires --prune --yes.")
            { Warnings = ComponentIdentityDiagnostics.Analyze(document) };
    }

    public async Task<ApplyResult> ApplyAsync(ApplyDocument document, ApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        var stopwatch = Stopwatch.StartNew();
        using var writer = CheckpointFiles.AcquireWriter(ApplyStateStore.ResolvePath(document, options.StateFile));
        if (client is IResoniteClientDiagnostics diagnostics) diagnostics.ResetMetrics();
        options.Progress?.Invoke(new ApplyProgress("validate", 0, 1, document.SourcePath, "Validating and planning before mutation."));
        var prepared = await PrepareAsync(document, options, cancellationToken);
        if (options.Prune && !options.ConfirmDeletes)
            throw new RLoopException("CONFIRMATION_REQUIRED", "apply --prune is destructive and requires --yes.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["deleteCandidates"] = prepared.Deletions.Count, ["stateFile"] = prepared.StatePath });
        var counts = new ApplyCounts();
        var updatedComponents = new HashSet<string>(StringComparer.Ordinal);
        var completed = 0;
        var total = prepared.Nodes.Count + prepared.Components.Count * 2;
        try
        {
            foreach (var asset in prepared.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                asset.Url = asset.DirectUrl ?? (asset.Action == "no-op" && prepared.State.Assets.TryGetValue(asset.Key, out var saved)
                    ? asset.MigratedUrl ?? saved.Url : await client.ImportAssetAsync(asset.Spec, asset.ResolvedSource, cancellationToken));
                if (asset.Action == "create")
                {
                    counts.AssetsImported++;
                    ForgetAssetEvidence(prepared, asset.Key);
                }
                else counts.AssetsUnchanged++;
                prepared.State.Assets[asset.Key] = new ApplyStateAsset(asset.Spec.Kind, asset.SourceHash, asset.Url);
                Checkpoint(prepared);
                options.Progress?.Invoke(new ApplyProgress("assets", counts.AssetsImported + counts.AssetsUnchanged,
                    prepared.Assets.Count, "$assets/" + asset.Key, asset.Action == "create" ? "imported asset" : "reused asset"));
            }
            var assetUrls = prepared.Assets.ToDictionary(x => x.Key, x => x.Url!, StringComparer.Ordinal);
            foreach (var existingNode in prepared.Nodes.Where(node => node.Existing is not null))
                existingNode.Id = existingNode.Existing!.Id;
            foreach (var node in prepared.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // UIX children must not be reparented into an empty Slot before its RectTransform
                // and layout are attached. Keep the old checkpoint path until relocation commits.
                if (node.SlotAction == "relocate") continue;
                var parentId = node.Parent?.Id ?? prepared.ParentId;
                switch (node.SlotAction)
                {
                    case "create":
                        // Persist intent before the remote mutation. If the response is lost after Resonite
                        // creates the Slot, the next run can bind the exact pending path without duplicating it.
                        prepared.State.Slots[node.StableKey] = new ApplyStateSlot(string.Empty, node.Path, node.Spec.RuntimeRelocatable, node.PathSegments);
                        Checkpoint(prepared);
                        node.Id = await client.CreateSlotAsync(new SlotCreateRequest(parentId, node.Spec.Name,
                            node.Spec.Position?.ToVector3("position"), node.Spec.Rotation?.ToQuaternion("rotation"),
                            node.Spec.Scale?.ToVector3("scale")), cancellationToken);
                        counts.SlotsCreated++;
                        break;
                    case "update":
                        node.Id = node.Existing!.Id;
                        await client.UpdateSlotAsync(CreateSlotUpdate(node, prepared.ParentId), cancellationToken);
                        counts.SlotsUpdated++;
                        break;
                    default:
                        node.Id = node.Existing!.Id;
                        counts.SlotsUnchanged++;
                        break;
                }
                prepared.State.Slots[node.StableKey] = new ApplyStateSlot(node.Id, node.Path, node.Spec.RuntimeRelocatable, node.PathSegments);
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("slots", completed, total, node.Path, $"{node.SlotAction} Slot"));
            }

            // A recreate's replacement is not a reference target until every replacement is verified.
            var byKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null && !x.AwaitsVerification)
                .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
            var awaitingKeys = prepared.Components.Where(x => x.AwaitsVerification)
                .Select(x => x.Spec.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var slotsByKey = prepared.Nodes.ToDictionary(x => x.StableKey, StringComparer.Ordinal);
            foreach (var component in prepared.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Existing is not null)
                {
                    component.Id = component.Existing.Id;
                    component.ResolvedType = component.Existing.Type;
                }
                else
                {
                    IReadOnlyDictionary<string, string> initialFields = new Dictionary<string, string>();
                    var createFields = MergeCreateFields(component.Spec);
                    // Leave out only the references to replacements that are not verified yet; the fields phase writes them.
                    var withoutPending = createFields.Where(field => !ReferencesComponentKeys(field.Value, awaitingKeys, component.Spec.Key))
                        .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
                    if (withoutPending.Count < createFields.Count && CanResolveAll(withoutPending, byKey, slotsByKey)) createFields = withoutPending;
                    if (CanResolveAll(createFields, byKey, slotsByKey))
                    {
                        initialFields = await ResolveFieldsAsync(createFields, byKey, slotsByKey, assetUrls, cancellationToken);
                        component.AppliedOnCreate = initialFields;
                    }
                    // The asset phase may already have forgotten evidence for re-imported assets; an undo must keep that.
                    if (component.RecreateReason is not null)
                        component.StateBeforeRecreate = prepared.State.Components.GetValueOrDefault(component.StableKey);
                    // A new Component may be created even if its response is lost. Its old source's
                    // declarations cannot prove which asset the replacement's fields now contain.
                    prepared.State.Components[component.StableKey] = CreateComponentState(component, string.Empty)
                        with { AssetFields = new Dictionary<string, JsonElement>() };
                    Checkpoint(prepared);
                    var created = await client.AddComponentAsync(component.Node.Id!, component.Spec.Type, initialFields, cancellationToken);
                    component.Id = created.Id;
                    component.ResolvedType = created.Type;
                    counts.ComponentsAdded++;
                    component.ReplacementCreated = component.SupersededId is not null;
                }
                if (!string.IsNullOrWhiteSpace(component.Spec.Key) && !component.AwaitsVerification) byKey[component.Spec.Key!] = component;
                prepared.State.Components[component.StableKey] = CreateComponentState(component, component.Id!,
                    previous: prepared.State.Components.GetValueOrDefault(component.StableKey));
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("components", completed, total, component.Path,
                    component.Existing is null ? "created Component" : "resolved Component"));
            }

            var recreates = prepared.Components.Where(component => component.SupersededId is not null).ToArray();
            await VerifyReplacementsAsync(prepared, recreates, byKey, slotsByKey, assetUrls, counts, cancellationToken);

            foreach (var component in prepared.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var desiredFields = component.Existing is null ? MergeCreateFields(component.Spec) : component.Spec.Fields;
                var fields = await ResolveFieldsAsync(desiredFields, byKey, slotsByKey, assetUrls, cancellationToken);
                var changed = fields.Where(field => component.AppliedOnCreate is null ||
                                                    !component.AppliedOnCreate.TryGetValue(field.Key, out var applied) || applied != field.Value)
                    .Where(field => component.Existing?.Members is null ||
                                    !component.Existing.Members.TryGetValue(field.Key, out var current) ||
                                    !MemberMatchesRaw(current, field.Value)).ToDictionary(StringComparer.Ordinal);
                if (changed.Count > 0)
                {
                    ForgetComponentAssetEvidence(prepared, component.StableKey, changed.Keys);
                    Checkpoint(prepared);
                    await client.SetComponentMembersAsync(component.Id!, component.ResolvedType ?? component.Spec.Type, changed, cancellationToken);
                    if (component.Existing is not null) { counts.ComponentsUpdated++; updatedComponents.Add(component.Id!); }
                }
                else if (component.Existing is not null)
                {
                    counts.ComponentsUnchanged++;
                }
                if (component.RelocationSource is not null && component.RelocationSource.Id != component.Id)
                {
                    await client.RemoveComponentAsync(component.RelocationSource.Id, cancellationToken);
                    counts.ComponentsDeleted++;
                    component.RelocationSource = null;
                    await SaveSlotIndexesAsync(prepared, component.RelocationSourceSlotKey!, component.RelocationSourceSlotId!,
                        cancellationToken);
                }
                prepared.State.Components[component.StableKey] = CreateComponentState(component, component.Id!, fields);
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("fields", completed, total, component.Path,
                    changed.Count == 0 ? "no field changes" : $"updated {changed.Count} field(s)"));
            }

            // Every managed reference now points at the replacements. Remove each replaced Component, then save the
            // Slot's final indexes and clear the recreate in one checkpoint.
            foreach (var component in recreates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Superseded is not null)
                {
                    await client.RemoveComponentAsync(component.Superseded.Id, cancellationToken);
                    counts.ComponentsDeleted++;
                    component.Superseded = null;
                }
                await SaveSlotIndexesAsync(prepared, component.Node.StableKey, component.Node.Id!, cancellationToken);
                component.SupersededId = null;
                prepared.State.Components[component.StableKey] = prepared.State.Components[component.StableKey] with { SupersededId = null };
                Checkpoint(prepared);
                options.Progress?.Invoke(new ApplyProgress("fields", completed, total, component.Path,
                    "removed the Component that the recreate replaced"));
            }

            // All new parents and their field/reference configuration now exist. Preserve the
            // parent-first relocation order, local/world transform policy, and existing Slot IDs.
            foreach (var node in prepared.Nodes.Where(node => node.SlotAction == "relocate"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.UpdateSlotAsync(CreateSlotUpdate(node, prepared.ParentId), cancellationToken);
                counts.SlotsUpdated++;
                prepared.State.Slots[node.StableKey] = new ApplyStateSlot(node.Id!, node.Path, node.Spec.RuntimeRelocatable, node.PathSegments);
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("slots", completed, total, node.Path, "relocated Slot after parent preparation"));
            }

            if (options.Prune)
            {
                foreach (var deletion in prepared.Deletions.Where(x => x.Kind == "component"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await client.RemoveComponentAsync(deletion.Id, cancellationToken);
                    prepared.State.Components.Remove(deletion.Key);
                    counts.ComponentsDeleted++;
                    // Save the removal before reading the Slot again, so a cancel or a failed read cannot keep the removed key.
                    Checkpoint(prepared);
                    await SaveSlotIndexesAsync(prepared, deletion.SlotKey!, deletion.SlotId!, cancellationToken);
                    Checkpoint(prepared);
                    options.Progress?.Invoke(new ApplyProgress("prune", counts.ComponentsDeleted + counts.SlotsDeleted,
                        prepared.Deletions.Count, deletion.Path, "deleted owned Component"));
                }
                foreach (var deletion in prepared.Deletions.Where(x => x.Kind == "slot").OrderByDescending(x => x.Path.Count(ch => ch == '/')))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deletion.Id.Equals("Root", StringComparison.OrdinalIgnoreCase))
                        throw new RLoopException("DELETE_ROOT_FORBIDDEN", "The Root Slot can never be pruned.", ExitCodes.ValidationFailed);
                    await client.DeleteSlotAsync(deletion.Id, cancellationToken);
                    foreach (var componentKey in deletion.CoveredComponentKeys ?? [])
                        prepared.State.Components.Remove(componentKey);
                    foreach (var slotKey in deletion.CoveredSlotKeys ?? [deletion.Key])
                        prepared.State.Slots.Remove(slotKey);
                    counts.SlotsDeleted++;
                    Checkpoint(prepared);
                    options.Progress?.Invoke(new ApplyProgress("prune", counts.ComponentsDeleted + counts.SlotsDeleted,
                        prepared.Deletions.Count, deletion.Path, "deleted owned Slot"));
                }
            }
            // An old driver can reject a replacement's reference while it still owns the field.
            // Reconcile after removals, and never report success for a connection the runtime dropped.
            if (counts.ComponentsAdded + counts.ComponentsUpdated + counts.ComponentsDeleted + counts.SlotsUpdated + counts.SlotsDeleted > 0)
            foreach (var component in prepared.Components)
            {
                var desired = component.Existing is null ? MergeCreateFields(component.Spec) : component.Spec.Fields;
                var referenceFields = (desired ?? new Dictionary<string, JsonElement>())
                    .Where(field => ContainsWorldReference(field.Value)).ToDictionary(pair => pair.Key, pair => pair.Value);
                if (referenceFields.Count == 0) continue;
                var fields = await ResolveFieldsAsync(referenceFields, byKey, slotsByKey, assetUrls, cancellationToken);
                var current = await client.GetComponentAsync(component.Id!, cancellationToken);
                var missing = fields.Where(field => !current.Members.TryGetValue(field.Key, out var observed) ||
                    !MemberMatchesRaw(observed, field.Value)).ToDictionary(pair => pair.Key, pair => pair.Value);
                if (missing.Count == 0) continue;
                var previousAssetFields = prepared.State.Components[component.StableKey].AssetFields;
                ForgetComponentAssetEvidence(prepared, component.StableKey, missing.Keys);
                Checkpoint(prepared);
                await client.SetComponentMembersAsync(component.Id!, component.ResolvedType ?? component.Spec.Type, missing, cancellationToken);
                if (component.Existing is not null && updatedComponents.Add(component.Id!))
                {
                    counts.ComponentsUpdated++;
                    counts.ComponentsUnchanged--;
                }
                current = await client.GetComponentAsync(component.Id!, cancellationToken);
                var rejected = missing.Where(field => !current.Members.TryGetValue(field.Key, out var observed) ||
                    !MemberMatchesRaw(observed, field.Value)).Select(field => field.Key).ToArray();
                if (rejected.Length > 0)
                    throw new RLoopException("APPLY_REFERENCE_NOT_RETAINED", "The runtime did not retain declared references after reconciliation. Inspect competing drivers and the saved checkpoint.",
                        ExitCodes.OperationFailed, new Dictionary<string, object?> { ["stateFile"] = prepared.StatePath,
                            ["componentKey"] = component.StableKey, ["componentId"] = component.Id, ["members"] = rejected },
                        ["Inspect the exact targets and any obsolete field owner. If deletion is needed, review diff --deletes-only and use apply --prune --yes; no implicit pruning occurs."]);
                prepared.State.Components[component.StableKey] = prepared.State.Components[component.StableKey]
                    with { AssetFields = previousAssetFields };
                Checkpoint(prepared);
                options.Progress?.Invoke(new ApplyProgress("references", completed, total, component.Path, "reconciled references after removals"));
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new RLoopException("APPLY_CANCELLED", "Apply was cancelled; completed operations were checkpointed and the same command can resume safely.",
                ExitCodes.OperationFailed, new Dictionary<string, object?>
                {
                    ["stateFile"] = prepared.StatePath,
                    ["completed"] = completed,
                    ["total"] = total,
                    ["remaining"] = Math.Max(0, total - completed),
                    ["slotsCreated"] = counts.SlotsCreated,
                    ["componentsAdded"] = counts.ComponentsAdded,
                    ["atomic"] = false,
                    ["recovery"] = $"Re-run the same apply command. Checkpoint: {prepared.StatePath}"
                }, ["Re-run the same apply command after resolving the cancellation cause."], ex);
        }
        catch (RLoopException ex)
        {
            var context = new Dictionary<string, object?>(ex.Context, StringComparer.Ordinal)
            {
                ["stateFile"] = prepared.StatePath,
                ["completed"] = completed,
                ["total"] = total,
                ["remaining"] = Math.Max(0, total - completed),
                ["slotsCreated"] = counts.SlotsCreated,
                ["slotsUpdated"] = counts.SlotsUpdated,
                ["componentsAdded"] = counts.ComponentsAdded,
                ["componentsUpdated"] = counts.ComponentsUpdated,
                ["componentsDeleted"] = counts.ComponentsDeleted,
                ["slotsDeleted"] = counts.SlotsDeleted,
                ["assetsImported"] = counts.AssetsImported,
                ["atomic"] = false,
                ["recovery"] = $"Re-run the same apply command. Checkpoint: {prepared.StatePath}"
            };
            var suggestions = ex.Suggestions.Concat(["Re-run the same apply command after resolving the error; completed operations are checkpointed."])
                .Distinct(StringComparer.Ordinal).ToArray();
            throw new RLoopException(ex.Code, ex.Message, ex.ExitCode, context, suggestions, ex);
        }

        stopwatch.Stop();
        var metrics = client is IResoniteClientDiagnostics profiled ? profiled.SnapshotMetrics() :
            new ClientMetrics(0, 0, 0, []);
        var mutations = counts.SlotsCreated + counts.SlotsUpdated + counts.ComponentsAdded + counts.ComponentsUpdated +
                        counts.ComponentsDeleted + counts.SlotsDeleted + counts.AssetsImported;
        var noOps = counts.SlotsUnchanged + counts.ComponentsUnchanged + counts.AssetsUnchanged;
        var profile = options.Profile ? new ApplyProfile(stopwatch.Elapsed.TotalMilliseconds, metrics,
            prepared.Entries.Count, mutations, noOps) : null;
        var root = prepared.Nodes[0];
        return new ApplyResult(root.Id!, root.SlotAction == "create", counts.ComponentsAdded, counts.ComponentsUpdated,
            counts.SlotsCreated, counts.SlotsUpdated, counts.SlotsUnchanged, counts.ComponentsUnchanged,
            prepared.StatePath, prepared.Session.UniqueSessionId, profile, counts.ComponentsDeleted, counts.SlotsDeleted,
            false, $"Operations are non-atomic. Re-run the same apply command to converge from checkpoint {prepared.StatePath}.",
            counts.AssetsImported, counts.AssetsUnchanged);
    }

    public async Task<ApplyTestReport> TestAsync(ApplyDocument document, ApplyOptions? options = null,
        bool allowProbe = false, CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        if (document.Tests is null || document.Tests.Count == 0)
            throw new RLoopException("APPLY_TESTS_MISSING", "The apply document does not declare any tests.", ExitCodes.ValidationFailed);
        var prepared = await PrepareAsync(document, options, cancellationToken);
        foreach (var node in prepared.Nodes) node.Id = node.Existing?.Id;
        foreach (var component in prepared.Components)
        {
            component.Id = component.Existing?.Id;
            component.ResolvedType = component.Existing?.Type;
        }
        var byKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null)
            .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
        var slotsByKey = prepared.Nodes.ToDictionary(x => x.StableKey, StringComparer.Ordinal);
        var assetUrls = PlanAssetUrls(prepared);
        var results = new List<ApplyTestCaseResult>();
        foreach (var test in document.Tests ?? [])
        {
            var assertions = new List<ApplyAssertionResult>();
            var childCountBaselines = new Dictionary<ApplyAssertionSpec, int>();
            foreach (var assertion in test.Assertions ?? [])
                if (assertion.Kind.Equals("child-count", StringComparison.OrdinalIgnoreCase) && assertion.Delta is not null)
                    childCountBaselines[assertion] = await CountChildren(assertion, slotsByKey, cancellationToken, refresh: true);
            foreach (var assertion in (test.Assertions ?? []).Where(x => !string.Equals(x.Phase, "after", StringComparison.OrdinalIgnoreCase)))
                assertions.Add(await EvaluateAssertion(test.Name, assertion, "before", byKey, slotsByKey, assetUrls, cancellationToken));

            var probeExecuted = false;
            var structuralOnly = test.Probe is null;
            var capability = test.Probe is null ? "No interaction probe declared; field/reference structure was verified." : string.Empty;
            Func<Task>? restoreProbe = null;
            try
            {
                if (test.Probe is { } probe)
                {
                    if (!probe.Safe)
                        throw new RLoopException("UNSAFE_PROBE_REJECTED", $"Test '{test.Name}' probe must declare safe=true.", ExitCodes.ValidationFailed);
                    if (!allowProbe)
                    {
                        structuralOnly = true;
                        capability = "Probe was not executed. Re-run with --probe --yes after confirming the operation is safe.";
                    }
                    else
                    {
                        var key = SymbolKey(probe.Target);
                        byKey.TryGetValue(key, out var target);
                        if (!string.Equals(probe.Kind, "set-members", StringComparison.OrdinalIgnoreCase) && target?.Existing is null)
                            throw UnknownApplyReference(probe.Target, byKey.Keys);
                        switch (probe.Kind?.ToLowerInvariant())
                        {
                            case "method":
                            {
                                if (string.IsNullOrWhiteSpace(probe.Method))
                                    throw new RLoopException("PROBE_METHOD_MISSING", $"Test '{test.Name}' method probe requires method.", ExitCodes.ValidationFailed);
                                var definition = await client.DescribeComponentTypeAsync(target!.Existing!.Type, cancellationToken);
                                if (definition.Methods?.Any(x => x.Name == probe.Method && !x.IsStatic) != true)
                                {
                                    structuralOnly = true;
                                    capability = $"Runtime method '{probe.Method}' is not exposed by public Reflection; structural assertions only.";
                                }
                                else
                                {
                                    var call = await client.CallComponentMethodAsync(target.Existing.Id, probe.Method, probe.Arguments, cancellationToken);
                                    if (!call.Success)
                                        throw new RLoopException("PROBE_FAILED", call.Error ?? $"Probe '{probe.Method}' failed.", ExitCodes.OperationFailed);
                                    probeExecuted = true;
                                    capability = "Probe executed through the public ResoniteLink SyncMethod API; after assertions were polled.";
                                }
                                break;
                            }
                            case "set-member":
                            case "set-members":
                            {
                                if (!probe.Restore)
                                    throw new RLoopException("PROBE_RESTORE_REQUIRED", $"Test '{test.Name}' set-member probe requires restore=true.", ExitCodes.ValidationFailed);
                                var values = probe.Kind.Equals("set-members", StringComparison.OrdinalIgnoreCase)
                                    ? probe.Values! : new Dictionary<string, JsonElement> { [probe.Target] = probe.Value!.Value };
                                var changes = new List<(string Selector, string Id, string Member, string Original, string Temporary)>();
                                foreach (var pair in values)
                                {
                                    var selector = pair.Key[(pair.Key.IndexOf(':') + 1)..];
                                    var separator = selector.LastIndexOf('.');
                                    if (!byKey.TryGetValue(selector[..separator], out var currentTarget) || currentTarget.Existing is null)
                                        throw UnknownApplyReference(pair.Key, byKey.Keys);
                                    var memberName = selector[(separator + 1)..];
                                    var current = await client.GetComponentAsync(currentTarget.Existing.Id, cancellationToken);
                                    if (!current.Members.TryGetValue(memberName, out var original))
                                        throw new RLoopException("PROBE_MEMBER_NOT_FOUND", $"Probe member '{pair.Key}' was not found.", ExitCodes.NotFound);
                                    if (original.Kind != "field")
                                        throw new RLoopException("PROBE_MEMBER_KIND_UNSUPPORTED", $"Transactional probes support field members; '{pair.Key}' is '{original.Kind}'.", ExitCodes.ValidationFailed);
                                    if (changes.Any(change => change.Id == current.Id && change.Member == memberName))
                                        throw new RLoopException("PROBE_DUPLICATE_TARGET", "Probe aliases refer to the same member.", ExitCodes.ValidationFailed);
                                    var temporary = await ResolveValueAsync(pair.Value, byKey, slotsByKey, assetUrls, cancellationToken);
                                    await client.ValidateComponentMemberAsync(current.Type, memberName, temporary, cancellationToken);
                                    changes.Add((pair.Key, current.Id, memberName, MemberRaw(original), temporary));
                                }
                                var attempted = 0;
                                restoreProbe = async () =>
                                {
                                    var failures = new List<string>();
                                    foreach (var change in changes.Take(attempted).Reverse())
                                    {
                                        try
                                        {
                                            await client.SetComponentMemberAsync(change.Id, change.Member, change.Original, CancellationToken.None);
                                            var restored = await client.GetComponentAsync(change.Id, CancellationToken.None);
                                            if (!restored.Members.TryGetValue(change.Member, out var actual) || !MemberMatchesRaw(actual, change.Original))
                                                failures.Add(change.Selector);
                                        }
                                        catch (Exception) { failures.Add(change.Selector); }
                                    }
                                    if (failures.Count > 0)
                                        throw new RLoopException("PROBE_RESTORE_FAILED", "One or more temporary probe values could not be restored.", ExitCodes.OperationFailed,
                                            new Dictionary<string, object?> { ["targets"] = failures });
                                };
                                foreach (var change in changes)
                                {
                                    attempted++;
                                    await client.SetComponentMemberAsync(change.Id, change.Member, change.Temporary, cancellationToken);
                                }
                                probeExecuted = true;
                                capability = "Transactional field probe executed; after assertions were polled and the original value was restored.";
                                break;
                            }
                            default:
                                throw new RLoopException("PROBE_KIND_UNSUPPORTED", $"Probe kind '{probe.Kind}' is not supported.", ExitCodes.ValidationFailed,
                                    suggestions: ["Use kind 'method', 'set-member', or 'set-members'."]);
                        }
                    }
                }

                foreach (var assertion in (test.Assertions ?? []).Where(x => string.Equals(x.Phase, "after", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!probeExecuted)
                    {
                        assertions.Add(new ApplyAssertionResult(test.Name, assertion.Target, "after", true,
                            assertion.Expected is { } skippedExpected ? JsonNode.Parse(skippedExpected.GetRawText()) : null, null,
                            "After assertion was not evaluated because the runtime probe was unavailable or not authorized.", false));
                        continue;
                    }
                    ApplyAssertionResult evaluated;
                    var deadline = Stopwatch.StartNew();
                    do
                    {
                        evaluated = await EvaluateAssertion(test.Name, assertion, "after", byKey, slotsByKey, assetUrls, cancellationToken, refresh: true,
                            childCountBaselines.GetValueOrDefault(assertion));
                        if (evaluated.Passed) break;
                        await Task.Delay(Math.Clamp(test.PollMs, 10, 5000), cancellationToken);
                    } while (deadline.ElapsedMilliseconds < Math.Clamp(test.TimeoutMs, 10, 60_000));
                    assertions.Add(evaluated);
                }
            }
            finally
            {
                if (restoreProbe is not null) await restoreProbe();
            }
            results.Add(new ApplyTestCaseResult(test.Name, assertions.All(x => x.Passed), structuralOnly,
                probeExecuted, capability, assertions));
        }
        return new ApplyTestReport(results.All(x => x.Passed), results.Any(x => x.StructuralOnly), results.Count,
            results.Count(x => x.Passed), results);
    }

    private async Task<ApplyAssertionResult> EvaluateAssertion(string testName, ApplyAssertionSpec assertion, string phase,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken, bool refresh = false, int? childCountBaseline = null)
    {
        if (assertion.Kind.Equals("child-count", StringComparison.OrdinalIgnoreCase))
        {
            var actualCount = await CountChildren(assertion, slots, cancellationToken, refresh);
            var expectedCount = assertion.Delta is { } delta && childCountBaseline is { } baseline ? baseline + delta :
                assertion.Count ?? (assertion.Expected is { } countElement && countElement.ValueKind == JsonValueKind.Number ? countElement.GetInt32() : actualCount);
            return new ApplyAssertionResult(testName, assertion.Target, phase, actualCount == expectedCount,
                JsonValue.Create(expectedCount), JsonValue.Create(actualCount), actualCount == expectedCount ? "Child count matched." : "Child count differed.");
        }
        if (assertion.Target.StartsWith("$slot:", StringComparison.Ordinal))
        {
            var exists = slots.TryGetValue(assertion.Target[6..], out var slot) && (slot.Existing is not null || slot.Id is not null);
            var expectedExists = assertion.Exists ?? true;
            return new ApplyAssertionResult(testName, assertion.Target, phase, exists == expectedExists,
                JsonValue.Create(expectedExists), JsonValue.Create(exists), exists == expectedExists ? "Slot existence matched." : "Slot existence differed.");
        }
        var selector = assertion.Target.StartsWith("$member:", StringComparison.Ordinal) ? assertion.Target[8..] :
            assertion.Target.StartsWith("$component:", StringComparison.Ordinal) ? assertion.Target[11..] : assertion.Target;
        var separator = selector.LastIndexOf('.');
        if (separator <= 0)
        {
            var componentExists = components.TryGetValue(selector, out var componentRuntime) && componentRuntime.Id is not null;
            var expectedExists = assertion.Exists ?? true;
            return new ApplyAssertionResult(testName, assertion.Target, phase, componentExists == expectedExists,
                JsonValue.Create(expectedExists), JsonValue.Create(componentExists), componentExists == expectedExists ? "Component existence matched." : "Component existence differed.");
        }
        if (!components.TryGetValue(selector[..separator], out var runtime) || runtime.Id is null)
            return new ApplyAssertionResult(testName, assertion.Target, phase, false,
                assertion.Expected is { } missingExpected ? JsonNode.Parse(missingExpected.GetRawText()) : null, null, "Component/member target was not found.");
        var memberName = selector[(separator + 1)..];
        var component = refresh ? await client.GetComponentAsync(runtime.Id, cancellationToken) :
            new ComponentInfo(runtime.Id, runtime.ResolvedType ?? runtime.Spec.Type,
                runtime.Existing?.Members ?? new Dictionary<string, MemberValue>());
        if (!component.Members.TryGetValue(memberName, out var member))
            return new ApplyAssertionResult(testName, assertion.Target, phase, assertion.Exists == false,
                assertion.Expected is { } absentExpected ? JsonNode.Parse(absentExpected.GetRawText()) : JsonValue.Create(assertion.Exists), null, "Member does not exist.");
        if (assertion.Exists is not null)
            return new ApplyAssertionResult(testName, assertion.Target, phase, assertion.Exists.Value,
                JsonValue.Create(assertion.Exists), JsonValue.Create(true), assertion.Exists.Value ? "Member exists." : "Member unexpectedly exists.");
        if (assertion.Expected is not { } expected)
            return new ApplyAssertionResult(testName, assertion.Target, phase, true, null, MemberActual(member), "Member was readable.");
        var raw = await ResolveValueAsync(expected, components, slots, assets, cancellationToken);
        return new ApplyAssertionResult(testName, assertion.Target, phase, MemberMatchesRaw(member, raw),
            JsonNode.Parse(expected.GetRawText()), MemberActual(member), MemberMatchesRaw(member, raw) ? "Value matched." : "Value differed.");
    }

    private async Task<int> CountChildren(ApplyAssertionSpec assertion, IReadOnlyDictionary<string, NodeRuntime> slots,
        CancellationToken cancellationToken, bool refresh)
    {
        if (!assertion.Target.StartsWith("$slot:", StringComparison.Ordinal) ||
            !slots.TryGetValue(assertion.Target[6..], out var runtime) || runtime.Id is null)
            return 0;
        var slot = refresh ? await client.GetSlotAsync(runtime.Id, 1, true, cancellationToken) : runtime.Existing;
        if (slot is null) return 0;
        return slot.Children.Count(child =>
            (assertion.Name is null || child.Name.Equals(assertion.Name, StringComparison.Ordinal)) &&
            (assertion.ComponentType is null || child.Components.Any(component => TypeNamesEquivalent(component.Type, assertion.ComponentType))));
    }

    private static JsonNode? MemberActual(MemberValue member) => member.Kind switch
    {
        "reference" => JsonValue.Create(member.TargetId),
        "list" => new JsonArray((member.Elements ?? []).Select(MemberActual).ToArray()),
        "syncObject" or "dictionary" => new JsonObject((member.Members ?? new Dictionary<string, MemberValue>())
            .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, MemberActual(pair.Value)))),
        "field" => NormalizeNode(member.Value),
        "empty" => null,
        _ => NormalizeNode(member.Value)
    };

    private static string MemberRaw(MemberValue member)
    {
        if (member.Kind == "reference") return member.TargetId ?? "null";
        if (member.Value is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (member.Value is JsonObject obj)
        {
            var coordinates = obj.ContainsKey("x") ? new[] { "x", "y", "z", "w" } :
                obj.ContainsKey("r") ? new[] { "r", "g", "b", "a" } : [];
            var present = coordinates.TakeWhile(obj.ContainsKey).ToArray();
            if (present.Length is >= 2 and <= 4)
                return string.Join(',', present.Select(key => obj[key]?.ToJsonString() ?? "0"));
        }
        return member.Value?.ToJsonString() ?? "null";
    }

    private static string SymbolKey(string value) => value[(value.IndexOf(':') + 1)..].Split('.')[0];

    private async Task<PreparedApply> PrepareAsync(ApplyDocument document, ApplyOptions options, CancellationToken cancellationToken)
    {
        document = GeneratedContentMetadata.AddToGeneratedRoots(document, generatedContentSource);
        var offlineValidation = await ApplyDocumentValidator.ValidateAsync(document, cancellationToken: cancellationToken);
        ApplyDocumentValidator.ThrowIfInvalid(offlineValidation);
        var statePath = ApplyStateStore.ResolvePath(document, options.StateFile);
        var state = ApplyStateStore.Load(statePath, document.Ownership!.Key);
        var session = await client.GetSessionInfoAsync(cancellationToken);
        var sameSession = !string.IsNullOrWhiteSpace(session.UniqueSessionId) && session.UniqueSessionId == state.SessionId;
        state.SessionId = session.UniqueSessionId;
        var migrations = ApplyStateMigrations(document, state);
        var parentSelector = string.IsNullOrWhiteSpace(document.Slot!.Parent) ? "Root" : document.Slot.Parent;
        var parentId = await ResolveSlotIdAsync(parentSelector, cancellationToken);
        var stateDepth = state.Slots.Values.Select(x => x.PathSegments?.Count - 1 ?? x.Path.Count(ch => ch == '/')).DefaultIfEmpty(0).Max();
        var parent = await client.GetSlotAsync(parentId, Math.Clamp(Math.Max(MaxDepth(document.Children) + 1, stateDepth), 0, 64), true, cancellationToken);
        var parentPath = await ObserveAbsolutePathAsync(parent, cancellationToken);
        var parentSegments = await ObserveAbsoluteSegmentsAsync(parent, cancellationToken);
        var snapshots = new List<(SlotInfo Slot, string Path)> { (parent, parentPath) };
        var rootKey = document.Slot!.Key!;
        if (state.Slots.TryGetValue(rootKey, out var rootState) && !IsDirectChild(parent, rootState.Id))
        {
            if (rootState.RuntimeRelocatable && !sameSession)
            {
                var stable = new StableSlotReference(rootKey, rootState.Id, rootState.Path, state.SessionId,
                    state.OwnershipKey, true, rootState.PathSegments);
                var relocated = await ResolveRelocatableSlotAsync(statePath, stable, cancellationToken);
                state.Slots[rootKey] = rootState = rootState with { Id = relocated.Id };
                AddRootSnapshot(snapshots, parent, relocated, relocated.Path ?? rootState.Path);
            }
            else
            try
            {
                var oldRootId = sameSession && !string.IsNullOrWhiteSpace(rootState.Id)
                    ? rootState.Id : await ResolveSlotIdAsync(SlotPaths.Selector(rootState.Path, rootState.PathSegments), cancellationToken);
                AddRootSnapshot(snapshots, parent, await client.GetSlotAsync(oldRootId, Math.Clamp(stateDepth, 0, 64), true, cancellationToken), rootState.Path);
            }
            catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "SLOT_PATH_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
            {
                if (rootState.RuntimeRelocatable)
                {
                    var stable = new StableSlotReference(rootKey, rootState.Id, rootState.Path, state.SessionId,
                        state.OwnershipKey, true, rootState.PathSegments);
                    var relocated = await ResolveRelocatableSlotAsync(statePath, stable, cancellationToken);
                    state.Slots[rootKey] = rootState = rootState with { Id = relocated.Id };
                    AddRootSnapshot(snapshots, parent, relocated, relocated.Path ?? rootState.Path);
                }
            }
        }
        var prepared = new PreparedApply(document, options, state, statePath, session, parentId, sameSession, snapshots,
            migrations.Slots, migrations.Components, parentSegments);
        var rootSpec = new ApplyNodeSpec(document.Slot, document.Components, document.Children);
        BuildNode(prepared, rootSpec, null, parent, parentPath, true);
        await PrepareRelocationTransformsAsync(prepared, parentPath, cancellationToken);
        BuildAssetPlans(prepared);
        ThrowIfRecreateInterruptedAcrossSessions(prepared);
        BuildComponentPlans(prepared);
        BuildDeletionPlans(prepared);
        ValidateInterruptedRecreates(prepared);
        ValidateSlotOwnership(prepared);
        ValidateComponentOwnership(prepared);
        ValidateRecreateReferences(prepared);
        var resolvedTypes = prepared.Components.Where(x => x.Existing is not null)
            .GroupBy(x => x.Spec.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Existing!.Type, StringComparer.Ordinal);
        var strictValidation = await ApplyDocumentValidator.ValidateAsync(document, client, cancellationToken, resolvedTypes);
        ApplyDocumentValidator.ThrowIfInvalid(strictValidation);
        return prepared;
    }

    private static void BuildNode(PreparedApply prepared, ApplyNodeSpec spec, NodeRuntime? parentRuntime,
        SlotInfo parentSnapshot, string parentPath, bool isRoot)
    {
        var path = parentPath.TrimEnd('/') + "/" + spec.Slot.Name;
        var stableKey = spec.Slot.Key ?? "$path:" + path;
        prepared.State.Slots.TryGetValue(stableKey, out var stateSlot);
        var newManagedSlot = stateSlot is null && parentRuntime is not null && prepared.State.Slots.ContainsKey(parentRuntime.StableKey);
        var existing = newManagedSlot ? null : MatchSlot(parentSnapshot, spec.Slot.Name, stateSlot, prepared.SameSession, path);
        existing ??= FindManagedSlot(prepared, stateSlot);
        if (isRoot && existing is not null && stateSlot is null && !prepared.Options.Adopt)
            throw new RLoopException("APPLY_OWNERSHIP_UNVERIFIED",
                $"Slot '{path}' already exists but is not bound to ownership '{prepared.Document.Ownership!.Key}'.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["path"] = path, ["stateFile"] = prepared.StatePath },
                ["Inspect the target, then re-run with --adopt to bind this exact root Slot without deleting it."]);
        var desiredParentId = parentRuntime?.Existing?.Id ?? (parentRuntime is null ? prepared.ParentId : null);
        var relocating = existing is not null && (desiredParentId is null || existing.ParentId != desiredParentId);
        if (relocating && spec.Slot.RuntimeRelocatable && stateSlot is not null &&
            NormalizePath(stateSlot.Path) == NormalizePath(path))
            throw new RLoopException("APPLY_RUNTIME_RELOCATABLE_ACTIVE",
                $"Managed Slot '{stableKey}' is currently outside its declared parent, which is expected for a runtime-relocatable item.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?>
                {
                    ["key"] = stableKey, ["declaredPath"] = path, ["currentParentId"] = existing!.ParentId
                }, ["Drop or return the item to its declared parent before apply; the command stopped before mutation."]);
        var action = existing is null ? "create" : relocating ? "relocate" : SlotNeedsUpdate(existing, spec.Slot) ? "update" : "no-op";
        var node = new NodeRuntime(spec.Slot, spec.Components ?? [], parentRuntime, existing, stableKey, path, action);
        node.PathSegments = [.. parentRuntime?.PathSegments ?? prepared.ParentSegments, spec.Slot.Name];
        prepared.Nodes.Add(node);
        var planAction = action == "update" && existing?.Name != spec.Slot.Name ? "rename" : action;
        var migratedFrom = prepared.SlotMigrations.GetValueOrDefault(stableKey);
        prepared.Entries.Add(new ApplyPlanEntry(planAction, "slot", path, stableKey,
            Reason: action == "create" ? "managed Slot does not exist" : action == "relocate" ?
                $"stable key '{stableKey}' preserves identity while the parent changes; relocationTransform={spec.Slot.RelocationTransform}" : planAction == "rename" ?
                $"stable key '{stableKey}' preserves identity while the name changes from '{existing!.Name}' to '{spec.Slot.Name}'" :
                action == "update" ? "one or more managed transforms differ" : migratedFrom is not null ?
                $"stable key migrated from '{migratedFrom}' without recreating the Slot" : "Slot already matches"));

        var childParent = existing ?? new SlotInfo("", spec.Slot.Name, null, null, null, null, null, null, null, false, [], []);
        for (var i = 0; i < (spec.Children?.Count ?? 0); i++)
            BuildNode(prepared, spec.Children![i], node, childParent, path, false);
    }

    private static void BuildComponentPlans(PreparedApply prepared)
    {
        foreach (var node in prepared.Nodes)
        {
            var typeOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var spec in node.ComponentSpecs)
            {
                var normalizedType = NormalizeType(spec.Type);
                var ordinal = typeOrdinals.GetValueOrDefault(normalizedType);
                typeOrdinals[normalizedType] = ordinal + 1;
                var stableKey = spec.Key ?? $"{node.StableKey}/component:{normalizedType}:{ordinal}";
                prepared.State.Components.TryGetValue(stableKey, out var stateComponent);
                var relocating = stateComponent is not null && stateComponent.SlotKey != node.StableKey;
                var topologyTargets = stateComponent is null ? null :
                    ResolveStateTopologyTargets(prepared, stateComponent, new HashSet<string>(StringComparer.Ordinal));
                // A new key on an already managed Slot is a new object, not an ordinal rename.
                // Otherwise it can alias a retained key or a stale key scheduled for pruning.
                var newManagedComponent = stateComponent is null && prepared.State.Slots.ContainsKey(node.StableKey);
                // An interrupted recreate resumes by ID only: never by type or position.
                var resumed = !relocating && !string.IsNullOrEmpty(stateComponent?.SupersededId);
                ComponentSummary? superseded = null;
                ComponentSummary? existing;
                if (resumed)
                {
                    var components = node.Existing?.Components ?? [];
                    superseded = components.FirstOrDefault(candidate => candidate.Id == stateComponent!.SupersededId);
                    existing = string.IsNullOrEmpty(stateComponent!.Id) || stateComponent.Id == stateComponent.SupersededId ? null :
                        components.FirstOrDefault(candidate => candidate.Id == stateComponent.Id);
                }
                else
                {
                    existing = relocating || newManagedComponent ? null :
                        MatchComponent(node.Existing?.Components ?? [], spec.Type, ordinal, stateComponent, prepared.SameSession,
                            topologyTargets);
                }
                // Like a move, a recreate needs a saved record: the first adopting apply updates existing content in place.
                // A resumed recreate whose replaced Component is already gone starts again if the declaration shrank further.
                var recreateReason = existing is null || stateComponent is null || (resumed && superseded is not null) ? null :
                    ListShrinkReason(spec, existing);
                // A key whose create was interrupted has no saved ID, so this Component was matched by type and position only.
                // It may be unmanaged content; never remove it as a replaced Component.
                if (recreateReason is not null && stateComponent?.Id is { Length: 0 })
                    throw new RLoopException("STABLE_COMPONENT_AMBIGUOUS",
                        $"Component '{stableKey}' has no saved ID, so it was matched by type and position only, and its list is longer than declared. Apply does not recreate a Component it cannot verify. No mutations were performed.",
                        ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["candidateIds"] = new[] { existing!.Id }, ["type"] = existing.Type, ["componentKey"] = stableKey },
                        ["If an interrupted apply created this Component, remove it with 'resoloop component remove ID --yes'; otherwise it is unmanaged content and should stay.",
                         $"Then back up the state file, remove components.{stableKey} from it, and re-run apply; apply creates the Component for this key."]);
                if (recreateReason is not null) (superseded, existing) = (existing, null);
                var componentIndex = existing is null
                    ? (node.Existing?.Components.Count ?? 0) + prepared.Components.Count(candidate => candidate.Node == node && candidate.Existing is null)
                    : node.Existing!.Components.ToList().FindIndex(candidate => candidate.Id == existing.Id);
                var runtime = new ComponentRuntime(spec, node, existing, stableKey, ordinal,
                    node.Path + "/@" + (spec.Key ?? normalizedType + "[" + ordinal + "]"), componentIndex)
                {
                    Superseded = superseded,
                    SupersededId = recreateReason is not null ? superseded!.Id : resumed ? stateComponent!.SupersededId : null,
                    RecreateReason = recreateReason,
                    Resumed = resumed && recreateReason is null
                };
                if (relocating)
                {
                    var sourceSlot = FindStateSlot(prepared, stateComponent!.SlotKey);
                    // An interrupted recreate never matches by type or position on its old Slot; ValidateInterruptedRecreates
                    // stops it with key-not-declared.
                    runtime.RelocationSource = sourceSlot is null || !string.IsNullOrEmpty(stateComponent.SupersededId) ? null :
                        MatchComponent(sourceSlot.Components, spec.Type, ordinal, stateComponent, prepared.SameSession,
                            topologyTargets);
                    if (runtime.RelocationSource?.Id == existing?.Id) runtime.RelocationSource = null;
                    if (runtime.RelocationSource is not null)
                        (runtime.RelocationSourceSlotKey, runtime.RelocationSourceSlotId) = (stateComponent.SlotKey, sourceSlot!.Id);
                }
                prepared.Components.Add(runtime);
            }
        }

        DetectSavedAssetMigrations(prepared);
        var existingByKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null)
            .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
        var slotsByKey = prepared.Nodes.ToDictionary(x => x.StableKey, StringComparer.Ordinal);
        var assetUrls = PlanAssetUrls(prepared);
        foreach (var component in prepared.Components)
        {
            var action = component.RelocationSource is not null ? "relocate" : component.SupersededId is not null ? "recreate" :
                component.Existing is null ? "create" : "no-op";
            var reason = component.RelocationSource is not null
                ? $"stable key '{component.StableKey}' moves the Component to Slot '{component.Node.StableKey}'"
                : component.SupersededId is not null ? RecreatePlanReason(component)
                : component.Existing is null ? "managed Component does not exist" : "Component and fields already match";
            var diffs = new List<ApplyMemberDiff>();
            var observed = component.Existing ?? component.Superseded;
            if (observed is not null)
            {
                foreach (var field in component.Spec.Fields ?? new Dictionary<string, JsonElement>())
                {
                    var resolvable = TryResolveRawForPlan(field.Value, existingByKey, slotsByKey, assetUrls, out var raw);
                    MemberValue? current = null;
                    var found = observed.Members?.TryGetValue(field.Key, out current) == true;
                    if (!resolvable || !found || !MemberMatchesRaw(current!, raw))
                    {
                        if (action != "recreate") (action, reason) = ("update", "one or more fields differ or depend on a new target");
                        diffs.Add(found && current!.Kind == "list" && resolvable
                            ? ListDiff(field.Key, current, raw)
                            : new ApplyMemberDiff(field.Key, "replace", Reason: !resolvable ? "depends on a target created by this apply" : !found ? "member is absent from snapshot" : "value differs"));
                    }
                }
            }
            var migratedFrom = prepared.ComponentMigrations.GetValueOrDefault(component.StableKey);
            prepared.Entries.Add(new ApplyPlanEntry(action, "component", component.Path, component.StableKey,
                component.Spec.Type, component.Spec.Fields?.Keys.ToArray(), reason, diffs));
            if (action == "no-op" && migratedFrom is not null)
                prepared.Entries[^1] = prepared.Entries[^1] with
                { Reason = $"stable key migrated from '{migratedFrom}' without recreating the Component" };
        }
    }

    // ResoniteLink 0.13.1 replaces only the leading elements of a list and cannot remove any (observed on Resonite
    // 2026.9.18.82), so a declared list shorter than the runtime list never converges in place.
    private static string? ListShrinkReason(ApplyComponentSpec spec, ComponentSummary existing)
    {
        var shrinks = (spec.Fields ?? new Dictionary<string, JsonElement>())
            .Where(field => field.Value.ValueKind == JsonValueKind.Array &&
                            existing.Members?.GetValueOrDefault(field.Key) is { Kind: "list" } current &&
                            (current.Elements?.Count ?? 0) > field.Value.GetArrayLength())
            .Select(field => $"list member '{field.Key}' shrinks from {existing.Members![field.Key].Elements!.Count} to {field.Value.GetArrayLength()} elements")
            .ToArray();
        return shrinks.Length == 0 ? null : string.Join("; ", shrinks) +
            "; ResoniteLink cannot remove list elements, so apply replaces the Component (initialFields return to their declared values and undeclared members to their type defaults)";
    }

    private static string RecreatePlanReason(ComponentRuntime component) => component.RecreateReason ??
        (component.Existing, component.Superseded) switch
        {
            (null, null) => "resumes an interrupted recreate: the replaced Component is already removed; apply creates the replacement",
            (null, { } replaced) => $"resumes an interrupted recreate: creates the replacement, then removes the replaced Component '{replaced.Id}'",
            (_, null) => "resumes an interrupted recreate: the replaced Component is already removed; apply saves the final Component indexes",
            (_, { } replaced) => $"resumes an interrupted recreate: removes the replaced Component '{replaced.Id}' after managed references point at the replacement"
        };

    private static ApplyMemberDiff ListDiff(string member, MemberValue current, string desiredRaw)
    {
        var desired = JsonNode.Parse(desiredRaw) as JsonArray ?? [];
        var actual = new JsonArray((current.Elements ?? []).Select(MemberActual).ToArray());
        var desiredCounts = desired.GroupBy(x => x?.ToJsonString() ?? "null").ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var actualCounts = actual.GroupBy(x => x?.ToJsonString() ?? "null").ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var added = desired.Where(item => actualCounts.GetValueOrDefault(item?.ToJsonString() ?? "null") <
            desired.TakeWhile(candidate => !ReferenceEquals(candidate, item)).Count(candidate => (candidate?.ToJsonString() ?? "null") == (item?.ToJsonString() ?? "null")) + 1).Select(x => x?.DeepClone()).ToArray();
        var removed = actual.Where(item => desiredCounts.GetValueOrDefault(item?.ToJsonString() ?? "null") <
            actual.TakeWhile(candidate => !ReferenceEquals(candidate, item)).Count(candidate => (candidate?.ToJsonString() ?? "null") == (item?.ToJsonString() ?? "null")) + 1).Select(x => x?.DeepClone()).ToArray();
        return new ApplyMemberDiff(member, "list", added, removed,
            "ResoniteLink 0.13.1 applies the list atomically as one member; the preview exposes element additions/removals.");
    }

    private static void BuildAssetPlans(PreparedApply prepared)
    {
        foreach (var pair in prepared.Document.Assets ?? new Dictionary<string, ApplyAssetSpec>())
        {
            string resolved;
            string hash;
            string? direct = null;
            var hasAbsoluteUri = Uri.TryCreate(pair.Value.Source, UriKind.Absolute, out var uri);
            if (!Path.IsPathFullyQualified(pair.Value.Source) && hasAbsoluteUri && uri!.Scheme != Uri.UriSchemeFile)
            {
                direct = uri.ToString();
                resolved = direct;
                hash = "uri:" + direct;
            }
            else
            {
                var sourceDirectory = Path.GetDirectoryName(prepared.Document.SourcePath) ?? Environment.CurrentDirectory;
                resolved = uri?.Scheme == Uri.UriSchemeFile ? uri.LocalPath : Path.GetFullPath(pair.Value.Source, sourceDirectory);
                if (!File.Exists(resolved))
                    throw new RLoopException("ASSET_SOURCE_NOT_FOUND", $"Asset '{pair.Key}' source '{resolved}' does not exist.", ExitCodes.NotFound);
                using var stream = File.OpenRead(resolved);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            var unchanged = direct is not null || prepared.State.Assets.TryGetValue(pair.Key, out var state) &&
                state.SourceHash == hash && state.Kind.Equals(pair.Value.Kind, StringComparison.OrdinalIgnoreCase);
            var runtime = new AssetRuntime(pair.Key, pair.Value, resolved, hash, direct, unchanged ? "no-op" : "create");
            prepared.Assets.Add(runtime);
            prepared.Entries.Insert(0, new ApplyPlanEntry(runtime.Action, "asset", "$assets/" + pair.Key, pair.Key, pair.Value.Kind,
                Reason: direct is not null ? "asset URI is already addressable" : unchanged ?
                    "source hash and imported URL match state" : "source is new or changed and must be imported"));
        }
    }

    private static Dictionary<string, string> PlanAssetUrls(PreparedApply prepared) =>
        prepared.Assets.Where(x => x.DirectUrl is not null || prepared.State.Assets.ContainsKey(x.Key))
            .ToDictionary(x => x.Key, x => x.DirectUrl ?? x.MigratedUrl ?? prepared.State.Assets[x.Key].Url, StringComparer.Ordinal);

    // Saving a world moves imported local:// assets into the saved record and rewrites every live URL to resdb:///.
    // State still holds the local URL, so an unchanged asset would otherwise push the unportable local URL back.
    // Adopt the live URL only when every managed reference observed the same resdb URI.
    // A relocated or recreated Component is observed through the live Component it replaces.
    // A field counts only while its declaration matches the last apply: a repointed field still holds another asset's URL.
    private static void DetectSavedAssetMigrations(PreparedApply prepared)
    {
        var candidates = prepared.Assets.Where(asset => asset.Action == "no-op" && asset.DirectUrl is null &&
                prepared.State.Assets.TryGetValue(asset.Key, out var saved) &&
                Uri.TryCreate(saved.Url, UriKind.Absolute, out var uri) && uri.Scheme == "local")
            .ToDictionary(asset => asset.Key, StringComparer.Ordinal);
        if (candidates.Count == 0) return;
        var observed = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        void Collect(JsonElement desired, MemberValue? live, bool verified)
        {
            if (desired.ValueKind == JsonValueKind.String)
            {
                var text = desired.GetString() ?? string.Empty;
                if (!text.StartsWith("$asset:", StringComparison.Ordinal) || !candidates.ContainsKey(text[7..])) return;
                if (!verified && live is { Kind: "field", Value: JsonValue legacyValue } &&
                    legacyValue.TryGetValue<string>(out var legacyUrl) &&
                    Uri.TryCreate(legacyUrl, UriKind.Absolute, out var legacyUri) && legacyUri.Scheme == "resdb")
                    throw new RLoopException("APPLY_ASSET_MIGRATION_UNVERIFIED",
                        $"Legacy state cannot verify which asset the saved URL for '{text[7..]}' belongs to. No changes were made.",
                        ExitCodes.ValidationFailed, new Dictionary<string, object?>
                        { ["assetKey"] = text[7..], ["stateFile"] = prepared.StatePath, ["liveUrl"] = legacyUrl },
                        ["Inspect the saved asset and explicitly declare its verified resdb URI as the asset source, or restore a state with recorded assetFields. Do not infer the mapping from a changed manifest."]);
                if (!observed.TryGetValue(text[7..], out var values)) observed[text[7..]] = values = [];
                values.Add(live is { Kind: "field", Value: JsonValue value } && value.TryGetValue<string>(out var url) ? url : null);
            }
            else if (desired.ValueKind == JsonValueKind.Array)
            {
                var elements = live?.Elements ?? [];
                var index = 0;
                foreach (var item in desired.EnumerateArray())
                    Collect(item, index < elements.Count ? elements[index++] : null, verified);
            }
            else if (desired.ValueKind == JsonValueKind.Object)
                foreach (var property in desired.EnumerateObject())
                    Collect(property.Value, live?.Members?.GetValueOrDefault(property.Name), verified);
        }
        foreach (var component in prepared.Components)
        {
            var live = component.Existing ?? component.RelocationSource ?? component.Superseded;
            if (live is null) continue;
            // Legacy state has no declaration provenance. Never guess a saved URL's asset from today's manifest.
            var applied = prepared.State.Components.GetValueOrDefault(component.StableKey)?.AssetFields;
            foreach (var field in component.Spec.Fields ?? new Dictionary<string, JsonElement>())
            {
                if (applied is not null && (!applied.TryGetValue(field.Key, out var previous) || !JsonElement.DeepEquals(previous, field.Value)))
                    continue;
                Collect(field.Value, live.Members?.GetValueOrDefault(field.Key), applied is not null);
            }
        }
        foreach (var (key, values) in observed)
        {
            var live = values.Distinct(StringComparer.Ordinal).ToArray();
            if (live is not [{ } url] || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "resdb") continue;
            candidates[key].MigratedUrl = url;
            var index = prepared.Entries.FindIndex(entry => entry.Kind == "asset" && entry.Key == key);
            if (index >= 0)
                prepared.Entries[index] = prepared.Entries[index] with
                { Reason = "asset URL was migrated to resdb by a world save; state will record the live URL" };
        }
    }

    // Persist this before a remote write: the server may apply fields and then lose its response.
    // Retain evidence for untouched fields, and restore changed declarations only after success.
    private static void ForgetComponentAssetEvidence(PreparedApply prepared, string stableKey, IEnumerable<string> members)
    {
        var saved = prepared.State.Components[stableKey];
        var changed = members.ToHashSet(StringComparer.Ordinal);
        prepared.State.Components[stableKey] = saved with
        {
            AssetFields = saved.AssetFields?.Where(field => !changed.Contains(field.Key)).ToDictionary(StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>()
        };
    }

    // A re-import leaves every live reference on the previous content until its field is written again, and a world save
    // in between moves that content to resdb. Until the fields are written, the record must not offer those references as
    // evidence, or an interrupted apply would adopt the previous content's URL. A state written before asset fields were
    // recorded cannot tell which field held the asset, so only Components that declare it now stop counting.
    private static void ForgetAssetEvidence(PreparedApply prepared, string key)
    {
        var declared = prepared.Components.Select(component => component.StableKey).ToHashSet(StringComparer.Ordinal);
        var declaring = prepared.Components.Where(component => (component.Spec.Fields ?? new Dictionary<string, JsonElement>())
            .Values.Any(value => ContainsAssetReference(value, key))).Select(component => component.StableKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (stableKey, saved) in prepared.State.Components.ToArray())
        {
            var fields = saved.AssetFields is { } applied
                ? applied.Where(field => !ContainsAssetReference(field.Value, key)).ToDictionary(StringComparer.Ordinal)
                : declared.Contains(stableKey) && !declaring.Contains(stableKey) ? null : new Dictionary<string, JsonElement>();
            prepared.State.Components[stableKey] = saved with { AssetFields = fields };
        }
    }

    private static void BuildDeletionPlans(PreparedApply prepared)
    {
        var liveSlotKeys = prepared.Nodes.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var liveComponentKeys = prepared.Components.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var rootId = prepared.Nodes[0].Existing?.Id;
        var snapshots = prepared.SnapshotSlots.GroupBy(slot => slot.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        bool IsWithin(string id, string? ancestor, bool includeRoot = true)
        {
            if (ancestor is null) return false;
            if (!includeRoot && id == ancestor) return false;
            for (var depth = 0; depth <= 64 && snapshots.TryGetValue(id, out var current); depth++)
            {
                if (id == ancestor) return true;
                if (current.ParentId is null) break;
                id = current.ParentId;
            }
            return false;
        }
        SlotInfo? ResolveOwned(ApplyStateSlot state) => prepared.SameSession && !string.IsNullOrEmpty(state.Id)
            ? snapshots.GetValueOrDefault(state.Id) : FindManagedSlot(prepared, state);

        var staleSlots = new List<(string Key, ApplyStateSlot State, SlotInfo Slot, string Path)>();
        foreach (var stateSlot in prepared.State.Slots.Where(x => !liveSlotKeys.Contains(x.Key)).ToArray())
        {
            var slot = ResolveOwned(stateSlot.Value);
            if (slot is null || !IsWithin(slot.Id, rootId, false) || slot.Id == "Root") continue;
            staleSlots.Add((stateSlot.Key, stateSlot.Value, slot, slot.Path ?? stateSlot.Value.Path));
            // Keep the binding: once a checkpoint records this session, a stale ID would hide the Slot from later plans.
            if (stateSlot.Value.Id != slot.Id) prepared.State.Slots[stateSlot.Key] = stateSlot.Value with { Id = slot.Id };
        }
        var parentSlotDeletions = staleSlots.OrderBy(x => x.Path.Count(ch => ch == '/'))
            .Where(candidate => !staleSlots.Any(other => other.Slot.Id != candidate.Slot.Id && IsWithin(candidate.Slot.Id, other.Slot.Id)))
            .ToArray();
        var coveredSlotKeys = staleSlots.Where(stateSlot => parentSlotDeletions.Any(deletion =>
                IsWithin(stateSlot.Slot.Id, deletion.Slot.Id)))
            .Select(stateSlot => stateSlot.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var stateComponent in prepared.State.Components.Where(x => !liveComponentKeys.Contains(x.Key) &&
                     !coveredSlotKeys.Contains(x.Value.SlotKey)).ToArray())
        {
            if (!prepared.State.Slots.TryGetValue(stateComponent.Value.SlotKey, out var stateSlot)) continue;
            var slot = ResolveOwned(stateSlot);
            if (slot is null || !IsWithin(slot.Id, rootId)) continue;
            var component = prepared.SameSession ? slot.Components.FirstOrDefault(x => x.Id == stateComponent.Value.Id) : null;
            if (!prepared.SameSession)
            {
                var topology = ResolveStateTopologyTargets(prepared, stateComponent.Value, new HashSet<string>(StringComparer.Ordinal));
                component = MatchComponent(slot.Components, stateComponent.Value.Type,
                    stateComponent.Value.TypeOrdinal, stateComponent.Value, false, topology);
            }
            if (component is null) continue;
            // Keep the binding and its position, so later plans in this session and SaveSlotIndexesAsync still find it.
            var index = slot.Components.ToList().FindIndex(candidate => candidate.Id == component.Id);
            if (stateComponent.Value.Id != component.Id || stateComponent.Value.ComponentIndex != index)
                prepared.State.Components[stateComponent.Key] = stateComponent.Value with { Id = component.Id, ComponentIndex = index };
            var deletion = new DeletionRuntime("component", stateComponent.Key, component.Id,
                slot.Path + "/@" + stateComponent.Key, "stable key is no longer declared inside the owned boundary",
                SlotKey: stateComponent.Value.SlotKey, SlotId: slot.Id);
            prepared.Deletions.Add(deletion);
            prepared.Entries.Add(new ApplyPlanEntry("delete", deletion.Kind, deletion.Path, deletion.Key,
                component.Type, Reason: deletion.Reason));
        }

        foreach (var staleSlot in parentSlotDeletions)
        {
            var removedSlotKeys = staleSlots.Where(stateSlot => IsWithin(stateSlot.Slot.Id, staleSlot.Slot.Id))
                .Select(stateSlot => stateSlot.Key).ToArray();
            var removedComponentKeys = prepared.State.Components.Where(component => !liveComponentKeys.Contains(component.Key) &&
                    removedSlotKeys.Contains(component.Value.SlotKey, StringComparer.Ordinal))
                .Select(component => component.Key).ToArray();
            var deletion = new DeletionRuntime("slot", staleSlot.Key, staleSlot.Slot.Id, staleSlot.Path,
                $"stable parent Slot is no longer declared; one Slot delete covers {removedSlotKeys.Length} managed Slot(s) and {removedComponentKeys.Length} Component(s)",
                removedSlotKeys, removedComponentKeys);
            prepared.Deletions.Add(deletion);
            prepared.Entries.Add(new ApplyPlanEntry("delete", deletion.Kind, deletion.Path, deletion.Key,
                Reason: deletion.Reason));
        }
    }

    private static void ValidateComponentOwnership(PreparedApply prepared)
    {
        var claims = prepared.Components.Where(component => component.Existing is not null)
            .Select(component => (Id: component.Existing!.Id, Key: component.StableKey, Kind: "live"))
            .Concat(prepared.Components.Where(component => component.RelocationSource is not null)
                .Select(component => (Id: component.RelocationSource!.Id, Key: component.StableKey, Kind: "relocation")))
            .Concat(prepared.Components.Where(component => component.Superseded is not null)
                .Select(component => (Id: component.Superseded!.Id, Key: component.StableKey, Kind: "superseded")))
            .Concat(prepared.Deletions.Where(deletion => deletion.Kind == "component")
                .Select(deletion => (deletion.Id, deletion.Key, Kind: "delete")));
        var conflicts = claims.GroupBy(claim => claim.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new { id = group.Key, claims = group.Select(claim => new { claim.Key, claim.Kind }).ToArray() })
            .ToArray();
        if (conflicts.Length > 0)
            throw new RLoopException("APPLY_COMPONENT_OWNERSHIP_CONFLICT",
                "Multiple managed keys or deletion operations claim the same runtime Component. No mutations were performed.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["conflicts"] = conflicts },
                ["Inspect the exact conflicting Components and preserve the checkpoint. Use migrateFrom for an intentional key rename; never repair this by guessing IDs."]);
    }

    // An interrupted recreate resumes only by ID. In another session those IDs no longer name the replaced Component and
    // its replacement, so stop before mutation.
    private static void ThrowIfRecreateInterruptedAcrossSessions(PreparedApply prepared)
    {
        if (prepared.SameSession) return;
        foreach (var (key, saved) in prepared.State.Components.Where(pair => !string.IsNullOrEmpty(pair.Value.SupersededId)))
            throw RecreateInterrupted(prepared, "session-changed", key, saved);
    }

    // Resume only while the document declares the key on the same Slot, and never undo a replacement that something may
    // already reference. Create a lost replacement again only when no same-type Component on the Slot could be it; never
    // pick one by position.
    private static void ValidateInterruptedRecreates(PreparedApply prepared)
    {
        var declared = prepared.Components.ToDictionary(component => component.StableKey, StringComparer.Ordinal);
        foreach (var (key, saved) in prepared.State.Components.Where(pair => !string.IsNullOrEmpty(pair.Value.SupersededId)))
            if (!declared.TryGetValue(key, out var component) || component.Node.StableKey != saved.SlotKey)
                throw RecreateInterrupted(prepared, "key-not-declared", key, saved);
        // A resumed replacement cannot shrink in place, and undoing it would break the references that an earlier apply
        // moved to it, so a declaration shorter than the replacement stops while anything apply read references it.
        foreach (var component in prepared.Components.Where(component => component.Resumed && component.Existing is not null &&
                     component.Superseded is not null && ListShrinkReason(component.Spec, component.Existing) is not null))
            if (ReferencesTo(prepared, component.Existing!).Length > 0)
                throw RecreateInterrupted(prepared, "declaration-shrank", component.StableKey,
                    prepared.State.Components[component.StableKey], [component.Existing!]);
        var owned = prepared.Components
            .SelectMany(component => new[] { component.Existing?.Id, component.RelocationSource?.Id, component.Superseded?.Id })
            .Concat(prepared.Deletions.Where(deletion => deletion.Kind == "component").Select(deletion => deletion.Id))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var component in prepared.Components.Where(component => component.Resumed && component.Existing is null))
        {
            var unowned = (component.Node.Existing?.Components ?? [])
                .Where(candidate => TypeNamesEquivalent(candidate.Type, component.Spec.Type) && !owned.Contains(candidate.Id)).ToArray();
            if (unowned.Length > 0)
                throw RecreateInterrupted(prepared, "replacement-unverified", component.StableKey,
                    prepared.State.Components[component.StableKey], unowned);
        }
    }

    private static RLoopException RecreateInterrupted(PreparedApply prepared, string reason, string key, ApplyStateComponent saved,
        IReadOnlyList<ComponentSummary>? candidates = null)
    {
        candidates ??= (prepared.Nodes.FirstOrDefault(node => node.StableKey == saved.SlotKey)?.Existing?.Components ?? [])
            .Where(component => TypeNamesEquivalent(component.Type, saved.Type)).ToArray();
        // After a reload, nothing tells the recreate's Components apart from those of another same-type key on the Slot.
        var sameTypeKeys = prepared.State.Components.Any(pair => pair.Key != key && pair.Value.SlotKey == saved.SlotKey &&
            TypeNamesEquivalent(pair.Value.Type, saved.Type));
        var (message, suggestions) = reason switch
        {
            "session-changed" => (
                $"A list-shrink recreate of '{key}' was interrupted and the world session changed, so the replaced Component and its replacement can no longer be told apart by ID. No mutations were performed.",
                sameTypeKeys
                    ? new[] { "Another key on this Slot has the same type, so the candidates include its Components and apply cannot tell the recreate's Components apart. Keep the state file as it is, check in Resonite what each candidate references and what references it, and do not repair this by guessing IDs." }
                    : new[] { "Keep the candidate that the Components in its referencedBy point at, or either one if none is referenced, and remove the other with 'resoloop component remove ID --yes'. Use only the IDs in candidates; supersededId and savedId are IDs from the earlier session.",
                              $"Then back up the state file, set components.{key}.id to its supersededId if the ID is empty, delete supersededId, and re-run apply." }),
            "key-not-declared" => (
                $"A list-shrink recreate of '{key}' was interrupted, and the document no longer declares the key on its Slot. No mutations were performed.",
                new[] { "Declare the key again on the same Slot and re-run apply in this session to finish the recreate. Then remove the key and apply with --prune --yes if you no longer want it." }),
            "declaration-shrank" => (
                $"A list-shrink recreate of '{key}' was interrupted, and the document now declares a list shorter than its replacement holds while something references the replacement. No mutations were performed.",
                new[] { "Declare at least as many elements as the replacement holds (see candidates) and re-run apply in this session to finish the recreate, then shorten the list again." }),
            _ => (
                $"A list-shrink recreate of '{key}' was interrupted before its replacement ID was saved, and the Slot holds a same-type Component that no key owns. No mutations were performed.",
                new[] { "If a candidate's lists match the declaration, it is most likely the replacement that the interrupted apply created. If the declaration references a Component that the same apply creates later, apply creates the replacement without initial values, so its lists are all empty; a Component with empty lists that you added looks the same. Inspect each candidate with 'resoloop component inspect ID', remove only one you can confirm the interrupted apply created with 'resoloop component remove ID --yes', and re-run apply; apply creates the replacement again.",
                        $"If no candidate fits that description, back up the state file, set components.{key}.id back to its supersededId, delete supersededId, and re-run apply to start the recreate again." })
        };
        return new RLoopException("APPLY_RECREATE_INTERRUPTED", message, ExitCodes.ValidationFailed, new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["stateFile"] = prepared.StatePath,
            ["componentKey"] = key,
            ["slotKey"] = saved.SlotKey,
            ["slotPath"] = prepared.State.Slots.GetValueOrDefault(saved.SlotKey)?.Path,
            ["type"] = saved.Type,
            ["supersededId"] = saved.SupersededId,
            ["savedId"] = saved.Id,
            ["candidates"] = candidates.Select(component => new
            {
                id = component.Id,
                lists = (component.Members ?? new Dictionary<string, MemberValue>()).Where(member => member.Value.Kind == "list")
                    .ToDictionary(member => member.Key, member => member.Value.Elements?.Count ?? 0, StringComparer.Ordinal),
                referencedBy = ReferencesTo(prepared, component)
            }).ToArray()
        }, suggestions);
    }

    // The Components in the snapshots that apply read whose members point at this Component or at one of its members.
    private static object[] ReferencesTo(PreparedApply prepared, ComponentSummary target)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { target.Id };
        void CollectIds(MemberValue member)
        {
            if (!string.IsNullOrWhiteSpace(member.Id)) ids.Add(member.Id);
            foreach (var child in member.Members?.Values ?? []) CollectIds(child);
            foreach (var element in member.Elements ?? []) CollectIds(element);
        }
        foreach (var member in target.Members?.Values ?? []) CollectIds(member);
        static IEnumerable<string> Targets(MemberValue member) =>
            (member.Kind == "reference" && member.TargetId is { } id ? [id] : Enumerable.Empty<string>())
                .Concat((member.Members?.Values ?? []).SelectMany(Targets))
                .Concat((member.Elements ?? []).SelectMany(Targets));
        return prepared.SnapshotSlots.DistinctBy(slot => slot.Id).SelectMany(slot => slot.Components)
            .Where(component => component.Id != target.Id)
            .SelectMany(component => (component.Members ?? new Dictionary<string, MemberValue>())
                .Where(member => Targets(member.Value).Any(ids.Contains))
                .Select(member => (object)new { id = component.Id, type = component.Type, member = member.Key }))
            .ToArray();
    }

    // Recreating gives the Component and its members new IDs. Apply re-points only the declared reference fields of managed
    // Components, so stop before mutation when anything else in the snapshots that apply read points at a replaced Component.
    private static void ValidateRecreateReferences(PreparedApply prepared)
    {
        var recreates = prepared.Components.Where(component => component.Superseded is not null).ToArray();
        if (recreates.Length == 0) return;
        var targets = new Dictionary<string, ComponentRuntime>(StringComparer.Ordinal);
        void CollectIds(MemberValue member, ComponentRuntime owner)
        {
            if (!string.IsNullOrWhiteSpace(member.Id)) targets[member.Id] = owner;
            foreach (var child in member.Members?.Values ?? []) CollectIds(child, owner);
            foreach (var element in member.Elements ?? []) CollectIds(element, owner);
        }
        foreach (var component in recreates)
        {
            targets[component.Superseded!.Id] = component;
            foreach (var member in component.Superseded.Members?.Values ?? []) CollectIds(member, component);
        }
        // Apply re-points a reference only where the declaration puts a selector at the same path: a partial syncObject
        // declaration writes only its declared children, so a child it leaves out keeps pointing at the replaced Component.
        static IEnumerable<(string Target, bool Repointed)> References(MemberValue member, JsonElement? declared)
        {
            if (member.Kind == "reference" && member.TargetId is { } target)
                yield return (target, declared is { } value && ContainsWorldReference(value));
            foreach (var (name, child) in member.Members ?? new Dictionary<string, MemberValue>())
            {
                JsonElement? declaredChild = declared is { ValueKind: JsonValueKind.Object } parent &&
                    parent.TryGetProperty(name, out var property) ? property : null;
                foreach (var reference in References(child, declaredChild)) yield return reference;
            }
            var elements = member.Elements ?? [];
            for (var i = 0; i < elements.Count; i++)
            {
                JsonElement? declaredElement = declared is { ValueKind: JsonValueKind.Array } array && i < array.GetArrayLength() ? array[i] : null;
                foreach (var reference in References(elements[i], declaredElement)) yield return reference;
            }
        }
        var removed = recreates.Select(component => component.Superseded!.Id)
            .Concat(prepared.Components.Where(component => component.RelocationSource is not null).Select(component => component.RelocationSource!.Id))
            .ToHashSet(StringComparer.Ordinal);
        var managed = prepared.Components.Where(component => component.Existing is not null)
            .ToDictionary(component => component.Existing!.Id, StringComparer.Ordinal);
        var references = new List<object>();
        void Check(SlotInfo slot, string ownerId, string ownerType, IReadOnlyDictionary<string, MemberValue>? members)
        {
            var declaredFields = managed.TryGetValue(ownerId, out var owner) ? owner.Spec.Fields : null;
            foreach (var (name, member) in members ?? new Dictionary<string, MemberValue>())
            {
                JsonElement? declared = declaredFields?.TryGetValue(name, out var field) == true ? field : null;
                foreach (var (target, _) in References(member, declared).Where(reference => !reference.Repointed && targets.ContainsKey(reference.Target)))
                    references.Add(new { componentKey = targets[target].StableKey, targetId = target, referencedBy = ownerId,
                        type = ownerType, member = name, slotId = slot.Id, slotPath = slot.Path });
            }
        }
        foreach (var slot in prepared.SnapshotSlots)
        {
            Check(slot, slot.Id, "[FrooxEngine]FrooxEngine.Slot", slot.Members);
            foreach (var component in slot.Components.Where(component => !removed.Contains(component.Id)))
                Check(slot, component.Id, component.Type, component.Members);
        }
        if (references.Count > 0)
            throw new RLoopException("APPLY_LIST_SHRINK_REFERENCED",
                "A declared list member must shrink, which requires recreating its Component, but something other than a declared managed reference points at the Component or its members. No mutations were performed.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?>
                {
                    ["stateFile"] = prepared.StatePath,
                    ["recreates"] = recreates.Select(component => new
                        { key = component.StableKey, id = component.Superseded!.Id, reason = RecreatePlanReason(component) }).ToArray(),
                    ["references"] = references
                },
                ["Declare the referencing member as a managed field with a $component/$member selector so apply can re-point it, or remove the reference.",
                 "Only the snapshots this apply read were checked. Inspect other references to the Component before applying."]);
    }

    private static void ValidateSlotOwnership(PreparedApply prepared)
    {
        var claims = prepared.Nodes.Where(node => node.Existing is not null)
            .Select(node => (Id: node.Existing!.Id, Key: node.StableKey, Kind: "live"))
            .Concat(prepared.Deletions.Where(deletion => deletion.Kind == "slot")
                .Select(deletion => (deletion.Id, deletion.Key, Kind: "delete")));
        var conflicts = claims.GroupBy(claim => claim.Id, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => new { id = group.Key, claims = group.Select(claim => new { claim.Key, claim.Kind }).ToArray() }).ToArray();
        if (conflicts.Length > 0)
            throw new RLoopException("APPLY_SLOT_OWNERSHIP_CONFLICT", "Multiple managed keys or deletion operations claim the same Slot. No mutations were performed.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["conflicts"] = conflicts });
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveFieldsAsync(
        IReadOnlyDictionary<string, JsonElement>? fields,
        IReadOnlyDictionary<string, ComponentRuntime> components,
        IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields ?? new Dictionary<string, JsonElement>())
            result[field.Key] = await ResolveValueAsync(field.Value, components, slots, assets, cancellationToken);
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> MergeCreateFields(ApplyComponentSpec component)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in component.InitialFields ?? new Dictionary<string, JsonElement>()) result[field.Key] = field.Value;
        foreach (var field in component.Fields ?? new Dictionary<string, JsonElement>()) result[field.Key] = field.Value;
        return result;
    }

    private static bool ContainsWorldReference(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { } text &&
            (text.StartsWith("$ref:", StringComparison.Ordinal) || text.StartsWith("$component:", StringComparison.Ordinal) ||
             text.StartsWith("$member:", StringComparison.Ordinal) || text.StartsWith("$slot:", StringComparison.Ordinal) ||
             text.StartsWith("$slot-member:", StringComparison.Ordinal)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsWorldReference),
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsWorldReference(property.Value)),
        _ => false
    };

    // With a key, only references to that asset count.
    private static bool ContainsAssetReference(JsonElement value, string? key = null) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { } text && (key is null
            ? text.StartsWith("$asset:", StringComparison.Ordinal) : text == "$asset:" + key),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsAssetReference(item, key)),
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsAssetReference(property.Value, key)),
        _ => false
    };

    private async Task<string> ResolveValueAsync(JsonElement element,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken)
    {
        if (element.ValueKind == JsonValueKind.String)
            return await ResolveSymbolAsync(element.GetString() ?? string.Empty, components, slots, assets, cancellationToken);
        if (element.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            var node = await ResolveCompositeAsync(element, components, slots, assets, cancellationToken);
            return node?.ToJsonString() ?? "null";
        }
        return element.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }

    private async Task<JsonNode?> ResolveCompositeAsync(JsonElement element,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets, CancellationToken cancellationToken)
    {
        if (element.ValueKind == JsonValueKind.String)
            return JsonValue.Create(await ResolveSymbolAsync(element.GetString() ?? string.Empty, components, slots, assets, cancellationToken));
        if (element.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var child in element.EnumerateArray()) array.Add(await ResolveCompositeAsync(child, components, slots, assets, cancellationToken));
            return array;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var obj = new JsonObject();
            foreach (var property in element.EnumerateObject()) obj[property.Name] = await ResolveCompositeAsync(property.Value, components, slots, assets, cancellationToken);
            return obj;
        }
        return JsonNode.Parse(element.GetRawText());
    }

    private async Task<string> ResolveSymbolAsync(string value,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken)
    {
        if (StableSelectorSyntax.TryParse(value, out var stable))
        {
            if (stable!.Kind == "slot-member")
            {
                if (!slots.TryGetValue(stable.Key, out var target) || target.Id is null)
                    throw new RLoopException("APPLY_REFERENCE_NOT_FOUND", $"Slot reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
                var observed = await client.GetSlotAsync(target.Id, 0, false, cancellationToken);
                return RequireSlotMember(observed, stable.MemberName!, value).Id!;
            }
            if (stable!.Kind == "component")
                return components.TryGetValue(stable.Key, out var component) && component.Id is not null
                    ? component.Id : throw UnknownApplyReference(value, components.Keys);
            if (stable.Kind == "slot")
                return slots.TryGetValue(stable.Key, out var slot) && slot.Id is not null
                    ? slot.Id : throw new RLoopException("APPLY_REFERENCE_NOT_FOUND", $"Slot reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
            if (!components.TryGetValue(stable.Key, out var memberComponent) || memberComponent.Id is null)
                throw UnknownApplyReference(value, components.Keys);
            var memberName = stable.MemberName!;
            if (memberComponent.MemberIds.TryGetValue(memberName, out var cached)) return cached;
            if (memberComponent.Existing?.Members is not null && memberComponent.Existing.Members.TryGetValue(memberName, out var summaryMember) &&
                !string.IsNullOrWhiteSpace(summaryMember.Id))
            {
                memberComponent.MemberIds[memberName] = summaryMember.Id;
                return summaryMember.Id;
            }
            var inspected = await client.GetComponentAsync(memberComponent.Id, cancellationToken);
            foreach (var member in inspected.Members.Where(x => !string.IsNullOrWhiteSpace(x.Value.Id)))
                memberComponent.MemberIds[member.Key] = member.Value.Id!;
            if (memberComponent.MemberIds.TryGetValue(memberName, out var id)) return id;
            throw new RLoopException("APPLY_MEMBER_REFERENCE_NOT_FOUND", $"Member reference '{value}' was not found.", ExitCodes.ValidationFailed);
        }
        if (value.StartsWith("$asset:", StringComparison.Ordinal))
        {
            var key = value[7..];
            return assets.TryGetValue(key, out var url) ? url : throw new RLoopException(
                "APPLY_REFERENCE_NOT_FOUND", $"Asset reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
        }
        return value;
    }

    private static MemberValue RequireSlotMember(SlotInfo slot, string name, string selector)
    {
        if (slot.Members?.TryGetValue(name, out var member) == true && !string.IsNullOrWhiteSpace(member.Id)) return member;
        throw new RLoopException("APPLY_MEMBER_REFERENCE_NOT_FOUND", $"Slot member '{selector}' has no observed field ID.", ExitCodes.ValidationFailed);
    }

    private static bool CanResolveAll(IReadOnlyDictionary<string, JsonElement>? fields,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots) =>
        (fields ?? new Dictionary<string, JsonElement>()).Values.All(value => CanResolve(value, components, slots));

    private static bool CanResolve(JsonElement value, IReadOnlyDictionary<string, ComponentRuntime> components,
        IReadOnlyDictionary<string, NodeRuntime> slots)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (StableSelectorSyntax.TryParse(text, out var stable))
                return stable!.Kind is "slot" or "slot-member"
                    ? slots.TryGetValue(stable.Key, out var slot) && slot.Id is not null
                    : components.TryGetValue(stable.Key, out var runtime) && runtime.Id is not null;
            return true;
        }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(x => CanResolve(x, components, slots));
        if (value.ValueKind == JsonValueKind.Object) return value.EnumerateObject().All(x => CanResolve(x.Value, components, slots));
        return true;
    }

    // Whether the value references one of these Component keys other than the Component's own key.
    private static bool ReferencesComponentKeys(JsonElement value, IReadOnlySet<string> keys, string? self) => value.ValueKind switch
    {
        JsonValueKind.String => StableSelectorSyntax.TryParse(value.GetString() ?? string.Empty, out var stable) &&
            stable!.Kind is "component" or "member" && stable.Key != self && keys.Contains(stable.Key),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ReferencesComponentKeys(item, keys, self)),
        JsonValueKind.Object => value.EnumerateObject().Any(property => ReferencesComponentKeys(property.Value, keys, self)),
        _ => false
    };

    private static bool TryResolveRawForPlan(JsonElement value,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets, out string raw)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (StableSelectorSyntax.TryParse(text, out var stable))
            {
                if (stable!.Kind == "component")
                {
                    if (components.TryGetValue(stable.Key, out var target) && target.Existing is not null) { raw = target.Existing.Id; return true; }
                    raw = string.Empty; return false;
                }
                if (stable.Kind == "slot")
                {
                    if (slots.TryGetValue(stable.Key, out var target) && target.Existing is not null) { raw = target.Existing.Id; return true; }
                    raw = string.Empty; return false;
                }
                if (stable.Kind == "slot-member")
                {
                    if (slots.TryGetValue(stable.Key, out var target) &&
                        target.Existing?.Members?.TryGetValue(stable.MemberName!, out var slotMember) == true &&
                        !string.IsNullOrWhiteSpace(slotMember.Id)) { raw = slotMember.Id; return true; }
                    raw = string.Empty; return false;
                }
                if (components.TryGetValue(stable.Key, out var memberTarget) &&
                    memberTarget.Existing?.Members is not null && memberTarget.Existing.Members.TryGetValue(stable.MemberName!, out var member) &&
                    !string.IsNullOrWhiteSpace(member.Id)) { raw = member.Id; return true; }
                raw = string.Empty; return false;
            }
            if (text.StartsWith("$asset:", StringComparison.Ordinal))
            {
                if (assets.TryGetValue(text[7..], out var url)) { raw = url; return true; }
                raw = string.Empty; return false;
            }
            raw = text; return true;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var item in value.EnumerateArray())
            {
                if (!TryResolveRawForPlan(item, components, slots, assets, out var itemRaw)) { raw = string.Empty; return false; }
                if (item.ValueKind == JsonValueKind.String) array.Add(itemRaw);
                else array.Add(JsonNode.Parse(itemRaw));
            }
            raw = array.ToJsonString(); return true;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var obj = new JsonObject();
            foreach (var property in value.EnumerateObject())
            {
                if (!TryResolveRawForPlan(property.Value, components, slots, assets, out var propertyRaw)) { raw = string.Empty; return false; }
                obj[property.Name] = property.Value.ValueKind == JsonValueKind.String ? JsonValue.Create(propertyRaw) : JsonNode.Parse(propertyRaw);
            }
            raw = obj.ToJsonString(); return true;
        }
        raw = value.ValueKind switch
        {
            JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "null", _ => value.GetRawText()
        };
        return true;
    }

    private static bool MemberMatchesRaw(MemberValue member, string raw)
    {
        if (member.Kind == "reference") return string.Equals(member.TargetId ?? "null", raw, StringComparison.Ordinal);
        if (member.Kind is "syncObject" or "dictionary")
        {
            JsonNode? desired;
            try { desired = JsonNode.Parse(raw); } catch (JsonException) { return false; }
            if (desired is not JsonObject obj || member.Members is null) return false;
            if (member.Kind == "dictionary" && obj.Count != member.Members.Count) return false;
            return obj.All(pair => member.Members.TryGetValue(pair.Key, out var child) &&
                MemberMatchesRaw(child, pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text : pair.Value?.ToJsonString() ?? "null"));
        }
        if (member.Kind == "list")
        {
            JsonNode? desired;
            try { desired = JsonNode.Parse(raw); } catch (JsonException) { return false; }
            if (desired is not JsonArray desiredArray || member.Elements is null || desiredArray.Count != member.Elements.Count) return false;
            for (var i = 0; i < desiredArray.Count; i++)
            {
                var desiredValue = desiredArray[i];
                if (!MemberMatchesRaw(member.Elements[i], desiredValue is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text : desiredValue?.ToJsonString() ?? "null")) return false;
            }
            return true;
        }
        if (member.Kind != "field") return false;
        var currentNode = NormalizeNode(member.Value);
        JsonNode? desiredNode;
        if (IsStringLike(member.Type)) desiredNode = JsonValue.Create(raw);
        else desiredNode = MemberValueSyntax.NormalizeTupleOrJson(member.Type, raw, currentNode is JsonArray array ? array.Count : null);
        desiredNode = NormalizeNode(desiredNode);
        return JsonEquivalent(currentNode, desiredNode);
    }

    private static JsonNode? NormalizeNode(JsonNode? node)
    {
        if (node is not JsonObject obj) return node?.DeepClone();
        var orderedNames = obj.ContainsKey("x") ? new[] { "x", "y", "z", "w" } :
            obj.ContainsKey("r") ? new[] { "r", "g", "b", "a" } : [];
        if (orderedNames.Length == 0) return node.DeepClone();
        var result = new JsonArray();
        foreach (var name in orderedNames)
            if (obj.TryGetPropertyValue(name, out var value)) result.Add(value?.DeepClone());
        return result;
    }

    private static bool JsonEquivalent(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is JsonArray la && right is JsonArray ra)
            return la.Count == ra.Count && Enumerable.Range(0, la.Count).All(i => JsonEquivalent(la[i], ra[i]));
        if (left is JsonValue lv && right is JsonValue rv)
        {
            if (TryNumeric(lv, out var ld) && TryNumeric(rv, out var rd))
                return double.IsNaN(ld) && double.IsNaN(rd) || Math.Abs(ld - rd) <= 0.00001 * Math.Max(1, Math.Max(Math.Abs(ld), Math.Abs(rd)));
            return left.ToJsonString() == right.ToJsonString();
        }
        return JsonNode.DeepEquals(left, right);
    }

    private static bool TryNumeric(JsonValue value, out double number)
    {
        number = 0;
        return value.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static bool IsStringLike(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var bare = NormalizeType(type).Split('.').Last();
        return bare.Equals("string", StringComparison.OrdinalIgnoreCase) || bare.Equals("Uri", StringComparison.OrdinalIgnoreCase) ||
               bare.Equals("Type", StringComparison.OrdinalIgnoreCase) || bare.Contains("Enum", StringComparison.OrdinalIgnoreCase);
    }

    private static SlotInfo? MatchSlot(SlotInfo parent, string desiredName, ApplyStateSlot? state,
        bool sameSession, string desiredPath)
    {
        SlotInfo[] candidates = [];
        if (state is not null && sameSession)
            candidates = parent.Children.Where(x => x.Id == state.Id).ToArray();
        if (candidates.Length == 0 && state is not null && !state.RuntimeRelocatable)
        {
            var oldName = (state.PathSegments ?? SlotPaths.LegacySegments(state.Path)).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(oldName)) candidates = parent.Children.Where(x => x.Name == oldName).ToArray();
        }
        if (candidates.Length == 0 && state?.RuntimeRelocatable != true)
            candidates = parent.Children.Where(x => x.Name == desiredName).ToArray();
        if (candidates.Length > 1)
            throw new RLoopException("APPLY_TARGET_AMBIGUOUS", $"Multiple Slots match managed target '{desiredPath}'.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["ids"] = candidates.Select(x => x.Id).ToArray() });
        return candidates.SingleOrDefault();
    }

    private static SlotInfo? FindManagedSlot(PreparedApply prepared, ApplyStateSlot? state)
    {
        if (state is null) return null;
        if ((prepared.SameSession || state.RuntimeRelocatable) && !string.IsNullOrWhiteSpace(state.Id))
        {
            var byId = prepared.SnapshotSlots.Where(slot => slot.Id == state.Id).ToArray();
            if (byId.Length == 1) return byId[0];
        }
        var normalizedPath = NormalizePath(state.Path);
        var byPath = prepared.SnapshotSlots.Where(slot => state.PathSegments is not null
            ? prepared.SnapshotSegments[slot.Id].SequenceEqual(state.PathSegments, StringComparer.Ordinal)
            : NormalizePath(slot.Path ?? string.Empty) == normalizedPath).ToArray();
        if (byPath.Length > 1)
            throw new RLoopException("APPLY_TARGET_AMBIGUOUS", $"Multiple Slots match managed state path '{state.Path}'.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["ids"] = byPath.Select(slot => slot.Id).ToArray() });
        if (byPath.Length == 1) return byPath[0];
        return null;
    }

    private static SlotInfo? FindStateSlot(PreparedApply prepared, string slotKey) =>
        prepared.State.Slots.TryGetValue(slotKey, out var stateSlot) ? FindManagedSlot(prepared, stateSlot) : null;

    private static ComponentSummary? MatchComponent(IReadOnlyList<ComponentSummary> components, string type, int ordinal,
        ApplyStateComponent? state, bool sameSession, IReadOnlyDictionary<string, string>? referenceTargets = null)
    {
        if (state is not null && sameSession)
        {
            var byId = components.SingleOrDefault(x => x.Id == state.Id);
            if (byId is not null) return byId;
        }
        var matches = StableComponentCandidates(components, state?.Type ?? type, state?.ComponentIndex,
            state?.MemberNames, state?.IdentityValues, referenceTargets);
        if (matches.Length > 1 && state is not null && (state.MemberNames is not null || state.IdentityValues is not null))
            throw new RLoopException("STABLE_COMPONENT_AMBIGUOUS",
                $"Stable Component on Slot '{state.SlotKey}' matches multiple runtime Components.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["candidateIds"] = matches.Select(candidate => candidate.Id).ToArray(),
                    ["type"] = state.Type },
                ["Inspect candidateIds and preserve the existing state. Adding identityFields to a manifest does not populate an older checkpoint's identity values.",
                 "Prefer named provider Slots for new content. For existing content, verify ownership and each candidate before an explicit recovery; never guess by ordinal or automatically adopt."]);
        if (matches.Length == 1) return matches[0];
        if (state is not null && (state.MemberNames is not null || state.IdentityValues is not null)) return null;
        matches = components.Where(x => TypeNamesEquivalent(x.Type, state?.Type ?? type)).ToArray();
        var requestedOrdinal = state?.TypeOrdinal ?? ordinal;
        return requestedOrdinal >= 0 && requestedOrdinal < matches.Length ? matches[requestedOrdinal] : null;
    }

    private static IReadOnlyDictionary<string, string>? ResolveStateTopologyTargets(PreparedApply prepared,
        ApplyStateComponent state, HashSet<string> resolving)
    {
        if (state.ReferenceSelectors is not { Count: > 0 } || !resolving.Add(state.SlotKey + "\n" + state.Id)) return null;
        try
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var reference in state.ReferenceSelectors)
            {
                var target = ResolveStateReferenceId(prepared, reference.Value, resolving);
                if (target is not null) result[reference.Key] = target;
            }
            return result.Count == 0 ? null : result;
        }
        finally
        {
            resolving.Remove(state.SlotKey + "\n" + state.Id);
        }
    }

    private static string? ResolveStateReferenceId(PreparedApply prepared, string selector, HashSet<string> resolving)
    {
        if (!StableSelectorSyntax.TryParse(selector, out var syntax)) return null;
        if (syntax!.Kind == "slot")
            return prepared.State.Slots.TryGetValue(syntax.Key, out var slotState)
                ? FindManagedSlot(prepared, slotState)?.Id : null;
        if (syntax.Kind == "slot-member")
            return FindStateSlot(prepared, syntax.Key)?.Members?.GetValueOrDefault(syntax.MemberName!)?.Id;
        if (!prepared.State.Components.TryGetValue(syntax.Key, out var componentState)) return null;
        var slot = FindStateSlot(prepared, componentState.SlotKey);
        if (slot is null) return null;
        var referenceTargets = ResolveStateTopologyTargets(prepared, componentState, resolving);
        var matches = StableComponentCandidates(slot.Components, componentState.Type, componentState.ComponentIndex,
            componentState.MemberNames, componentState.IdentityValues, referenceTargets);
        if (matches.Length != 1) return null;
        if (syntax.Kind == "component") return matches[0].Id;
        return matches[0].Members?.FirstOrDefault(member =>
            member.Key.Equals(syntax.MemberName, StringComparison.OrdinalIgnoreCase)).Value?.Id;
    }

    private static ComponentSummary[] StableComponentCandidates(IReadOnlyList<ComponentSummary> components, string type,
        int? componentIndex, IReadOnlyList<string>? memberNames, IReadOnlyDictionary<string, string>? identityValues,
        IReadOnlyDictionary<string, string>? referenceTargets = null)
    {
        var candidates = components.Where(component => TypeNamesEquivalent(component.Type, type)).ToArray();
        if (memberNames is not null)
            candidates = candidates.Where(component => memberNames.All(name => component.Members?.ContainsKey(name) == true)).ToArray();
        if (identityValues is not null)
            candidates = candidates.Where(component => identityValues.All(identity =>
                component.Members?.TryGetValue(identity.Key, out var value) == true && MemberMatchesRaw(value, identity.Value))).ToArray();
        if (referenceTargets is not null)
            candidates = candidates.Where(component => referenceTargets.All(reference =>
                component.Members?.TryGetValue(reference.Key, out var value) == true &&
                value.Kind == "reference" && value.TargetId == reference.Value)).ToArray();
        if (candidates.Length <= 1) return candidates;
        if ((identityValues is null || identityValues.Count == 0) && componentIndex is >= 0 && componentIndex < components.Count)
        {
            var indexed = components[componentIndex.Value];
            if (candidates.Any(candidate => candidate.Id == indexed.Id)) return [indexed];
        }
        return candidates;
    }

    private static ApplyStateComponent CreateComponentState(ComponentRuntime component, string id,
        IReadOnlyDictionary<string, string>? resolvedFields = null, ApplyStateComponent? previous = null)
    {
        var memberNames = (component.Spec.Fields?.Keys ?? [])
            .Concat(component.Spec.InitialFields?.Keys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Dictionary<string, string>? identityValues = null;
        if (component.Spec.IdentityFields is { Count: > 0 })
        {
            identityValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in component.Spec.IdentityFields)
            {
                if (TryGetReferenceSelector(component.Spec, name, out _)) continue;
                if (resolvedFields?.TryGetValue(name, out var resolved) == true) identityValues[name] = resolved;
                else if (component.AppliedOnCreate?.TryGetValue(name, out var initial) == true) identityValues[name] = initial;
                else if (component.Existing?.Members?.TryGetValue(name, out var current) == true) identityValues[name] = MemberRaw(current);
            }
        }
        // Until its fields are written, a Component this apply creates has only the references its create wrote. Matching it by
        // one it does not have yet would miss it after a lost create response, and the re-run would create another.
        var written = component.Existing is null && resolvedFields is null ? component.AppliedOnCreate ?? new Dictionary<string, string>() : null;
        var referenceSelectors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in memberNames)
            if ((written is null || written.ContainsKey(name)) && TryGetReferenceSelector(component.Spec, name, out var selector))
                referenceSelectors[name] = selector;
        // Record asset references only once the fields are written. Earlier checkpoints keep the last applied record,
        // and an empty record still tells a current state apart from one written before this was recorded.
        var assetFields = resolvedFields is null ? previous?.AssetFields :
            (component.Spec.Fields ?? new Dictionary<string, JsonElement>()).Where(field => ContainsAssetReference(field.Value))
                .ToDictionary(field => field.Key, field => field.Value.Clone(), StringComparer.Ordinal);
        return new ApplyStateComponent(id, component.Node.StableKey, component.ResolvedType ?? component.Spec.Type,
            component.TypeOrdinal, component.ComponentIndex, memberNames, identityValues,
            referenceSelectors.Count == 0 ? null : referenceSelectors, assetFields, component.SupersededId);
    }

    private static bool TryGetReferenceSelector(ApplyComponentSpec component, string memberName, out string selector)
    {
        selector = string.Empty;
        JsonElement value;
        if (component.Fields?.TryGetValue(memberName, out value) != true &&
            component.InitialFields?.TryGetValue(memberName, out value) != true) return false;
        if (value.ValueKind != JsonValueKind.String) return false;
        var raw = value.GetString() ?? string.Empty;
        if (!StableSelectorSyntax.TryParse(raw, out var parsed) || parsed!.Kind == "member" && parsed.MemberName is null) return false;
        selector = raw;
        return true;
    }

    private static bool SlotNeedsUpdate(SlotInfo existing, ApplySlotSpec desired) =>
        existing.Name != desired.Name ||
        ManagesTransform(desired, "position") && desired.Position is not null && !VectorEquals(existing.Position, desired.Position) ||
        ManagesTransform(desired, "rotation") && desired.Rotation is not null && !QuaternionEquals(existing.Rotation, desired.Rotation) ||
        ManagesTransform(desired, "scale") && desired.Scale is not null && !VectorEquals(existing.Scale, desired.Scale);

    private async Task PrepareRelocationTransformsAsync(PreparedApply prepared, string parentPath,
        CancellationToken cancellationToken)
    {
        if (!prepared.Nodes.Any(node => node.SlotAction == "relocate" && node.Spec.RelocationTransform == "world")) return;
        var externalParentWorld = await WorldTransformAtPathAsync(SlotPaths.Selector(parentPath, prepared.ParentSegments), cancellationToken);
        var finalWorld = new Dictionary<NodeRuntime, Matrix4x4>();
        foreach (var node in prepared.Nodes)
        {
            var parentWorld = node.Parent is null ? externalParentWorld : finalWorld[node.Parent];
            Matrix4x4 local;
            if (node.SlotAction == "relocate" && node.Spec.RelocationTransform == "world")
            {
                if (!prepared.State.Slots.TryGetValue(node.StableKey, out var previous))
                    throw new RLoopException("APPLY_RELOCATION_STATE_MISSING",
                        $"World-transform relocation for '{node.StableKey}' requires its previous stable path.",
                        ExitCodes.ValidationFailed);
                var oldWorld = await WorldTransformAtPathAsync(SlotPaths.Selector(previous.Path, previous.PathSegments), cancellationToken);
                if (!Matrix4x4.Invert(parentWorld, out var inverseParent))
                    throw new RLoopException("APPLY_RELOCATION_PARENT_NONINVERTIBLE",
                        $"Cannot preserve world transform for '{node.StableKey}' because the new parent transform is non-invertible.",
                        ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["parentPath"] = parentPath });
                local = oldWorld * inverseParent;
                if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var position))
                    throw new RLoopException("APPLY_RELOCATION_TRANSFORM_DECOMPOSE_FAILED",
                        $"Cannot decompose the preserved local transform for '{node.StableKey}'.", ExitCodes.ValidationFailed);
                rotation = Quaternion.Normalize(rotation);
                node.RelocationPosition = new Vector3Value(position.X, position.Y, position.Z);
                node.RelocationRotation = new QuaternionValue(rotation.X, rotation.Y, rotation.Z, rotation.W);
                node.RelocationScale = new Vector3Value(scale.X, scale.Y, scale.Z);
            }
            else
            {
                local = EffectiveLocalTransform(node);
            }
            finalWorld[node] = local * parentWorld;
        }
    }

    private async Task<Matrix4x4> WorldTransformAtPathAsync(string path, CancellationToken cancellationToken)
    {
        var parts = SlotPaths.ParseSelector(path).Skip(1).ToArray();
        var currentId = "Root";
        var currentPath = "Root";
        var world = Matrix4x4.Identity;
        foreach (var part in parts)
        {
            var current = await client.GetSlotAsync(currentId, 1, false, cancellationToken);
            var matches = current.Children.Where(child => child.Name.Equals(part, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new RLoopException(matches.Length == 0 ? "SLOT_PATH_NOT_FOUND" : "SLOT_PATH_AMBIGUOUS",
                    $"Cannot resolve transform path segment '{part}' below '{currentPath}'.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["path"] = path, ["candidateIds"] = matches.Select(match => match.Id).ToArray() });
            var child = matches[0];
            world = LocalTransform(child.Position, child.Rotation, child.Scale) * world;
            currentId = child.Id;
            currentPath += "/" + part;
        }
        return world;
    }

    private static Matrix4x4 EffectiveLocalTransform(NodeRuntime node)
    {
        var position = node.Existing?.Position ?? new Vector3Value(0, 0, 0);
        var rotation = node.Existing?.Rotation ?? new QuaternionValue(0, 0, 0, 1);
        var scale = node.Existing?.Scale ?? new Vector3Value(1, 1, 1);
        if (node.Existing is null || ManagesTransform(node.Spec, "position") && node.Spec.Position is not null)
            position = node.Spec.Position?.ToVector3("position") ?? position;
        if (node.Existing is null || ManagesTransform(node.Spec, "rotation") && node.Spec.Rotation is not null)
            rotation = node.Spec.Rotation?.ToQuaternion("rotation") ?? rotation;
        if (node.Existing is null || ManagesTransform(node.Spec, "scale") && node.Spec.Scale is not null)
            scale = node.Spec.Scale?.ToVector3("scale") ?? scale;
        return LocalTransform(position, rotation, scale);
    }

    private static Matrix4x4 LocalTransform(Vector3Value? position, QuaternionValue? rotation, Vector3Value? scale)
    {
        position ??= new Vector3Value(0, 0, 0);
        rotation ??= new QuaternionValue(0, 0, 0, 1);
        scale ??= new Vector3Value(1, 1, 1);
        return Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z) *
               Matrix4x4.CreateFromQuaternion(new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W)) *
               Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
    }

    private static SlotUpdateRequest CreateSlotUpdate(NodeRuntime node, string rootParentId)
    {
        var existing = node.Existing!;
        return new SlotUpdateRequest(existing.Id,
            existing.Name == node.Spec.Name ? null : node.Spec.Name,
            node.RelocationPosition ?? (ManagesTransform(node.Spec, "position") && node.Spec.Position is not null && !VectorEquals(existing.Position, node.Spec.Position) ? node.Spec.Position.ToVector3("position") : null),
            node.RelocationRotation ?? (ManagesTransform(node.Spec, "rotation") && node.Spec.Rotation is not null && !QuaternionEquals(existing.Rotation, node.Spec.Rotation) ? node.Spec.Rotation.ToQuaternion("rotation") : null),
            node.RelocationScale ?? (ManagesTransform(node.Spec, "scale") && node.Spec.Scale is not null && !VectorEquals(existing.Scale, node.Spec.Scale) ? node.Spec.Scale.ToVector3("scale") : null),
            node.SlotAction == "relocate" ? node.Parent?.Id ?? rootParentId : null);
    }

    private static bool ManagesTransform(ApplySlotSpec slot, string field) =>
        !slot.PreserveWorldTransform && slot.RelocationTransform != "world" &&
        (slot.ManagedFields is null || slot.ManagedFields.Contains(field, StringComparer.Ordinal));

    private static StateMigrations ApplyStateMigrations(ApplyDocument document, ApplyState state)
    {
        var slotMigrations = new Dictionary<string, string>(StringComparer.Ordinal);
        var componentMigrations = new Dictionary<string, string>(StringComparer.Ordinal);

        void MigrateSlot(ApplySlotSpec slot)
        {
            if (!string.IsNullOrWhiteSpace(slot.Key) && !string.IsNullOrWhiteSpace(slot.MigrateFrom))
            {
                if (state.Slots.ContainsKey(slot.Key) && state.Slots.ContainsKey(slot.MigrateFrom))
                    throw new RLoopException("APPLY_STATE_MIGRATION_CONFLICT",
                        $"State contains both Slot keys '{slot.MigrateFrom}' and '{slot.Key}'.",
                        ExitCodes.ValidationFailed);
                if (!state.Slots.ContainsKey(slot.Key) && state.Slots.Remove(slot.MigrateFrom, out var migrated))
                {
                    state.Slots[slot.Key] = migrated;
                    foreach (var component in state.Components.Where(component => component.Value.SlotKey == slot.MigrateFrom).ToArray())
                        state.Components[component.Key] = component.Value with { SlotKey = slot.Key };
                    slotMigrations[slot.Key] = slot.MigrateFrom;
                }
            }
        }

        void Visit(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children)
        {
            MigrateSlot(slot);
            foreach (var component in components ?? [])
            {
                if (string.IsNullOrWhiteSpace(component.Key) || string.IsNullOrWhiteSpace(component.MigrateFrom)) continue;
                if (state.Components.ContainsKey(component.Key) && state.Components.ContainsKey(component.MigrateFrom))
                    throw new RLoopException("APPLY_STATE_MIGRATION_CONFLICT",
                        $"State contains both Component keys '{component.MigrateFrom}' and '{component.Key}'.",
                        ExitCodes.ValidationFailed);
                if (!state.Components.ContainsKey(component.Key) && state.Components.Remove(component.MigrateFrom, out var migrated))
                {
                    state.Components[component.Key] = migrated;
                    componentMigrations[component.Key] = component.MigrateFrom;
                }
            }
            foreach (var child in children ?? []) Visit(child.Slot, child.Components, child.Children);
        }

        Visit(document.Slot!, document.Components, document.Children);
        return new StateMigrations(slotMigrations, componentMigrations);
    }

    private static bool VectorEquals(Vector3Value? current, float[] desired) => current is not null &&
        NearlyEqual(current.X, desired[0]) && NearlyEqual(current.Y, desired[1]) && NearlyEqual(current.Z, desired[2]);
    private static bool QuaternionEquals(QuaternionValue? current, float[] desired) => current is not null &&
        NearlyEqual(current.X, desired[0]) && NearlyEqual(current.Y, desired[1]) && NearlyEqual(current.Z, desired[2]) && NearlyEqual(current.W, desired[3]);
    private static bool NearlyEqual(float left, float right) => Math.Abs(left - right) <= 0.00001f * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    // Write each replacement's declared fields and read it back before any managed reference moves to it. Runtime
    // logic that refills a list would make every apply recreate the Component again, so a list still longer than declared
    // undoes the replacements that nothing can reference yet and stops.
    private async Task VerifyReplacementsAsync(PreparedApply prepared, IReadOnlyList<ComponentRuntime> recreates,
        Dictionary<string, ComponentRuntime> byKey, IReadOnlyDictionary<string, NodeRuntime> slotsByKey,
        IReadOnlyDictionary<string, string> assetUrls, ApplyCounts counts, CancellationToken cancellationToken)
    {
        var failures = new List<(ComponentRuntime Component, string Member, int Declared, int Observed)>();
        // A recreate whose replaced Component is already gone has nothing left to verify or undo.
        foreach (var component in recreates.Where(component => component.AwaitsVerification))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No replacement, this one included, is a reference target until every replacement passes, so the fields that
            // reference one are left to the fields phase.
            var resolvable = MergeCreateFields(component.Spec).Where(field => CanResolve(field.Value, byKey, slotsByKey))
                .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
            var fields = await ResolveFieldsAsync(resolvable, byKey, slotsByKey, assetUrls, cancellationToken);
            var unwritten = fields.Where(field => component.AppliedOnCreate is null ||
                                                  !component.AppliedOnCreate.TryGetValue(field.Key, out var applied) || applied != field.Value)
                .Where(field => component.Existing?.Members is null ||
                                !component.Existing.Members.TryGetValue(field.Key, out var current) ||
                                !MemberMatchesRaw(current, field.Value)).ToDictionary(StringComparer.Ordinal);
            if (unwritten.Count > 0)
            {
                ForgetComponentAssetEvidence(prepared, component.StableKey, unwritten.Keys);
                Checkpoint(prepared);
                await client.SetComponentMembersAsync(component.Id!, component.ResolvedType ?? component.Spec.Type, unwritten, cancellationToken);
            }
            // The fields phase must not write these again.
            component.AppliedOnCreate = fields;
            var replacement = await client.GetComponentAsync(component.Id!, cancellationToken);
            foreach (var field in component.Spec.Fields ?? new Dictionary<string, JsonElement>())
                if (field.Value.ValueKind == JsonValueKind.Array &&
                    replacement.Members.GetValueOrDefault(field.Key) is { Kind: "list" } list &&
                    (list.Elements?.Count ?? 0) > field.Value.GetArrayLength())
                    failures.Add((component, field.Key, field.Value.GetArrayLength(), list.Elements!.Count));
        }
        if (failures.Count == 0)
        {
            // Every replacement passed, so the fields phase may point references at them.
            foreach (var component in recreates.Where(component => component.AwaitsVerification && !string.IsNullOrWhiteSpace(component.Spec.Key)))
                byKey[component.Spec.Key!] = component;
            return;
        }

        // Nothing references a replacement this apply created, not even another replacement, so removing it cannot leave a
        // dangling reference. A resumed replacement that passed stays, because an earlier apply may already have moved
        // references to it.
        var failed = failures.Select(failure => failure.Component).ToHashSet();
        var undone = recreates.Where(component => component.AwaitsVerification &&
                                                  (component.ReplacementCreated || failed.Contains(component))).ToArray();
        foreach (var component in undone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.RemoveComponentAsync(component.Id!, cancellationToken);
            counts.ComponentsDeleted++;
            var restored = component.StateBeforeRecreate ??
                prepared.State.Components[component.StableKey] with { AssetFields = new Dictionary<string, JsonElement>() };
            prepared.State.Components[component.StableKey] = restored with { Id = component.SupersededId!, SupersededId = null };
        }
        foreach (var node in undone.Select(component => component.Node).Distinct())
            await SaveSlotIndexesAsync(prepared, node.StableKey, node.Id!, cancellationToken);
        Checkpoint(prepared);
        var inProgress = recreates.Where(component => component.AwaitsVerification && !undone.Contains(component))
            .Select(component => component.StableKey).ToArray();
        throw new RLoopException("APPLY_LIST_SHRINK_NOT_CONVERGED",
            "The runtime kept a list longer than declared on a replacement Component, so recreating it cannot converge. The undone replacements were removed and their original Components are unchanged.",
            ExitCodes.OperationFailed, new Dictionary<string, object?>
            {
                ["failures"] = failures.Select(failure => new
                {
                    componentKey = failure.Component.StableKey, member = failure.Member,
                    declared = failure.Declared, observed = failure.Observed
                }).ToArray(),
                ["undone"] = undone.Select(component => component.StableKey).ToArray(),
                // Resumed recreates that passed keep their replacements and stay in progress.
                ["inProgress"] = inProgress
            },
            new[] { "Declare the list length that the runtime keeps, or find what refills the list.",
                    "Re-run apply only after changing the declaration; re-running the same command recreates and undoes the Component again. The undone keys have no recreate in progress." }
                .Concat(inProgress.Length == 0 ? Array.Empty<string>() :
                    [$"The recreate of '{string.Join("', '", inProgress)}' stays in progress. Finish it by re-running apply in this session; after a world reload, apply stops with APPLY_RECREATE_INTERRUPTED."])
                .ToArray());
    }

    // Removing a Component moves the ones after it, and after a world reload a key without identity values is bound by
    // its saved index. Read the Slot again and save every key whose saved ID is on it at that Component's position.
    // A key whose ID is not on the Slot keeps its index. The Components declared on the Slot take the same positions in
    // this apply too, because the fields phase saves each one's index again. The caller's next checkpoint persists the result.
    private async Task SaveSlotIndexesAsync(PreparedApply prepared, string slotKey, string slotId,
        CancellationToken cancellationToken)
    {
        var layout = (await client.GetSlotAsync(slotId, 0, false, cancellationToken)).Components
            .Select(component => component.Id).ToList();
        foreach (var (key, saved) in prepared.State.Components.Where(pair => pair.Value.SlotKey == slotKey).ToArray())
        {
            var index = string.IsNullOrEmpty(saved.Id) ? -1 : layout.IndexOf(saved.Id);
            if (index >= 0 && saved.ComponentIndex != index)
                prepared.State.Components[key] = saved with { ComponentIndex = index };
        }
        foreach (var runtime in prepared.Components.Where(runtime => runtime.Node.StableKey == slotKey && !string.IsNullOrEmpty(runtime.Id)))
        {
            var index = layout.IndexOf(runtime.Id!);
            if (index >= 0) runtime.ComponentIndex = index;
        }
    }

    private static void Checkpoint(PreparedApply prepared)
    {
        prepared.State.SessionId = prepared.Session.UniqueSessionId;
        ApplyStateStore.Save(prepared.StatePath, prepared.State);
    }

    private static int MaxDepth(IReadOnlyList<ApplyNodeSpec>? children) => children is null || children.Count == 0
        ? 0 : 1 + children.Max(x => MaxDepth(x.Children));
    private async Task<string> ObserveAbsolutePathAsync(SlotInfo slot, CancellationToken cancellationToken) =>
        string.Join('/', await ObserveAbsoluteSegmentsAsync(slot, cancellationToken));

    private async Task<IReadOnlyList<string>> ObserveAbsoluteSegmentsAsync(SlotInfo slot, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (slot.Id != "Root")
        {
            if (!visited.Add(slot.Id) || visited.Count > 64 || string.IsNullOrWhiteSpace(slot.ParentId))
                throw new RLoopException("SLOT_PATH_UNRESOLVED", "Cannot observe a bounded parent chain to Root.", ExitCodes.ValidationFailed);
            names.Add(slot.Name);
            slot = await client.GetSlotAsync(slot.ParentId, 0, false, cancellationToken);
        }
        names.Reverse();
        return ["Root", .. names];
    }
    private static string NormalizePath(string path) => string.Join('/', SlotPaths.LegacySegments(path));
    private static string MemberKey(string selector) { var separator = selector.LastIndexOf('.'); return separator > 0 ? selector[..separator] : selector; }
    private static string NormalizeType(string value) { var bracket = value.IndexOf(']'); return bracket >= 0 ? value[(bracket + 1)..] : value; }
    private static bool TypeNamesEquivalent(string left, string right) => NormalizeType(left).Equals(NormalizeType(right), StringComparison.Ordinal) ||
        NormalizeType(left).EndsWith('.' + NormalizeType(right), StringComparison.Ordinal) || NormalizeType(right).EndsWith('.' + NormalizeType(left), StringComparison.Ordinal);
    private static RLoopException UnknownApplyReference(string value, IEnumerable<string> keys) => new("APPLY_REFERENCE_NOT_FOUND",
        $"Symbolic reference '{value}' could not be resolved.", ExitCodes.ValidationFailed, suggestions: keys.Take(30).Select(x => $"$ref:{x}").ToArray());

    private static SlotInfo AddPaths(SlotInfo slot, string path)
    {
        var children = slot.Children.Select(c => AddPaths(c, path.TrimEnd('/') + "/" + c.Name)).ToArray();
        return slot with { Path = path, Children = children };
    }
    private static SlotInfo RemoveReferenceOnlyChildren(SlotInfo slot) => slot with
    {
        Children = slot.Children.Where(child => !child.IsReferenceOnly).Select(RemoveReferenceOnlyChildren).ToArray()
    };
    private static void Visit(SlotInfo slot, string path, Action<SlotInfo> visitor)
    {
        var withPath = slot with { Path = path };
        visitor(withPath);
        foreach (var child in slot.Children) Visit(child, path + "/" + child.Name, visitor);
    }

    private static bool IsDirectChild(SlotInfo parent, string id) => parent.Children.Any(child => child.Id == id);

    // The parent snapshot is only deep enough for a root directly under the parent. A root found anywhere else keeps its own
    // snapshot, placed first so that the ID de-duplication keeps its full subtree and its saved path segments.
    private static void AddRootSnapshot(List<(SlotInfo Slot, string Path)> snapshots, SlotInfo parent, SlotInfo root, string path)
    {
        if (!IsDirectChild(parent, root.Id)) snapshots.Insert(0, (root, path));
    }

    private sealed class PreparedApply(ApplyDocument document, ApplyOptions options, ApplyState state,
        string statePath, SessionInfo session, string parentId, bool sameSession, IReadOnlyList<(SlotInfo Slot, string Path)> snapshots,
        IReadOnlyDictionary<string, string> slotMigrations, IReadOnlyDictionary<string, string> componentMigrations, IReadOnlyList<string> parentSegments)
    {
        public ApplyDocument Document { get; } = document;
        public ApplyOptions Options { get; } = options;
        public ApplyState State { get; } = state;
        public string StatePath { get; } = statePath;
        public SessionInfo Session { get; } = session;
        public string ParentId { get; } = parentId;
        public bool SameSession { get; } = sameSession;
        public IReadOnlyList<string> ParentSegments { get; } = parentSegments;
        public IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotSegments { get; } = BuildSegments(snapshots, state, parentId, parentSegments);
        public IReadOnlyDictionary<string, string> SlotMigrations { get; } = slotMigrations;
        public IReadOnlyDictionary<string, string> ComponentMigrations { get; } = componentMigrations;
        public IReadOnlyList<SlotInfo> SnapshotSlots { get; } = snapshots.SelectMany(snapshot => Flatten(snapshot.Slot, snapshot.Path))
            .DistinctBy(slot => slot.Id, StringComparer.Ordinal).ToArray();
        public List<NodeRuntime> Nodes { get; } = [];
        public List<ComponentRuntime> Components { get; } = [];
        public List<ApplyPlanEntry> Entries { get; } = [];
        public List<DeletionRuntime> Deletions { get; } = [];
        public List<AssetRuntime> Assets { get; } = [];

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildSegments(IReadOnlyList<(SlotInfo Slot, string Path)> roots,
            ApplyState state, string parentId, IReadOnlyList<string> parentSegments)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            void VisitSegments(SlotInfo slot, IReadOnlyList<string> names)
            {
                if (!result.TryAdd(slot.Id, names)) names = result[slot.Id];
                foreach (var child in slot.Children) VisitSegments(child, [.. names, child.Name]);
            }
            foreach (var root in roots)
                VisitSegments(root.Slot, root.Slot.Id == parentId ? parentSegments :
                    state.Slots.Values.FirstOrDefault(slot => slot.Id == root.Slot.Id)?.PathSegments ?? SlotPaths.LegacySegments(root.Path));
            return result;
        }

        private static IReadOnlyList<SlotInfo> Flatten(SlotInfo root, string rootPath)
        {
            var result = new List<SlotInfo>();
            Visit(root, rootPath, result.Add);
            return result;
        }
    }

    private sealed class NodeRuntime(ApplySlotSpec spec, IReadOnlyList<ApplyComponentSpec> componentSpecs,
        NodeRuntime? parent, SlotInfo? existing, string stableKey, string path, string slotAction)
    {
        public ApplySlotSpec Spec { get; } = spec;
        public IReadOnlyList<ApplyComponentSpec> ComponentSpecs { get; } = componentSpecs;
        public NodeRuntime? Parent { get; } = parent;
        public SlotInfo? Existing { get; } = existing;
        public string StableKey { get; } = stableKey;
        public string Path { get; } = path;
        public IReadOnlyList<string> PathSegments { get; set; } = [];
        public string SlotAction { get; } = slotAction;
        public string? Id { get; set; }
        public Vector3Value? RelocationPosition { get; set; }
        public QuaternionValue? RelocationRotation { get; set; }
        public Vector3Value? RelocationScale { get; set; }
    }

    private sealed class ComponentRuntime(ApplyComponentSpec spec, NodeRuntime node, ComponentSummary? existing,
        string stableKey, int typeOrdinal, string path, int componentIndex)
    {
        public ApplyComponentSpec Spec { get; } = spec;
        public NodeRuntime Node { get; } = node;
        public ComponentSummary? Existing { get; } = existing;
        public string StableKey { get; } = stableKey;
        public int TypeOrdinal { get; } = typeOrdinal;
        public int ComponentIndex { get; set; } = componentIndex;
        public string Path { get; } = path;
        public string? Id { get; set; }
        public string? ResolvedType { get; set; }
        public IReadOnlyDictionary<string, string>? AppliedOnCreate { get; set; }
        public ComponentSummary? RelocationSource { get; set; }
        public string? RelocationSourceSlotKey { get; set; }
        public string? RelocationSourceSlotId { get; set; }
        // A list-shrink recreate: the Component it replaces while that is still on the Slot, and its ID until it is removed.
        public ComponentSummary? Superseded { get; set; }
        public string? SupersededId { get; set; }
        public string? RecreateReason { get; set; }
        // True when the recreate was already in progress in state, false when this plan starts it.
        public bool Resumed { get; set; }
        // The saved record just before this apply creates the replacement, after the asset phase forgot the evidence of
        // re-imported assets; restored if the replacement does not converge.
        public ApplyStateComponent? StateBeforeRecreate { get; set; }
        // True once this apply has created the replacement.
        public bool ReplacementCreated { get; set; }
        // While the replaced Component is still on the Slot, its replacement is not a reference target until verified.
        public bool AwaitsVerification => SupersededId is not null && Superseded is not null;
        public Dictionary<string, string> MemberIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ApplyCounts
    {
        public int SlotsCreated { get; set; }
        public int SlotsUpdated { get; set; }
        public int SlotsUnchanged { get; set; }
        public int ComponentsAdded { get; set; }
        public int ComponentsUpdated { get; set; }
        public int ComponentsUnchanged { get; set; }
        public int ComponentsDeleted { get; set; }
        public int SlotsDeleted { get; set; }
        public int AssetsImported { get; set; }
        public int AssetsUnchanged { get; set; }
    }

    private sealed record StateMigrations(IReadOnlyDictionary<string, string> Slots,
        IReadOnlyDictionary<string, string> Components);

    private sealed record DeletionRuntime(string Kind, string Key, string Id, string Path, string Reason,
        IReadOnlyList<string>? CoveredSlotKeys = null, IReadOnlyList<string>? CoveredComponentKeys = null,
        string? SlotKey = null, string? SlotId = null);

    private sealed class AssetRuntime(string key, ApplyAssetSpec spec, string resolvedSource,
        string sourceHash, string? directUrl, string action)
    {
        public string Key { get; } = key;
        public ApplyAssetSpec Spec { get; } = spec;
        public string ResolvedSource { get; } = resolvedSource;
        public string SourceHash { get; } = sourceHash;
        public string? DirectUrl { get; } = directUrl;
        public string Action { get; } = action;
        public string? Url { get; set; }
        public string? MigratedUrl { get; set; }
    }
}
