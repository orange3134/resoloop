using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed class WorldService(IResoniteClient client)
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

    public async Task<SlotInfo> InspectAsync(string selector, int depth, bool includeComponentData,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, depth, includeComponentData, cancellationToken);
        return AddPaths(slot, selector.Contains('/') ? NormalizePath(selector) : slot.Name);
    }

    public async Task<IReadOnlyList<SlotMatch>> FindAsync(string? name, bool exact, string? componentType,
        int depth, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(componentType))
            throw new RLoopException("FIND_FILTER_MISSING", "find requires --name or --component.", ExitCodes.InvalidArguments);
        var root = await client.GetSlotAsync("Root", depth, false, cancellationToken);
        var results = new List<SlotMatch>();
        Visit(root, "Root", slot =>
        {
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

    public async Task<IReadOnlyList<ComponentInfo>> ListComponentsAsync(string selector, CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, 0, false, cancellationToken);
        var result = new List<ComponentInfo>();
        foreach (var component in slot.Components) result.Add(await client.GetComponentAsync(component.Id, cancellationToken));
        return result;
    }

    public Task<ApplyValidationResult> ValidateApplyAsync(ApplyDocument document, bool strict,
        CancellationToken cancellationToken = default) =>
        ApplyDocumentValidator.ValidateAsync(document, strict ? client : null, cancellationToken);

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
            prepared.Entries.Count(x => x.Action is "update" or "rename"),
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
                        prepared.State.Slots[node.StableKey] = new ApplyStateSlot(string.Empty, node.Path);
                        Checkpoint(prepared);
                        node.Id = await client.CreateSlotAsync(new SlotCreateRequest(parentId, node.Spec.Name,
                            node.Spec.Position?.ToVector3("position"), node.Spec.Rotation?.ToQuaternion("rotation"),
                            node.Spec.Scale?.ToVector3("scale")), cancellationToken);
                        counts.SlotsCreated++;
                        break;
                    case "update":
                        node.Id = node.Existing!.Id;
                        await client.UpdateSlotAsync(CreateSlotUpdate(node), cancellationToken);
                        counts.SlotsUpdated++;
                        break;
                    default:
                        node.Id = node.Existing!.Id;
                        counts.SlotsUnchanged++;
                        break;
                }
                prepared.State.Slots[node.StableKey] = new ApplyStateSlot(node.Id, node.Path);
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
                    if (CanResolveAll(component.Spec.Fields, byKey, slotsByKey))
                    {
                        initialFields = await ResolveFieldsAsync(component.Spec.Fields, byKey, slotsByKey, assetUrls, cancellationToken);
                        component.AppliedOnCreate = initialFields;
                    }
                    prepared.State.Components[component.StableKey] = new ApplyStateComponent(string.Empty,
                        component.Node.StableKey, component.Spec.Type, component.TypeOrdinal);
                    Checkpoint(prepared);
                    var created = await client.AddComponentAsync(component.Node.Id!, component.Spec.Type, initialFields, cancellationToken);
                    component.Id = created.Id;
                    component.ResolvedType = created.Type;
                    counts.ComponentsAdded++;
                }
                if (!string.IsNullOrWhiteSpace(component.Spec.Key)) byKey[component.Spec.Key!] = component;
                prepared.State.Components[component.StableKey] = new ApplyStateComponent(component.Id!, component.Node.StableKey,
                    component.ResolvedType ?? component.Spec.Type, component.TypeOrdinal);
                Checkpoint(prepared);
                completed++;
                options.Progress?.Invoke(new ApplyProgress("components", completed, total, component.Path,
                    component.Existing is null ? "created Component" : "resolved Component"));
            }

            foreach (var component in prepared.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = await ResolveFieldsAsync(component.Spec.Fields, byKey, slotsByKey, assetUrls, cancellationToken);
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
                    prepared.State.Slots.Remove(deletion.Key);
                    foreach (var componentKey in prepared.State.Components.Where(x => x.Value.SlotKey == deletion.Key).Select(x => x.Key).ToArray())
                        prepared.State.Components.Remove(componentKey);
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
            foreach (var assertion in test.Assertions.Where(x => !string.Equals(x.Phase, "after", StringComparison.OrdinalIgnoreCase)))
                assertions.Add(await EvaluateAssertion(test.Name, assertion, "before", byKey, slotsByKey, assetUrls, cancellationToken));

            var probeExecuted = false;
            var structuralOnly = test.Probe is null;
            var capability = test.Probe is null ? "No interaction probe declared; field/reference structure was verified." : string.Empty;
            if (test.Probe is { } probe)
            {
                if (!probe.Safe)
                    throw new RLoopException("UNSAFE_PROBE_REJECTED", $"Test '{test.Name}' probe must declare safe=true.", ExitCodes.ValidationFailed);
                if (!allowProbe)
                {
                    structuralOnly = true;
                    capability = "Probe was not executed. Re-run with --probe --yes after confirming the method is safe.";
                }
                else
                {
                    var key = SymbolKey(probe.Target);
                    if (!byKey.TryGetValue(key, out var target) || target.Existing is null)
                        throw UnknownApplyReference(probe.Target, byKey.Keys);
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
                }
            }

            foreach (var assertion in test.Assertions.Where(x => string.Equals(x.Phase, "after", StringComparison.OrdinalIgnoreCase)))
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
                    evaluated = await EvaluateAssertion(test.Name, assertion, "after", byKey, slotsByKey, assetUrls, cancellationToken, refresh: true);
                    if (evaluated.Passed) break;
                    await Task.Delay(Math.Clamp(test.PollMs, 10, 5000), cancellationToken);
                } while (deadline.ElapsedMilliseconds < Math.Clamp(test.TimeoutMs, 10, 60_000));
                assertions.Add(evaluated);
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
        CancellationToken cancellationToken, bool refresh = false)
    {
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
        if (separator <= 0 || !components.TryGetValue(selector[..separator], out var runtime) || runtime.Existing is null)
            return new ApplyAssertionResult(testName, assertion.Target, phase, false,
                assertion.Expected is { } missingExpected ? JsonNode.Parse(missingExpected.GetRawText()) : null, null, "Component/member target was not found.");
        var memberName = selector[(separator + 1)..];
        var component = refresh ? await client.GetComponentAsync(runtime.Existing.Id, cancellationToken) :
            new ComponentInfo(runtime.Existing.Id, runtime.Existing.Type, runtime.Existing.Members ?? new Dictionary<string, MemberValue>());
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

    private static JsonNode? MemberActual(MemberValue member) => member.Kind switch
    {
        "reference" => JsonValue.Create(member.TargetId),
        "list" => new JsonArray((member.Elements ?? []).Select(MemberActual).ToArray()),
        _ => member.Value?.DeepClone()
    };

    private static string SymbolKey(string value) => value[(value.IndexOf(':') + 1)..].Split('.')[0];

    private async Task<PreparedApply> PrepareAsync(ApplyDocument document, ApplyOptions options, CancellationToken cancellationToken)
    {
        var offlineValidation = await ApplyDocumentValidator.ValidateAsync(document, cancellationToken: cancellationToken);
        ApplyDocumentValidator.ThrowIfInvalid(offlineValidation);
        var statePath = ApplyStateStore.ResolvePath(document, options.StateFile);
        var state = ApplyStateStore.Load(statePath, document.Ownership!.Key);
        var session = await client.GetSessionInfoAsync(cancellationToken);
        var sameSession = !string.IsNullOrWhiteSpace(session.UniqueSessionId) && session.UniqueSessionId == state.SessionId;
        state.SessionId = session.UniqueSessionId;
        var parentSelector = string.IsNullOrWhiteSpace(document.Slot!.Parent) ? "Root" : document.Slot.Parent;
        var parentId = await ResolveSlotIdAsync(parentSelector, cancellationToken);
        var stateDepth = state.Slots.Values.Select(x => x.Path.Count(ch => ch == '/')).DefaultIfEmpty(0).Max();
        var parent = await client.GetSlotAsync(parentId, Math.Clamp(Math.Max(MaxDepth(document.Children) + 1, stateDepth), 0, 64), true, cancellationToken);
        var prepared = new PreparedApply(document, options, state, statePath, session, parentId, sameSession);
        var rootSpec = new ApplyNodeSpec(document.Slot, document.Components, document.Children);
        BuildNode(prepared, rootSpec, null, parent, NormalizeParentPath(parentSelector), true);
        BuildAssetPlans(prepared);
        BuildComponentPlans(prepared);
        BuildDeletionPlans(prepared, parent);
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
        if (isRoot && existing is not null && stateSlot is null && !prepared.Options.Adopt)
            throw new RLoopException("APPLY_OWNERSHIP_UNVERIFIED",
                $"Slot '{path}' already exists but is not bound to ownership '{prepared.Document.Ownership!.Key}'.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["path"] = path, ["stateFile"] = prepared.StatePath },
                ["Inspect the target, then re-run with --adopt to bind this exact root Slot without deleting it."]);
        var action = existing is null ? "create" : SlotNeedsUpdate(existing, spec.Slot) ? "update" : "no-op";
        var node = new NodeRuntime(spec.Slot, spec.Components ?? [], parentRuntime, existing, stableKey, path, action);
        prepared.Nodes.Add(node);
        var planAction = action == "update" && existing?.Name != spec.Slot.Name ? "rename" : action;
        prepared.Entries.Add(new ApplyPlanEntry(planAction, "slot", path, stableKey,
            Reason: action == "create" ? "managed Slot does not exist" : planAction == "rename" ?
                $"stable key '{stableKey}' preserves identity while the name changes from '{existing!.Name}' to '{spec.Slot.Name}'" :
                action == "update" ? "transform differs" : "Slot already matches"));

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
                var existing = MatchComponent(node.Existing?.Components ?? [], spec.Type, ordinal, stateComponent, prepared.SameSession);
                var runtime = new ComponentRuntime(spec, node, existing, stableKey, ordinal,
                    node.Path + "/@" + (spec.Key ?? normalizedType + "[" + ordinal + "]"));
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
            var action = component.Existing is null ? "create" : "no-op";
            var reason = component.Existing is null ? "managed Component does not exist" : "Component and fields already match";
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
            prepared.Entries.Add(new ApplyPlanEntry(action, "component", component.Path, component.StableKey,
                component.Spec.Type, component.Spec.Fields?.Keys.ToArray(), reason, diffs));
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

    private static void BuildDeletionPlans(PreparedApply prepared, SlotInfo parentSnapshot)
    {
        var liveSlotKeys = prepared.Nodes.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var liveComponentKeys = prepared.Components.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var rootPath = prepared.Nodes[0].Path;
        var snapshots = new List<(SlotInfo Slot, string Path)>();
        void VisitSnapshot(SlotInfo slot, string path)
        {
            snapshots.Add((slot, path));
            foreach (var child in slot.Children) VisitSnapshot(child, path.TrimEnd('/') + "/" + child.Name);
        }
        VisitSnapshot(parentSnapshot, NormalizeParentPath(prepared.Document.Slot!.Parent ?? "Root"));

        foreach (var stateComponent in prepared.State.Components.Where(x => !liveComponentKeys.Contains(x.Key)).ToArray())
        {
            if (!prepared.State.Slots.TryGetValue(stateComponent.Value.SlotKey, out var stateSlot)) continue;
            var slot = snapshots.FirstOrDefault(x => prepared.SameSession && x.Slot.Id == stateSlot.Id || x.Path == stateSlot.Path);
            if (slot.Slot is null || !slot.Path.StartsWith(rootPath + "/", StringComparison.Ordinal) && slot.Path != rootPath) continue;
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

        foreach (var stateSlot in prepared.State.Slots.Where(x => !liveSlotKeys.Contains(x.Key)).ToArray())
        {
            if (!stateSlot.Value.Path.StartsWith(rootPath + "/", StringComparison.Ordinal)) continue;
            var slot = snapshots.FirstOrDefault(x => prepared.SameSession && x.Slot.Id == stateSlot.Value.Id || x.Path == stateSlot.Value.Path);
            if (slot.Slot is null || slot.Slot.Id.Equals("Root", StringComparison.OrdinalIgnoreCase)) continue;
            var deletion = new DeletionRuntime("slot", stateSlot.Key, slot.Slot.Id, slot.Path,
                "stable key is no longer declared inside the owned boundary");
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
        if (value.StartsWith("$ref:", StringComparison.Ordinal) || value.StartsWith("$component:", StringComparison.Ordinal))
        {
            var key = value[(value.IndexOf(':') + 1)..];
            return components.TryGetValue(key, out var component) && component.Id is not null
                ? component.Id
                : throw UnknownApplyReference(value, components.Keys);
        }
        if (value.StartsWith("$slot:", StringComparison.Ordinal))
        {
            var key = value[6..];
            return slots.TryGetValue(key, out var slot) && slot.Id is not null
                ? slot.Id : throw new RLoopException("APPLY_REFERENCE_NOT_FOUND", $"Slot reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
        }
        if (value.StartsWith("$asset:", StringComparison.Ordinal))
        {
            var key = value[7..];
            return assets.TryGetValue(key, out var url) ? url : throw new RLoopException(
                "APPLY_REFERENCE_NOT_FOUND", $"Asset reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
        }
        if (value.StartsWith("$member:", StringComparison.Ordinal))
        {
            var selector = value[8..];
            var separator = selector.LastIndexOf('.');
            if (separator <= 0 || !components.TryGetValue(selector[..separator], out var component) || component.Id is null)
                throw UnknownApplyReference(value, components.Keys);
            var memberName = selector[(separator + 1)..];
            if (component.MemberIds.TryGetValue(memberName, out var cached)) return cached;
            if (component.Existing?.Members is not null && component.Existing.Members.TryGetValue(memberName, out var summaryMember) &&
                !string.IsNullOrWhiteSpace(summaryMember.Id))
            {
                component.MemberIds[memberName] = summaryMember.Id;
                return summaryMember.Id;
            }
            var inspected = await client.GetComponentAsync(component.Id, cancellationToken);
            foreach (var member in inspected.Members.Where(x => !string.IsNullOrWhiteSpace(x.Value.Id)))
                component.MemberIds[member.Key] = member.Value.Id!;
            if (component.MemberIds.TryGetValue(memberName, out var id)) return id;
            throw new RLoopException("APPLY_MEMBER_REFERENCE_NOT_FOUND", $"Member reference '{value}' was not found.", ExitCodes.ValidationFailed);
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
            var key = text.StartsWith("$ref:", StringComparison.Ordinal) ? text[5..] :
                text.StartsWith("$component:", StringComparison.Ordinal) ? text[11..] :
                text.StartsWith("$member:", StringComparison.Ordinal) ? MemberKey(text[8..]) : null;
            if (key is not null) return components.TryGetValue(key, out var runtime) && runtime.Id is not null;
            if (text.StartsWith("$slot:", StringComparison.Ordinal)) return slots.TryGetValue(text[6..], out var slot) && slot.Id is not null;
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
            if (text.StartsWith("$ref:", StringComparison.Ordinal) || text.StartsWith("$component:", StringComparison.Ordinal))
            {
                var key = text[(text.IndexOf(':') + 1)..];
                if (components.TryGetValue(key, out var target) && target.Existing is not null) { raw = target.Existing.Id; return true; }
                raw = string.Empty; return false;
            }
            if (text.StartsWith("$slot:", StringComparison.Ordinal))
            {
                if (slots.TryGetValue(text[6..], out var target) && target.Existing is not null) { raw = target.Existing.Id; return true; }
                raw = string.Empty; return false;
            }
            if (text.StartsWith("$asset:", StringComparison.Ordinal))
            {
                if (assets.TryGetValue(text[7..], out var url)) { raw = url; return true; }
                raw = string.Empty; return false;
            }
            if (text.StartsWith("$member:", StringComparison.Ordinal))
            {
                var selector = text[8..];
                var separator = selector.LastIndexOf('.');
                if (separator > 0 && components.TryGetValue(selector[..separator], out var target) &&
                    target.Existing?.Members is not null && target.Existing.Members.TryGetValue(selector[(separator + 1)..], out var member) &&
                    !string.IsNullOrWhiteSpace(member.Id)) { raw = member.Id; return true; }
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
                var current = member.Elements[i].Kind == "reference" ? JsonValue.Create(member.Elements[i].TargetId) : NormalizeNode(member.Elements[i].Value);
                if (!JsonEquivalent(current, desiredArray[i])) return false;
            }
            return true;
        }
        if (member.Kind != "field") return false;
        var currentNode = NormalizeNode(member.Value);
        JsonNode? desiredNode;
        if (IsStringLike(member.Type)) desiredNode = JsonValue.Create(raw);
        else
        {
            try { desiredNode = JsonNode.Parse(raw); }
            catch (JsonException) { desiredNode = JsonValue.Create(raw); }
        }
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
        if (candidates.Length == 0 && state is not null)
        {
            var oldName = state.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(oldName)) candidates = parent.Children.Where(x => x.Name == oldName).ToArray();
        }
        if (candidates.Length == 0) candidates = parent.Children.Where(x => x.Name == desiredName).ToArray();
        if (candidates.Length > 1)
            throw new RLoopException("APPLY_TARGET_AMBIGUOUS", $"Multiple Slots match managed target '{desiredPath}'.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["ids"] = candidates.Select(x => x.Id).ToArray() });
        return candidates.SingleOrDefault();
    }

    private static ComponentSummary? MatchComponent(IReadOnlyList<ComponentSummary> components, string type, int ordinal,
        ApplyStateComponent? state, bool sameSession)
    {
        if (state is not null && sameSession)
        {
            var byId = components.SingleOrDefault(x => x.Id == state.Id);
            if (byId is not null) return byId;
        }
        var matches = components.Where(x => TypeNamesEquivalent(x.Type, state?.Type ?? type)).ToArray();
        var requestedOrdinal = state?.TypeOrdinal ?? ordinal;
        return requestedOrdinal >= 0 && requestedOrdinal < matches.Length ? matches[requestedOrdinal] : null;
    }

    private static bool SlotNeedsUpdate(SlotInfo existing, ApplySlotSpec desired) =>
        existing.Name != desired.Name || desired.Position is not null && !VectorEquals(existing.Position, desired.Position) ||
        desired.Rotation is not null && !QuaternionEquals(existing.Rotation, desired.Rotation) ||
        desired.Scale is not null && !VectorEquals(existing.Scale, desired.Scale);

    private static SlotUpdateRequest CreateSlotUpdate(NodeRuntime node)
    {
        var existing = node.Existing!;
        return new SlotUpdateRequest(existing.Id,
            existing.Name == node.Spec.Name ? null : node.Spec.Name,
            node.Spec.Position is not null && !VectorEquals(existing.Position, node.Spec.Position) ? node.Spec.Position.ToVector3("position") : null,
            node.Spec.Rotation is not null && !QuaternionEquals(existing.Rotation, node.Spec.Rotation) ? node.Spec.Rotation.ToQuaternion("rotation") : null,
            node.Spec.Scale is not null && !VectorEquals(existing.Scale, node.Spec.Scale) ? node.Spec.Scale.ToVector3("scale") : null);
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
    private static void Visit(SlotInfo slot, string path, Action<SlotInfo> visitor)
    {
        var withPath = slot with { Path = path };
        visitor(withPath);
        foreach (var child in slot.Children) Visit(child, path + "/" + child.Name, visitor);
    }

    private sealed class PreparedApply(ApplyDocument document, ApplyOptions options, ApplyState state,
        string statePath, SessionInfo session, string parentId, bool sameSession)
    {
        public ApplyDocument Document { get; } = document;
        public ApplyOptions Options { get; } = options;
        public ApplyState State { get; } = state;
        public string StatePath { get; } = statePath;
        public SessionInfo Session { get; } = session;
        public string ParentId { get; } = parentId;
        public bool SameSession { get; } = sameSession;
        public List<NodeRuntime> Nodes { get; } = [];
        public List<ComponentRuntime> Components { get; } = [];
        public List<ApplyPlanEntry> Entries { get; } = [];
        public List<DeletionRuntime> Deletions { get; } = [];
        public List<AssetRuntime> Assets { get; } = [];
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
    }

    private sealed class ComponentRuntime(ApplyComponentSpec spec, NodeRuntime node, ComponentSummary? existing,
        string stableKey, int typeOrdinal, string path)
    {
        public ApplyComponentSpec Spec { get; } = spec;
        public NodeRuntime Node { get; } = node;
        public ComponentSummary? Existing { get; } = existing;
        public string StableKey { get; } = stableKey;
        public int TypeOrdinal { get; } = typeOrdinal;
        public string Path { get; } = path;
        public string? Id { get; set; }
        public string? ResolvedType { get; set; }
        public IReadOnlyDictionary<string, string>? AppliedOnCreate { get; set; }
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

    private sealed record DeletionRuntime(string Kind, string Key, string Id, string Path, string Reason);

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
