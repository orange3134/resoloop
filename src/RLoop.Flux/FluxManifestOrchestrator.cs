using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Core;

namespace RLoop.Flux;

public sealed record FluxBindingSpec(string Target, string Mode);
public sealed record FluxResolvedBinding(string Name, string Mode, string Selector, string TargetId,
    string TargetKind, string? TargetType);
public sealed record FluxResolvedModuleBindings(IReadOnlyList<FluxResolvedBinding> Bindings)
{
    public IReadOnlyDictionary<string, string> InputMap => Bindings.Where(binding => binding.Mode == "source")
        .ToDictionary(binding => binding.Name, binding => binding.TargetId, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> OutputMap => Bindings.Where(binding => binding.Mode == "drive")
        .ToDictionary(binding => binding.Name, binding => binding.TargetId, StringComparer.Ordinal);
}
public sealed record FluxModuleSpec(string Name, string Source, string Module, IReadOnlyList<string>? DependsOn = null,
    IReadOnlyDictionary<string, FluxBindingSpec>? Bindings = null);
public sealed record FluxModuleManifest(string? SchemaVersion, IReadOnlyList<FluxModuleSpec> Modules, string? ProjectDirectory = null,
    string? Parent = null, string? WorldState = null, string? DeployState = null);
public sealed record FluxModuleDeployment(string Name, string Action, string Reason, bool BuildSucceeded,
    bool Deployed, string? BeforeSlotId, string? AfterSlotId, string? Error = null,
    IReadOnlyList<FluxResolvedBinding>? Bindings = null);
public sealed record FluxManifestResult(bool Success, string Manifest, string ParentSlotId,
    IReadOnlyList<FluxModuleDeployment> Modules, bool Atomic, string Recovery, bool WatchStopped = false);

public sealed class FluxManifestOrchestrator(IFluxTool flux)
{
    public async Task<FluxManifestResult> DeployAsync(string manifestPath, string parentSlotId, Uri url,
        string? libraryPath, string? helperPath, string? sessionId = null,
        IReadOnlyDictionary<string, FluxResolvedModuleBindings>? resolvedBindings = null,
        CancellationToken cancellationToken = default)
    {
        var loaded = Load(manifestPath);
        var statePath = ResolveStatePath(loaded.Manifest, loaded.Path);
        var state = LoadState(statePath);
        var hashes = ComputeHashes(loaded.Manifest, loaded.Directory);
        var results = new List<FluxModuleDeployment>();
        foreach (var module in Topological(loaded.Manifest.Modules))
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Modules.TryGetValue(module.Name, out var previous);
            FluxResolvedModuleBindings? moduleBindings = null;
            _ = resolvedBindings?.TryGetValue(module.Name, out moduleBindings);
            ValidateResolvedBindings(module, moduleBindings);
            var hash = EffectiveHash(hashes[module.Name], moduleBindings);
            if (previous?.Hash == hash && !string.IsNullOrWhiteSpace(previous.SlotId) &&
                state.ParentSlotId == parentSlotId && state.SessionId == sessionId)
            {
                results.Add(new FluxModuleDeployment(module.Name, "no-op", "source and transitive dependency hashes match deploy state",
                    true, false, previous.SlotId, previous.SlotId, Bindings: moduleBindings?.Bindings));
                continue;
            }
            var source = Path.GetFullPath(module.Source, loaded.Directory);
            var build = await flux.BuildAsync(new FluxBuildRequest(source, loaded.ProjectDirectory, null, libraryPath), cancellationToken);
            if (!build.Success)
            {
                results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update", "build failed; deploy was skipped",
                    false, false, previous?.SlotId, null, build.StandardError, moduleBindings?.Bindings));
                return Report(false);
            }
            var deploy = await flux.DeployAsync(new FluxDeployRequest(loaded.ProjectDirectory, module.Module, parentSlotId, url,
                libraryPath, helperPath, moduleBindings?.InputMap, moduleBindings?.OutputMap), cancellationToken);
            if (!deploy.Success)
            {
                results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update", "deploy failed after successful build",
                    true, false, previous?.SlotId, null, deploy.StandardError, moduleBindings?.Bindings));
                return Report(false);
            }
            state.Modules[module.Name] = new ModuleState(hash, deploy.OutputPath);
            state.ParentSlotId = parentSlotId;
            state.SessionId = sessionId;
            SaveState(statePath, state);
            results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update",
                previous is null ? "module has no deploy state" : "source or transitive dependency changed",
                true, true, previous?.SlotId, deploy.OutputPath, Bindings: moduleBindings?.Bindings));
        }
        return Report(true);

        FluxManifestResult Report(bool success) => new(success, loaded.Path, parentSlotId, results, false,
            $"Module replacement is non-atomic. Successful modules are checkpointed in {statePath}; fix the error and re-run to converge.");
    }

    public async Task<FluxManifestResult> WatchAsync(string manifestPath, string parentSlotId, Uri url,
        string? libraryPath, string? helperPath, string? sessionId, TimeSpan pollInterval,
        IReadOnlyDictionary<string, FluxResolvedModuleBindings>? resolvedBindings = null,
        CancellationToken cancellationToken = default)
    {
        FluxManifestResult? last = null;
        string? fingerprint = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = Load(manifestPath);
                var current = Fingerprint(loaded);
                if (current != fingerprint)
                {
                    last = await DeployAsync(manifestPath, parentSlotId, url, libraryPath, helperPath, sessionId,
                        resolvedBindings, cancellationToken);
                    fingerprint = current;
                }
                await Task.Delay(pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && last is not null)
        {
            return last with { WatchStopped = true };
        }
    }

    public static FluxModuleManifest Inspect(string manifestPath) => Load(manifestPath).Manifest;

    private static void ValidateResolvedBindings(FluxModuleSpec module, FluxResolvedModuleBindings? resolved)
    {
        var declared = module.Bindings ?? new Dictionary<string, FluxBindingSpec>();
        if (declared.Count == 0) return;
        if (resolved is null)
            throw new RLoopException("FLUX_BINDINGS_UNRESOLVED",
                $"Module '{module.Name}' declares bindings, but no resolved world targets were supplied.",
                ExitCodes.ValidationFailed);

        var byName = resolved.Bindings.ToDictionary(binding => binding.Name, StringComparer.Ordinal);
        var missing = declared.Keys.Where(name => !byName.ContainsKey(name)).ToArray();
        var extra = byName.Keys.Where(name => !declared.ContainsKey(name)).ToArray();
        var mismatched = declared.Where(pair => byName.TryGetValue(pair.Key, out var binding) &&
                (!string.Equals(pair.Value.Mode, binding.Mode, StringComparison.Ordinal) ||
                 !string.Equals(pair.Value.Target, binding.Selector, StringComparison.Ordinal)))
            .Select(pair => pair.Key).ToArray();
        if (missing.Length == 0 && extra.Length == 0 && mismatched.Length == 0) return;

        throw new RLoopException("FLUX_BINDINGS_UNRESOLVED",
            $"Resolved bindings for module '{module.Name}' do not match its manifest declaration.",
            ExitCodes.ValidationFailed,
            new Dictionary<string, object?>
            {
                ["module"] = module.Name,
                ["missing"] = missing,
                ["extra"] = extra,
                ["mismatched"] = mismatched
            });
    }

    private static LoadedManifest Load(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        if (!File.Exists(path)) throw new RLoopException("FLUX_MANIFEST_NOT_FOUND", $"Flux manifest '{path}' does not exist.", ExitCodes.NotFound);
        FluxModuleManifest manifest;
        try { manifest = JsonSerializer.Deserialize<FluxModuleManifest>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException("Manifest was empty."); }
        catch (JsonException ex) { throw new RLoopException("FLUX_MANIFEST_INVALID", $"Invalid Flux manifest: {ex.Message}", ExitCodes.ValidationFailed, innerException: ex); }
        if (manifest.SchemaVersion != "1") throw new RLoopException("FLUX_MANIFEST_VERSION_UNSUPPORTED", "Flux manifest schemaVersion must be \"1\".", ExitCodes.ValidationFailed);
        if (manifest.Modules.Count == 0) throw new RLoopException("FLUX_MANIFEST_EMPTY", "Flux manifest requires at least one module.", ExitCodes.ValidationFailed);
        if (manifest.Modules.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Modules.Count)
            throw new RLoopException("FLUX_MODULE_DUPLICATE", "Flux module names must be unique.", ExitCodes.ValidationFailed);
        var directory = Path.GetDirectoryName(path)!;
        var project = Path.GetFullPath(manifest.ProjectDirectory ?? directory, directory);
        foreach (var module in manifest.Modules)
        {
            if (!File.Exists(Path.GetFullPath(module.Source, directory))) throw new RLoopException("FLUX_SOURCE_NOT_FOUND", $"Module '{module.Name}' source '{module.Source}' does not exist.", ExitCodes.NotFound);
            foreach (var dependency in module.DependsOn ?? [])
                if (!manifest.Modules.Any(x => x.Name == dependency)) throw new RLoopException("FLUX_DEPENDENCY_NOT_FOUND", $"Module '{module.Name}' depends on unknown module '{dependency}'.", ExitCodes.ValidationFailed);
            foreach (var binding in module.Bindings ?? new Dictionary<string, FluxBindingSpec>())
            {
                if (string.IsNullOrWhiteSpace(binding.Key))
                    throw new RLoopException("FLUX_BINDING_NAME_MISSING", $"Module '{module.Name}' contains an empty binding name.", ExitCodes.ValidationFailed);
                if (binding.Value.Mode is not ("source" or "drive"))
                    throw new RLoopException("FLUX_BINDING_MODE_INVALID",
                        $"Binding '{module.Name}.{binding.Key}' mode must be 'source' or 'drive'.", ExitCodes.ValidationFailed);
                if (string.IsNullOrWhiteSpace(binding.Value.Target) ||
                    !(binding.Value.Target.StartsWith("$slot:", StringComparison.Ordinal) ||
                      binding.Value.Target.StartsWith("$component:", StringComparison.Ordinal) ||
                      binding.Value.Target.StartsWith("$member:", StringComparison.Ordinal)))
                    throw new RLoopException("FLUX_BINDING_TARGET_INVALID",
                        $"Binding '{module.Name}.{binding.Key}' requires a stable $slot:, $component:, or $member: target.", ExitCodes.ValidationFailed);
                if (binding.Value.Mode == "drive" && !binding.Value.Target.StartsWith("$member:", StringComparison.Ordinal))
                    throw new RLoopException("FLUX_BINDING_DRIVE_REQUIRES_MEMBER",
                        $"Drive binding '{module.Name}.{binding.Key}' must target $member:key.MemberName.", ExitCodes.ValidationFailed);
            }
        }
        _ = Topological(manifest.Modules);
        return new LoadedManifest(path, directory, project, manifest);
    }

    private static IReadOnlyList<FluxModuleSpec> Topological(IReadOnlyList<FluxModuleSpec> modules)
    {
        var byName = modules.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var visited = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<FluxModuleSpec>();
        void Visit(string name, Stack<string> stack)
        {
            if (visited.GetValueOrDefault(name) == 2) return;
            if (visited.GetValueOrDefault(name) == 1)
                throw new RLoopException("FLUX_DEPENDENCY_CYCLE", $"Flux module dependency cycle: {string.Join(" -> ", stack.Reverse().Append(name))}", ExitCodes.ValidationFailed);
            visited[name] = 1; stack.Push(name);
            foreach (var dependency in byName[name].DependsOn ?? []) Visit(dependency, stack);
            stack.Pop(); visited[name] = 2; output.Add(byName[name]);
        }
        foreach (var module in modules) Visit(module.Name, new Stack<string>());
        return output;
    }

    private static Dictionary<string, string> ComputeHashes(FluxModuleManifest manifest, string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in Topological(manifest.Modules))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(File.ReadAllBytes(Path.GetFullPath(module.Source, directory)));
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(module.Bindings ?? new Dictionary<string, FluxBindingSpec>(), JsonOptions));
            foreach (var dependency in module.DependsOn ?? []) hash.AppendData(Encoding.UTF8.GetBytes(result[dependency]));
            result[module.Name] = Convert.ToHexString(hash.GetHashAndReset());
        }
        return result;
    }

    private static string EffectiveHash(string sourceHash, FluxResolvedModuleBindings? bindings)
    {
        if (bindings is null || bindings.Bindings.Count == 0) return sourceHash;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(sourceHash));
        foreach (var binding in bindings.Bindings.OrderBy(binding => binding.Name, StringComparer.Ordinal))
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(binding, JsonOptions));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Fingerprint(LoadedManifest loaded)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(loaded.Path));
        foreach (var module in loaded.Manifest.Modules) hash.AppendData(File.ReadAllBytes(Path.GetFullPath(module.Source, loaded.Directory)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ResolveStatePath(FluxModuleManifest manifest, string manifestPath) => Path.GetFullPath(
        manifest.DeployState ?? Path.Combine(".rloop", "flux-state", Path.GetFileNameWithoutExtension(manifestPath) + ".json"),
        Path.GetDirectoryName(manifestPath)!);
    private static DeployState LoadState(string path)
    {
        if (!File.Exists(path)) return new DeployState();
        try { return JsonSerializer.Deserialize<DeployState>(File.ReadAllText(path), JsonOptions) ?? new DeployState(); }
        catch (JsonException ex) { throw new RLoopException("FLUX_STATE_INVALID", $"Invalid Flux deploy state '{path}': {ex.Message}", ExitCodes.ValidationFailed, innerException: ex); }
    }
    private static void SaveState(string path, DeployState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions) + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private sealed record LoadedManifest(string Path, string Directory, string ProjectDirectory, FluxModuleManifest Manifest);
    private sealed class DeployState
    {
        public int SchemaVersion { get; set; } = 1;
        public string? ParentSlotId { get; set; }
        public string? SessionId { get; set; }
        public Dictionary<string, ModuleState> Modules { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed record ModuleState(string Hash, string? SlotId);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
}
