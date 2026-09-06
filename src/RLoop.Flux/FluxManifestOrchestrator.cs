using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public delegate Task<string?> FluxModuleSlotResolver(FluxModuleSpec module, CancellationToken cancellationToken);
public sealed record FluxModuleManifest(string? SchemaVersion, IReadOnlyList<FluxModuleSpec> Modules, string? ProjectDirectory = null,
    string? Parent = null, string? WorldState = null, string? DeployState = null);
public sealed record FluxModuleDeployment(string Name, string Action, string Reason, bool BuildSucceeded,
    bool Deployed, string? ModuleSlotIdBefore, string? ModuleSlotIdAfter, string? Error = null,
    IReadOnlyList<FluxResolvedBinding>? Bindings = null);
public sealed record FluxManifestResult(bool Success, string Manifest, string ParentSlotId,
    IReadOnlyList<FluxModuleDeployment> Modules, bool Atomic, string Recovery, bool WatchStopped = false);
public sealed record FluxManifestModuleValidation(string Name, string Source, string Module,
    int Ports, int Bindings, IReadOnlyList<string> DependsOn);
public sealed record FluxManifestValidationResult(bool Valid, string Manifest, string ProjectDirectory,
    string? WorldState, string DeployState, IReadOnlyList<FluxManifestModuleValidation> Modules);

public sealed class FluxManifestOrchestrator(IFluxTool flux)
{
    public async Task<FluxManifestResult> DeployAsync(string manifestPath, string parentSlotId, Uri url,
        string? libraryPath, string? helperPath, string? sessionId = null,
        IReadOnlyDictionary<string, FluxResolvedModuleBindings>? resolvedBindings = null,
        FluxModuleSlotResolver? resolveModuleSlot = null,
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
            var source = Path.GetFullPath(module.Source, loaded.Directory);
            state.Modules.TryGetValue(module.Name, out var previous);
            FluxResolvedModuleBindings? moduleBindings = null;
            _ = resolvedBindings?.TryGetValue(module.Name, out moduleBindings);
            ValidateResolvedBindings(module, moduleBindings, source);
            var hash = EffectiveHash(hashes[module.Name], moduleBindings);
            var beforeSlotId = resolveModuleSlot is null
                ? previous?.SlotId
                : await resolveModuleSlot(module, cancellationToken);
            if (previous?.Hash == hash && !string.IsNullOrWhiteSpace(beforeSlotId) &&
                state.ParentSlotId == parentSlotId && (resolveModuleSlot is not null || state.SessionId == sessionId))
            {
                if (previous.SlotId != beforeSlotId)
                {
                    state.Modules[module.Name] = new ModuleState(hash, beforeSlotId);
                    SaveState(statePath, state);
                }
                results.Add(new FluxModuleDeployment(module.Name, "no-op", "source and transitive dependency hashes match deploy state",
                    true, false, beforeSlotId, beforeSlotId, Bindings: moduleBindings?.Bindings));
                continue;
            }
            var build = await flux.BuildAsync(new FluxBuildRequest(source, loaded.ProjectDirectory, null, libraryPath), cancellationToken);
            if (!build.Success)
            {
                results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update", "build failed; deploy was skipped",
                    false, false, beforeSlotId, beforeSlotId, build.StandardError, moduleBindings?.Bindings));
                return Report(false);
            }
            if (Regex.IsMatch(build.StandardOutput + "\n" + build.StandardError,
                    @"Packing\s+0\s+ProtoFlux\s+nodes", RegexOptions.IgnoreCase))
                throw new RLoopException("FLUX_EMPTY_MODULE",
                    $"Module '{module.Name}' compiled successfully but contains zero ProtoFlux nodes.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["module"] = module.Name, ["source"] = source },
                    ["Keep a reachable entrypoint such as CallInput, a Dynamic Impulse receiver, LocalUpdate, or another consumer of the graph."]);
            var deploy = await flux.DeployAsync(new FluxDeployRequest(loaded.ProjectDirectory, module.Module, parentSlotId, url,
                libraryPath, helperPath, moduleBindings?.InputMap, moduleBindings?.OutputMap), cancellationToken);
            if (!deploy.Success)
            {
                var partialSlotId = resolveModuleSlot is null ? null : await resolveModuleSlot(module, cancellationToken);
                results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update", "deploy failed after successful build",
                    true, false, beforeSlotId, partialSlotId, deploy.StandardError, moduleBindings?.Bindings));
                return Report(false);
            }
            var afterSlotId = resolveModuleSlot is null
                ? deploy.OutputPath
                : await resolveModuleSlot(module, cancellationToken);
            if (string.IsNullOrWhiteSpace(afterSlotId))
            {
                results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update",
                    "Flux-SDK reported success, but the generated module child could not be re-observed",
                    true, false, beforeSlotId, null,
                    $"Module child '{module.Module}' was not found directly below parent '{parentSlotId}'.",
                    moduleBindings?.Bindings));
                return Report(false);
            }
            state.Modules[module.Name] = new ModuleState(hash, afterSlotId);
            state.ParentSlotId = parentSlotId;
            state.SessionId = sessionId;
            SaveState(statePath, state);
            results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update",
                previous is null ? "module has no deploy state" : "source or transitive dependency changed",
                true, true, beforeSlotId, afterSlotId, Bindings: moduleBindings?.Bindings));
        }
        return Report(true);

        FluxManifestResult Report(bool success) => new(success, loaded.Path, parentSlotId, results, false,
            $"Module replacement is non-atomic. Successful modules are checkpointed in {statePath}; fix the error and re-run to converge.");
    }

    public async Task<FluxManifestResult> WatchAsync(string manifestPath, string parentSlotId, Uri url,
        string? libraryPath, string? helperPath, string? sessionId, TimeSpan pollInterval,
        IReadOnlyDictionary<string, FluxResolvedModuleBindings>? resolvedBindings = null,
        FluxModuleSlotResolver? resolveModuleSlot = null,
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
                        resolvedBindings, resolveModuleSlot, cancellationToken);
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

    public static FluxManifestValidationResult ValidateManifest(string manifestPath)
    {
        var loaded = Load(manifestPath);
        var modules = new List<FluxManifestModuleValidation>();
        foreach (var module in Topological(loaded.Manifest.Modules))
        {
            var source = Path.GetFullPath(module.Source, loaded.Directory);
            var ports = FluxModuleSignature.Parse(File.ReadAllText(source));
            ValidateDeclaredBindings(module, ports);
            modules.Add(new FluxManifestModuleValidation(module.Name, source, module.Module,
                ports.Count, module.Bindings?.Count ?? 0, module.DependsOn ?? []));
        }
        return new FluxManifestValidationResult(true, loaded.Path, loaded.ProjectDirectory,
            ResolveWorldStatePath(loaded.Path, null, loaded.Manifest.WorldState),
            ResolveStatePath(loaded.Manifest, loaded.Path), modules);
    }

    public static string? ResolveWorldStatePath(string manifestPath, string? commandLineState,
        string? manifestState, string? currentDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(commandLineState))
            return Path.GetFullPath(commandLineState, currentDirectory ?? Environment.CurrentDirectory);
        if (!string.IsNullOrWhiteSpace(manifestState))
            return Path.GetFullPath(manifestState, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        return null;
    }

    private static void ValidateResolvedBindings(FluxModuleSpec module, FluxResolvedModuleBindings? resolved, string source)
    {
        var declared = module.Bindings ?? new Dictionary<string, FluxBindingSpec>();
        var ports = FluxModuleSignature.Parse(File.ReadAllText(source));
        if (declared.Count > 0 && resolved is null)
            throw new RLoopException("FLUX_BINDINGS_UNRESOLVED",
                $"Module '{module.Name}' declares bindings, but no resolved world targets were supplied.",
                ExitCodes.ValidationFailed);
        ValidateDeclaredBindings(module, ports);
        if (declared.Count == 0) return;

        var byName = resolved!.Bindings.ToDictionary(binding => binding.Name, StringComparer.Ordinal);
        var missing = declared.Keys.Where(name => !byName.ContainsKey(name)).ToArray();
        var extra = byName.Keys.Where(name => !declared.ContainsKey(name)).ToArray();
        var mismatched = declared.Where(pair => byName.TryGetValue(pair.Key, out var binding) &&
                (!string.Equals(pair.Value.Mode, binding.Mode, StringComparison.Ordinal) ||
                 !string.Equals(pair.Value.Target, binding.Selector, StringComparison.Ordinal)))
            .Select(pair => pair.Key).ToArray();
        if (missing.Length != 0 || extra.Length != 0 || mismatched.Length != 0)
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

        var portsByName = ports.ToDictionary(port => port.Name, StringComparer.Ordinal);

        foreach (var binding in resolved.Bindings)
        {
            var port = portsByName[binding.Name];
            if (!TargetTypeCompatible(port.Type, binding.TargetKind, binding.TargetType))
                throw new RLoopException("FLUX_BINDING_TYPE_MISMATCH",
                    $"Binding '{module.Name}.{binding.Name}' expects '{port.Type}', but '{binding.Selector}' resolves to '{binding.TargetType ?? binding.TargetKind}'.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["module"] = module.Name,
                        ["binding"] = binding.Name, ["portType"] = port.Type, ["portModifier"] = port.Modifier,
                        ["targetKind"] = binding.TargetKind, ["targetType"] = binding.TargetType });
        }
    }

    private static void ValidateDeclaredBindings(FluxModuleSpec module, IReadOnlyList<FluxModulePort> ports)
    {
        var declared = module.Bindings ?? new Dictionary<string, FluxBindingSpec>();
        if (declared.Count == 0)
        {
            if (ports.Count > 0)
                throw new RLoopException("FLUX_MODULE_PORT_UNBOUND",
                    $"Module '{module.Name}' declares input/output ports but its manifest has no bindings.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["module"] = module.Name, ["ports"] = ports.Select(port => port.Name).ToArray() });
            return;
        }

        var portsByName = ports.ToDictionary(port => port.Name, StringComparer.Ordinal);
        var undeclaredPorts = ports.Where(port => !declared.ContainsKey(port.Name)).Select(port => port.Name).ToArray();
        var unknownBindings = declared.Keys.Where(name => !portsByName.ContainsKey(name)).ToArray();
        if (undeclaredPorts.Length > 0 || unknownBindings.Length > 0)
            throw new RLoopException("FLUX_MODULE_PORT_UNBOUND",
                $"Module '{module.Name}' ports and manifest bindings do not form a complete one-to-one map.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["module"] = module.Name, ["unboundPorts"] = undeclaredPorts,
                    ["unknownBindings"] = unknownBindings });

        foreach (var binding in declared)
        {
            var port = portsByName[binding.Key];
            if (port.Direction != binding.Value.Mode)
                throw new RLoopException("FLUX_BINDING_DIRECTION_MISMATCH",
                    $"Binding '{module.Name}.{binding.Key}' is mode '{binding.Value.Mode}', but the compiled module port is '{port.Direction}'.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["portType"] = port.Type, ["modifier"] = port.Modifier });
            if (port.Modifier == "global" && IsInterfaceName(port.Type))
                throw new RLoopException("FLUX_INTERFACE_GLOBAL_UNSUPPORTED",
                    $"Flux-SDK 1.9.x cannot safely deploy interface global input '{module.Name}.{binding.Key}' ({port.Type}).",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["module"] = module.Name,
                        ["binding"] = binding.Key, ["portType"] = port.Type },
                    ["Use an element input with a concrete Component type, then convert it to a global inside the module with asDrivenGlobal.",
                     "For event-only coupling, use a Dynamic Impulse bridge instead of an interface global."]);
        }
    }

    private static bool TargetTypeCompatible(string expected, string targetKind, string? actual)
    {
        var expectedName = CanonicalType(expected);
        if (targetKind == "slot") return expectedName == "Slot";
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var actualName = CanonicalType(actual);
        if (expectedName.Equals(actualName, StringComparison.OrdinalIgnoreCase)) return true;
        if (targetKind == "component" && IsInterfaceName(expectedName)) return true;
        return false;
    }

    private static string CanonicalType(string type)
    {
        var name = SimpleType(type);
        return name.ToLowerInvariant() switch
        {
            "boolean" or "bool" => "Boolean",
            "byte" or "uint8" => "Byte",
            "sbyte" or "int8" => "SByte",
            "short" or "int16" => "Int16",
            "ushort" or "uint16" => "UInt16",
            "int" or "int32" => "Int32",
            "uint" or "uint32" => "UInt32",
            "long" or "int64" => "Int64",
            "ulong" or "uint64" => "UInt64",
            "float" or "single" or "float32" => "Single",
            "double" or "float64" => "Double",
            "char" => "Char",
            "string" => "String",
            _ => name
        };
    }

    private static bool IsInterfaceName(string type)
    {
        var name = SimpleType(type);
        return name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
    }

    private static string SimpleType(string type)
    {
        var value = type.Trim();
        var bracket = value.LastIndexOf(']');
        if (bracket >= 0) value = value[(bracket + 1)..];
        var dot = value.LastIndexOf('.');
        return dot >= 0 ? value[(dot + 1)..] : value;
    }

    private static LoadedManifest Load(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        if (!File.Exists(path)) throw new RLoopException("FLUX_MANIFEST_NOT_FOUND", $"Flux manifest '{path}' does not exist.", ExitCodes.NotFound);
        FluxModuleManifest manifest;
        try { manifest = JsonSerializer.Deserialize<FluxModuleManifest>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException("Manifest was empty."); }
        catch (JsonException ex)
        {
            var suggestions = ex.Path?.Contains(".bindings", StringComparison.OrdinalIgnoreCase) == true
                ? new[] { "Declare bindings as a JSON object keyed by module port name, for example: \"bindings\": { \"Score\": { \"mode\": \"drive\", \"target\": \"$member:score.Value\" } }." }
                : null;
            throw new RLoopException("FLUX_MANIFEST_INVALID", $"Invalid Flux manifest: {ex.Message}", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["manifest"] = path, ["jsonPath"] = ex.Path }, suggestions, ex);
        }
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
        manifest.DeployState ?? Path.Combine(".resoloop", "flux-state", Path.GetFileNameWithoutExtension(manifestPath) + ".json"),
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
