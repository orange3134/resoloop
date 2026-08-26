using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Core;

namespace RLoop.Flux;

public sealed record FluxModuleSpec(string Name, string Source, string Module, IReadOnlyList<string>? DependsOn = null);
public sealed record FluxModuleManifest(string? SchemaVersion, IReadOnlyList<FluxModuleSpec> Modules, string? ProjectDirectory = null,
    string? Parent = null, string? WorldState = null, string? DeployState = null);
public sealed record FluxModuleDeployment(string Name, string Action, string Reason, bool BuildSucceeded,
    bool Deployed, string? BeforeSlotId, string? AfterSlotId, string? Error = null);
public sealed record FluxManifestResult(bool Success, string Manifest, string ParentSlotId,
    IReadOnlyList<FluxModuleDeployment> Modules, bool Atomic, string Recovery, bool WatchStopped = false);

public sealed class FluxManifestOrchestrator(IFluxTool flux)
{
    public async Task<FluxManifestResult> DeployAsync(string manifestPath, string parentSlotId, Uri url,
        string? libraryPath, string? helperPath, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var loaded = Load(manifestPath);
        var statePath = ResolveStatePath(loaded.Manifest, loaded.Path);
        var state = LoadState(statePath);
        var hashes = ComputeHashes(loaded.Manifest, loaded.Directory);
        var results = new List<FluxModuleDeployment>();
        foreach (var module in Topological(loaded.Manifest.Modules))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = hashes[module.Name];
            state.Modules.TryGetValue(module.Name, out var previous);
            if (previous?.Hash == hash && !string.IsNullOrWhiteSpace(previous.SlotId) &&
                state.ParentSlotId == parentSlotId && state.SessionId == sessionId)
            {
                results.Add(new FluxModuleDeployment(module.Name, "no-op", "source and transitive dependency hashes match deploy state",
                    true, false, previous.SlotId, previous.SlotId));
                continue;
            }
            var source = Path.GetFullPath(module.Source, loaded.Directory);
            var build = await flux.BuildAsync(new FluxBuildRequest(source, loaded.ProjectDirectory, null, libraryPath), cancellationToken);
            if (!build.Success)
            {
                results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update", "build failed; deploy was skipped",
                    false, false, previous?.SlotId, null, build.StandardError));
                return Report(false);
            }
            var deploy = await flux.DeployAsync(new FluxDeployRequest(loaded.ProjectDirectory, module.Module, parentSlotId, url, libraryPath, helperPath), cancellationToken);
            if (!deploy.Success)
            {
                results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update", "deploy failed after successful build",
                    true, false, previous?.SlotId, null, deploy.StandardError));
                return Report(false);
            }
            state.Modules[module.Name] = new ModuleState(hash, deploy.OutputPath);
            state.ParentSlotId = parentSlotId;
            state.SessionId = sessionId;
            SaveState(statePath, state);
            results.Add(new FluxModuleDeployment(module.Name, previous is null ? "create" : "update",
                previous is null ? "module has no deploy state" : "source or transitive dependency changed",
                true, true, previous?.SlotId, deploy.OutputPath));
        }
        return Report(true);

        FluxManifestResult Report(bool success) => new(success, loaded.Path, parentSlotId, results, false,
            $"Module replacement is non-atomic. Successful modules are checkpointed in {statePath}; fix the error and re-run to converge.");
    }

    public async Task<FluxManifestResult> WatchAsync(string manifestPath, string parentSlotId, Uri url,
        string? libraryPath, string? helperPath, string? sessionId, TimeSpan pollInterval,
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
                    last = await DeployAsync(manifestPath, parentSlotId, url, libraryPath, helperPath, sessionId, cancellationToken);
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
            foreach (var dependency in module.DependsOn ?? []) hash.AppendData(Encoding.UTF8.GetBytes(result[dependency]));
            result[module.Name] = Convert.ToHexString(hash.GetHashAndReset());
        }
        return result;
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
