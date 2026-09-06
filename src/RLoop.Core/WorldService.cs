using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed class WorldService(IResoniteClient client, string? generatedContentSource = null)
{
    public async Task<string> ResolveSlotIdAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new RLoopException("SLOT_SELECTOR_MISSING", "A Slot ID or path is required.", ExitCodes.InvalidArguments);
        if (selector.Equals("Root", StringComparison.OrdinalIgnoreCase) || selector is "/" or "/Root") return "Root";

        if (!selector.Contains('/') && !selector.StartsWith("Root", StringComparison.OrdinalIgnoreCase))
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

        var parts = selector.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("Root", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
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
        string? currentConnectionId, HashSet<string> resolvingComponents, CancellationToken cancellationToken)
    {
        var syntax = StableSelectorSyntax.Parse(selector);
        if (syntax.Kind == "slot")
        {
            var stable = StableReferenceResolver.ResolveSlot(stateFile, selector);
            string id;
            if (stable.SessionId == currentConnectionId && !string.IsNullOrWhiteSpace(stable.Id))
            {
                try { id = (await client.GetSlotAsync(stable.Id, 0, false, cancellationToken)).Id; }
                catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
                {
                    try { id = await ResolveSlotIdAsync(stable.Path, cancellationToken); }
                    catch (RLoopException pathError) when (stable.RuntimeRelocatable &&
                        pathError.Code is "SLOT_NOT_FOUND" or "SLOT_PATH_NOT_FOUND")
                    { id = (await ResolveRelocatableSlotAsync(stateFile, stable, cancellationToken)).Id; }
                }
            }
            else
            {
                id = stable.RuntimeRelocatable
                    ? (await ResolveRelocatableSlotAsync(stateFile, stable, cancellationToken)).Id
                    : await ResolveSlotIdAsync(stable.Path, cancellationToken);
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
                try { component = await client.GetComponentAsync(stableComponent.Id, cancellationToken); }
                catch (RLoopException ex) when (ex.Code is "COMPONENT_NOT_FOUND" or "RESONITE_OPERATION_FAILED") { }
            }
            if (component is null)
            {
                var stableSlot = StableReferenceResolver.ResolveSlot(stateFile, "$slot:" + stableComponent.SlotKey);
                var slotId = (await ResolveStableReferenceCoreAsync(stateFile, "$slot:" + stableComponent.SlotKey,
                    currentConnectionId, resolvingComponents, cancellationToken)).Id;
                var slot = await client.GetSlotAsync(slotId, 0, true, cancellationToken);
                Dictionary<string, string>? referenceTargets = null;
                if (firstVisit && stableComponent.ReferenceSelectors is { Count: > 0 })
                {
                    referenceTargets = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var reference in stableComponent.ReferenceSelectors)
                    {
                        var targetSyntax = StableSelectorSyntax.Parse(reference.Value);
                        if (targetSyntax.Kind != "slot" && resolvingComponents.Contains(targetSyntax.Key)) continue;
                        var target = await ResolveStableReferenceCoreAsync(stateFile, reference.Value, currentConnectionId,
                            resolvingComponents, cancellationToken);
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
                        ["Add identityFields or a stable managed reference that uniquely identifies this Component."]);
                component = await client.GetComponentAsync(matching[0].Id, cancellationToken);
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
        var name = NormalizePath(stable.Path).Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
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
            prepared.Entries.Count(x => x.Action is "update" or "rename" or "relocate"),
            prepared.Entries.Count(x => x.Action == "no-op"),
            prepared.Entries.Count(x => x.Action == "rename"),
            prepared.Entries.Count(x => x.Action == "delete"), false,
            $"Non-atomic preview. State checkpoint: {prepared.StatePath}. Re-run apply to converge; deletion requires --prune --yes.");
    }

    public async Task<ApplyResult> ApplyAsync(ApplyDocument document, ApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        var stopwatch = Stopwatch.StartNew();
        if (client is IResoniteClientDiagnostics diagnostics) diagnostics.ResetMetrics();
        options.Progress?.Invoke(new ApplyProgress("validate", 0, 1, document.SourcePath, "Validating and planning before mutation."));
        var prepared = await PrepareAsync(document, options, cancellationToken);
        if (options.Prune && !options.ConfirmDeletes)
            throw new RLoopException("CONFIRMATION_REQUIRED", "apply --prune is destructive and requires --yes.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["deleteCandidates"] = prepared.Deletions.Count, ["stateFile"] = prepared.StatePath });
        var counts = new ApplyCounts();
        var completed = 0;
        var total = prepared.Nodes.Count + prepared.Components.Count * 2;
        try
        {
            foreach (var asset in prepared.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                asset.Url = asset.DirectUrl ?? (asset.Action == "no-op" && prepared.State.Assets.TryGetValue(asset.Key, out var saved)
                    ? saved.Url : await client.ImportAssetAsync(asset.Spec, asset.ResolvedSource, cancellationToken));
                if (asset.Action == "create") counts.AssetsImported++;
                else counts.AssetsUnchanged++;
                prepared.State.Assets[asset.Key] = new ApplyStateAsset(asset.Spec.Kind, asset.SourceHash, asset.Url);
                Checkpoint(prepared);
                options.Progress?.Invoke(new ApplyProgress("assets", counts.AssetsImported + counts.AssetsUnchanged,
                    prepared.Assets.Count, "$assets/" + asset.Key, asset.Action == "create" ? "imported asset" : "reused asset"));
            }
            var assetUrls = prepared.Assets.ToDictionary(x => x.Key, x => x.Url!, StringComparer.Ordinal);
            foreach (var node in prepared.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentId = node.Parent?.Id ?? prepared.ParentId;
                switch (node.SlotAction)
                {
                    case "create":
                        // Persist intent before the remote mutation. If the response is lost after Resonite
                        // creates the Slot, the next run can bind the exact pending path without duplicating it.
                        prepared.State.Slots[node.StableKey] = new ApplyStateSlot(string.Empty, node.Path, node.Spec.RuntimeRelocatable);
                        Checkpoint(prepared);
                        node.Id = await client.CreateSlotAsync(new SlotCreateRequest(parentId, node.Spec.Name,
                            node.Spec.Position?.ToVector3("position"), node.Spec.Rotation?.ToQuaternion("rotation"),
                            node.Spec.Scale?.ToVector3("scale")), cancellationToken);
                        counts.SlotsCreated++;
                        break;
                    case "update":
                    case "relocate":
                        node.Id = node.Existing!.Id;
                        await client.UpdateSlotAsync(CreateSlotUpdate(node, prepared.ParentId), cancellationToken);
                        counts.SlotsUpdated++;
                        break;
                    default:
                        node.Id = node.Existing!.Id;
                        counts.SlotsUnchanged++;
                        break;
                }
                prepared.State.Slots[node.StableKey] = new ApplyStateSlot(node.Id, node.Path, node.Spec.RuntimeRelocatable);
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("slots", completed, total, node.Path, $"{node.SlotAction} Slot"));
            }

            var byKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null)
                .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
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
                    if (CanResolveAll(createFields, byKey, slotsByKey))
                    {
                        initialFields = await ResolveFieldsAsync(createFields, byKey, slotsByKey, assetUrls, cancellationToken);
                        component.AppliedOnCreate = initialFields;
                    }
                    prepared.State.Components[component.StableKey] = CreateComponentState(component, string.Empty);
                    Checkpoint(prepared);
                    var created = await client.AddComponentAsync(component.Node.Id!, component.Spec.Type, initialFields, cancellationToken);
                    component.Id = created.Id;
                    component.ResolvedType = created.Type;
                    counts.ComponentsAdded++;
                }
                if (!string.IsNullOrWhiteSpace(component.Spec.Key)) byKey[component.Spec.Key!] = component;
                prepared.State.Components[component.StableKey] = CreateComponentState(component, component.Id!);
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("components", completed, total, component.Path,
                    component.Existing is null ? "created Component" : "resolved Component"));
            }

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
                    await client.SetComponentMembersAsync(component.Id!, component.ResolvedType ?? component.Spec.Type, changed, cancellationToken);
                    if (component.Existing is not null) counts.ComponentsUpdated++;
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
                }
                prepared.State.Components[component.StableKey] = CreateComponentState(component, component.Id!, fields);
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("fields", completed, total, component.Path,
                    changed.Count == 0 ? "no field changes" : $"updated {changed.Count} field(s)"));
            }

            if (options.Prune)
            {
                foreach (var deletion in prepared.Deletions.Where(x => x.Kind == "component"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await client.RemoveComponentAsync(deletion.Id, cancellationToken);
                    prepared.State.Components.Remove(deletion.Key);
                    counts.ComponentsDeleted++;
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
        var assetUrls = prepared.Assets.Where(x => x.DirectUrl is not null || prepared.State.Assets.ContainsKey(x.Key))
            .ToDictionary(x => x.Key, x => x.DirectUrl ?? prepared.State.Assets[x.Key].Url, StringComparer.Ordinal);
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
                        if (!byKey.TryGetValue(key, out var target) || target.Existing is null)
                            throw UnknownApplyReference(probe.Target, byKey.Keys);
                        switch (probe.Kind?.ToLowerInvariant())
                        {
                            case "method":
                            {
                                if (string.IsNullOrWhiteSpace(probe.Method))
                                    throw new RLoopException("PROBE_METHOD_MISSING", $"Test '{test.Name}' method probe requires method.", ExitCodes.ValidationFailed);
                                var definition = await client.DescribeComponentTypeAsync(target.Existing.Type, cancellationToken);
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
                            {
                                if (!probe.Restore)
                                    throw new RLoopException("PROBE_RESTORE_REQUIRED", $"Test '{test.Name}' set-member probe requires restore=true.", ExitCodes.ValidationFailed);
                                if (probe.Value is not { } value)
                                    throw new RLoopException("PROBE_VALUE_MISSING", $"Test '{test.Name}' set-member probe requires value.", ExitCodes.ValidationFailed);
                                var selector = probe.Target[(probe.Target.IndexOf(':') + 1)..];
                                var separator = selector.LastIndexOf('.');
                                if (separator <= 0)
                                    throw new RLoopException("PROBE_MEMBER_TARGET_REQUIRED",
                                        $"Test '{test.Name}' set-member target must use $component:key.MemberName or $member:key.MemberName.", ExitCodes.ValidationFailed);
                                var memberName = selector[(separator + 1)..];
                                var current = await client.GetComponentAsync(target.Existing.Id, cancellationToken);
                                if (!current.Members.TryGetValue(memberName, out var original))
                                    throw new RLoopException("PROBE_MEMBER_NOT_FOUND", $"Probe member '{probe.Target}' was not found.", ExitCodes.NotFound);
                                if (original.Kind != "field")
                                    throw new RLoopException("PROBE_MEMBER_KIND_UNSUPPORTED",
                                        $"Transactional probes currently support field members; '{probe.Target}' is '{original.Kind}'.", ExitCodes.ValidationFailed);
                                var originalRaw = MemberRaw(original);
                                var temporaryRaw = await ResolveValueAsync(value, byKey, slotsByKey, assetUrls, cancellationToken);
                                restoreProbe = async () =>
                                {
                                    await client.SetComponentMemberAsync(target.Existing.Id, memberName, originalRaw, CancellationToken.None);
                                    var restored = await client.GetComponentAsync(target.Existing.Id, CancellationToken.None);
                                    if (!restored.Members.TryGetValue(memberName, out var restoredMember) || !MemberMatchesRaw(restoredMember, originalRaw))
                                        throw new RLoopException("PROBE_RESTORE_FAILED", $"Probe member '{probe.Target}' could not be restored.", ExitCodes.OperationFailed);
                                };
                                await client.SetComponentMemberAsync(target.Existing.Id, memberName, temporaryRaw, cancellationToken);
                                probeExecuted = true;
                                capability = "Transactional field probe executed; after assertions were polled and the original value was restored.";
                                break;
                            }
                            default:
                                throw new RLoopException("PROBE_KIND_UNSUPPORTED", $"Probe kind '{probe.Kind}' is not supported.", ExitCodes.ValidationFailed,
                                    suggestions: ["Use kind 'method' or 'set-member'."]);
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
        var stateDepth = state.Slots.Values.Select(x => x.Path.Count(ch => ch == '/')).DefaultIfEmpty(0).Max();
        var parent = await client.GetSlotAsync(parentId, Math.Clamp(Math.Max(MaxDepth(document.Children) + 1, stateDepth), 0, 64), true, cancellationToken);
        var snapshots = new List<(SlotInfo Slot, string Path)> { (parent, NormalizeParentPath(parentSelector)) };
        var rootKey = document.Slot!.Key!;
        if (state.Slots.TryGetValue(rootKey, out var rootState) && !ContainsSlot(parent, rootState.Id))
        {
            if (rootState.RuntimeRelocatable && !sameSession)
            {
                var stable = new StableSlotReference(rootKey, rootState.Id, rootState.Path, state.SessionId,
                    state.OwnershipKey, true);
                var relocated = await ResolveRelocatableSlotAsync(statePath, stable, cancellationToken);
                state.Slots[rootKey] = rootState = rootState with { Id = relocated.Id };
                snapshots.Add((relocated, relocated.Path ?? rootState.Path));
            }
            else
            try
            {
                var oldRootId = sameSession && !string.IsNullOrWhiteSpace(rootState.Id)
                    ? rootState.Id : await ResolveSlotIdAsync(rootState.Path, cancellationToken);
                snapshots.Add((await client.GetSlotAsync(oldRootId, Math.Clamp(stateDepth, 0, 64), true, cancellationToken), rootState.Path));
            }
            catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "SLOT_PATH_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
            {
                if (rootState.RuntimeRelocatable)
                {
                    var stable = new StableSlotReference(rootKey, rootState.Id, rootState.Path, state.SessionId,
                        state.OwnershipKey, true);
                    var relocated = await ResolveRelocatableSlotAsync(statePath, stable, cancellationToken);
                    state.Slots[rootKey] = rootState = rootState with { Id = relocated.Id };
                    snapshots.Add((relocated, relocated.Path ?? rootState.Path));
                }
            }
        }
        var prepared = new PreparedApply(document, options, state, statePath, session, parentId, sameSession, snapshots,
            migrations.Slots, migrations.Components);
        var rootSpec = new ApplyNodeSpec(document.Slot, document.Components, document.Children);
        BuildNode(prepared, rootSpec, null, parent, NormalizeParentPath(parentSelector), true);
        await PrepareRelocationTransformsAsync(prepared, NormalizeParentPath(parentSelector), cancellationToken);
        BuildAssetPlans(prepared);
        BuildComponentPlans(prepared);
        BuildDeletionPlans(prepared);
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
        var existing = MatchSlot(parentSnapshot, spec.Slot.Name, stateSlot, prepared.SameSession, path);
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
                var existing = relocating ? null :
                    MatchComponent(node.Existing?.Components ?? [], spec.Type, ordinal, stateComponent, prepared.SameSession,
                        topologyTargets);
                var componentIndex = existing is null
                    ? (node.Existing?.Components.Count ?? 0) + prepared.Components.Count(candidate => candidate.Node == node && candidate.Existing is null)
                    : node.Existing!.Components.ToList().FindIndex(candidate => candidate.Id == existing.Id);
                var runtime = new ComponentRuntime(spec, node, existing, stableKey, ordinal,
                    node.Path + "/@" + (spec.Key ?? normalizedType + "[" + ordinal + "]"), componentIndex);
                if (relocating)
                {
                    var sourceSlot = FindStateSlot(prepared, stateComponent!.SlotKey);
                    runtime.RelocationSource = sourceSlot is null ? null :
                        MatchComponent(sourceSlot.Components, spec.Type, ordinal, stateComponent, prepared.SameSession,
                            topologyTargets);
                    if (runtime.RelocationSource?.Id == existing?.Id) runtime.RelocationSource = null;
                }
                prepared.Components.Add(runtime);
            }
        }

        var existingByKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null)
            .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
        var slotsByKey = prepared.Nodes.ToDictionary(x => x.StableKey, StringComparer.Ordinal);
        var assetUrls = prepared.Assets.Where(x => x.DirectUrl is not null || prepared.State.Assets.ContainsKey(x.Key))
            .ToDictionary(x => x.Key, x => x.DirectUrl ?? prepared.State.Assets[x.Key].Url, StringComparer.Ordinal);
        foreach (var component in prepared.Components)
        {
            var action = component.RelocationSource is not null ? "relocate" : component.Existing is null ? "create" : "no-op";
            var reason = component.RelocationSource is not null
                ? $"stable key '{component.StableKey}' moves the Component to Slot '{component.Node.StableKey}'"
                : component.Existing is null ? "managed Component does not exist" : "Component and fields already match";
            var diffs = new List<ApplyMemberDiff>();
            if (component.Existing is not null)
            {
                foreach (var field in component.Spec.Fields ?? new Dictionary<string, JsonElement>())
                {
                    var resolvable = TryResolveRawForPlan(field.Value, existingByKey, slotsByKey, assetUrls, out var raw);
                    MemberValue? current = null;
                    var found = component.Existing.Members?.TryGetValue(field.Key, out current) == true;
                    if (!resolvable || !found || !MemberMatchesRaw(current!, raw))
                    {
                        action = "update";
                        reason = "one or more fields differ or depend on a new target";
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

    private static void BuildDeletionPlans(PreparedApply prepared)
    {
        var liveSlotKeys = prepared.Nodes.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var liveComponentKeys = prepared.Components.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var rootPath = prepared.Nodes[0].Path;
        var ownedRootPaths = new HashSet<string>(StringComparer.Ordinal) { rootPath };
        if (prepared.State.Slots.TryGetValue(prepared.Nodes[0].StableKey, out var previousRoot))
            ownedRootPaths.Add(previousRoot.Path);
        var snapshots = prepared.SnapshotSlots.Select(slot => (Slot: slot, Path: slot.Path ?? string.Empty)).ToArray();

        bool IsInsideOwnedRoot(string path, bool includeRoot) => ownedRootPaths.Any(ownedRoot =>
            includeRoot && path.Equals(ownedRoot, StringComparison.Ordinal) ||
            path.StartsWith(ownedRoot + "/", StringComparison.Ordinal));

        var staleSlots = new List<(string Key, ApplyStateSlot State, SlotInfo Slot, string Path)>();
        foreach (var stateSlot in prepared.State.Slots.Where(x => !liveSlotKeys.Contains(x.Key)).ToArray())
        {
            if (!IsInsideOwnedRoot(stateSlot.Value.Path, includeRoot: false)) continue;
            var slot = snapshots.FirstOrDefault(x => prepared.SameSession && x.Slot.Id == stateSlot.Value.Id || x.Path == stateSlot.Value.Path);
            if (slot.Slot is null || slot.Slot.Id.Equals("Root", StringComparison.OrdinalIgnoreCase)) continue;
            staleSlots.Add((stateSlot.Key, stateSlot.Value, slot.Slot, slot.Path));
        }
        var parentSlotDeletions = staleSlots.OrderBy(x => x.Path.Count(ch => ch == '/'))
            .Where(candidate => !staleSlots.Any(other => other.Path.Length < candidate.Path.Length &&
                candidate.Path.StartsWith(other.Path + "/", StringComparison.Ordinal)))
            .ToArray();
        var coveredSlotKeys = staleSlots.Where(stateSlot => parentSlotDeletions.Any(deletion =>
                stateSlot.Path == deletion.Path || stateSlot.Path.StartsWith(deletion.Path + "/", StringComparison.Ordinal)))
            .Select(stateSlot => stateSlot.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var stateComponent in prepared.State.Components.Where(x => !liveComponentKeys.Contains(x.Key) &&
                     !coveredSlotKeys.Contains(x.Value.SlotKey)).ToArray())
        {
            if (!prepared.State.Slots.TryGetValue(stateComponent.Value.SlotKey, out var stateSlot)) continue;
            var slot = snapshots.FirstOrDefault(x => prepared.SameSession && x.Slot.Id == stateSlot.Id || x.Path == stateSlot.Path);
            if (slot.Slot is null || !IsInsideOwnedRoot(slot.Path, includeRoot: true)) continue;
            var matches = slot.Slot.Components.Where(x => TypeNamesEquivalent(x.Type, stateComponent.Value.Type)).ToArray();
            var component = prepared.SameSession ? slot.Slot.Components.FirstOrDefault(x => x.Id == stateComponent.Value.Id) : null;
            component ??= stateComponent.Value.TypeOrdinal < matches.Length ? matches[stateComponent.Value.TypeOrdinal] : null;
            if (component is null) continue;
            var deletion = new DeletionRuntime("component", stateComponent.Key, component.Id,
                slot.Path + "/@" + stateComponent.Key, "stable key is no longer declared inside the owned boundary");
            prepared.Deletions.Add(deletion);
            prepared.Entries.Add(new ApplyPlanEntry("delete", deletion.Kind, deletion.Path, deletion.Key,
                component.Type, Reason: deletion.Reason));
        }

        foreach (var staleSlot in parentSlotDeletions)
        {
            var removedSlotKeys = staleSlots.Where(stateSlot => stateSlot.Path == staleSlot.Path ||
                    stateSlot.Path.StartsWith(staleSlot.Path + "/", StringComparison.Ordinal))
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
                return stable!.Kind == "slot"
                    ? slots.TryGetValue(stable.Key, out var slot) && slot.Id is not null
                    : components.TryGetValue(stable.Key, out var runtime) && runtime.Id is not null;
            return true;
        }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(x => CanResolve(x, components, slots));
        if (value.ValueKind == JsonValueKind.Object) return value.EnumerateObject().All(x => CanResolve(x.Value, components, slots));
        return true;
    }

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
        if (member.Kind == "list")
        {
            JsonNode? desired;
            try { desired = JsonNode.Parse(raw); } catch (JsonException) { return false; }
            if (desired is not JsonArray desiredArray || member.Elements is null || desiredArray.Count != member.Elements.Count) return false;
            for (var i = 0; i < desiredArray.Count; i++)
            {
                var current = MemberActual(member.Elements[i]);
                if (!JsonEquivalent(current, desiredArray[i])) return false;
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
            if (lv.TryGetValue<double>(out var ld) && rv.TryGetValue<double>(out var rd))
                return double.IsNaN(ld) && double.IsNaN(rd) || Math.Abs(ld - rd) <= 0.00001 * Math.Max(1, Math.Max(Math.Abs(ld), Math.Abs(rd)));
            return left.ToJsonString() == right.ToJsonString();
        }
        return JsonNode.DeepEquals(left, right);
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
            var oldName = state.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
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
        var byPath = prepared.SnapshotSlots.Where(slot => NormalizePath(slot.Path ?? string.Empty) == normalizedPath).ToArray();
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
                ["Add identityFields containing immutable managed values that uniquely identify this Component."]);
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
        IReadOnlyDictionary<string, string>? resolvedFields = null)
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
        var referenceSelectors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in memberNames)
            if (TryGetReferenceSelector(component.Spec, name, out var selector)) referenceSelectors[name] = selector;
        return new ApplyStateComponent(id, component.Node.StableKey, component.ResolvedType ?? component.Spec.Type,
            component.TypeOrdinal, component.ComponentIndex, memberNames, identityValues,
            referenceSelectors.Count == 0 ? null : referenceSelectors);
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
        var externalParentWorld = await WorldTransformAtPathAsync(parentPath, cancellationToken);
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
                var oldWorld = await WorldTransformAtPathAsync(previous.Path, cancellationToken);
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
        var parts = NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
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

    private static void Checkpoint(PreparedApply prepared)
    {
        prepared.State.SessionId = prepared.Session.UniqueSessionId;
        ApplyStateStore.Save(prepared.StatePath, prepared.State);
    }

    private static int MaxDepth(IReadOnlyList<ApplyNodeSpec>? children) => children is null || children.Count == 0
        ? 0 : 1 + children.Max(x => MaxDepth(x.Children));
    private static string NormalizeParentPath(string selector) => selector.Equals("Root", StringComparison.OrdinalIgnoreCase)
        ? "Root" : NormalizePath(selector);
    private static string NormalizePath(string path) => "Root/" + string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => !x.Equals("Root", StringComparison.OrdinalIgnoreCase)));
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

    private static bool ContainsSlot(SlotInfo root, string id) => root.Id == id || root.Children.Any(child => ContainsSlot(child, id));

    private sealed class PreparedApply(ApplyDocument document, ApplyOptions options, ApplyState state,
        string statePath, SessionInfo session, string parentId, bool sameSession, IReadOnlyList<(SlotInfo Slot, string Path)> snapshots,
        IReadOnlyDictionary<string, string> slotMigrations, IReadOnlyDictionary<string, string> componentMigrations)
    {
        public ApplyDocument Document { get; } = document;
        public ApplyOptions Options { get; } = options;
        public ApplyState State { get; } = state;
        public string StatePath { get; } = statePath;
        public SessionInfo Session { get; } = session;
        public string ParentId { get; } = parentId;
        public bool SameSession { get; } = sameSession;
        public IReadOnlyDictionary<string, string> SlotMigrations { get; } = slotMigrations;
        public IReadOnlyDictionary<string, string> ComponentMigrations { get; } = componentMigrations;
        public IReadOnlyList<SlotInfo> SnapshotSlots { get; } = snapshots.SelectMany(snapshot => Flatten(snapshot.Slot, snapshot.Path)).ToArray();
        public List<NodeRuntime> Nodes { get; } = [];
        public List<ComponentRuntime> Components { get; } = [];
        public List<ApplyPlanEntry> Entries { get; } = [];
        public List<DeletionRuntime> Deletions { get; } = [];
        public List<AssetRuntime> Assets { get; } = [];

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
        public int ComponentIndex { get; } = componentIndex;
        public string Path { get; } = path;
        public string? Id { get; set; }
        public string? ResolvedType { get; set; }
        public IReadOnlyDictionary<string, string>? AppliedOnCreate { get; set; }
        public ComponentSummary? RelocationSource { get; set; }
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
        IReadOnlyList<string>? CoveredSlotKeys = null, IReadOnlyList<string>? CoveredComponentKeys = null);

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
    }
}
