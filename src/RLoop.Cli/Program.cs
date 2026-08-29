using System.Diagnostics;
using System.Text.Json;
using RLoop.Core;
using RLoop.Flux;
using RLoop.Flux.Deployer;
using RLoop.ResoniteLink;

namespace RLoop.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = ParsedArguments.Parse(args);
        var output = new OutputWriter(parsed.Has("json"));
        using var userCancellation = new CancellationTokenSource();
        CancellationTokenSource? commandCancellation = null;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; userCancellation.Cancel(); };
        try
        {
            if (parsed.Positionals.Count == 0 || parsed.Has("help") || parsed.Positionals[0] is "help" or "-h")
            {
                PrintHelp(Console.Out, parsed.Positionals.Count > 1 ? parsed.Positionals[1] : null);
                return ExitCodes.Success;
            }

            if (parsed.Positionals[0].Equals("init", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed.Positionals.Count > 2)
                    throw new RLoopException("UNEXPECTED_ARGUMENT", "rloop init accepts at most one target directory.", ExitCodes.InvalidArguments);
                var target = parsed.Positionals.Count > 1 ? parsed.Positionals[1] : Environment.CurrentDirectory;
                var result = ProjectInitializer.Initialize(target);
                output.Success(result, writer =>
                {
                    writer.WriteLine($"initialized {result.RootDirectory}");
                    foreach (var path in result.Created) writer.WriteLine($"  created   {path}");
                    foreach (var path in result.Unchanged) writer.WriteLine($"  unchanged {path}");
                    writer.WriteLine("next:");
                    foreach (var step in result.NextSteps) writer.WriteLine($"  {step}");
                });
                return ExitCodes.Success;
            }

            var cliConfig = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["url"] = parsed.Option("url"), ["timeout"] = parsed.Option("timeout"),
                ["command-timeout"] = parsed.Option("command-timeout"),
                ["flux-executable"] = parsed.Option("flux-executable"), ["flux-deployer"] = parsed.Option("flux-deployer"),
                ["library-path"] = parsed.Option("library-path"), ["log-path"] = parsed.Option("log-path")
            };
            var resolution = ConfigResolver.Resolve(Environment.CurrentDirectory, cliConfig);
            commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(userCancellation.Token);
            commandCancellation.CancelAfter(TimeSpan.FromSeconds(resolution.Config.CommandTimeoutSeconds));
            var commandToken = commandCancellation.Token;
            if (parsed.Has("verbose")) Console.Error.WriteLine(JsonSerializer.Serialize(new { configSources = resolution.Sources }));

            var flux = new FluxProcessTool(resolution.Config.FluxExecutable ?? "flux-sdk", new FluxSdkDeployer());
            if (parsed.Positionals[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
                return await RunDoctor(output, resolution.Config, flux, commandToken);
            if (parsed.Positionals[0].Equals("flux", StringComparison.OrdinalIgnoreCase))
                return await RunFlux(parsed, output, resolution.Config, flux, commandToken);
            if (parsed.Positionals[0].Equals("logs", StringComparison.OrdinalIgnoreCase))
                return RunLogs(parsed, output, resolution.Config);
            if (parsed.Positionals[0].Equals("validate", StringComparison.OrdinalIgnoreCase) && !parsed.Has("strict"))
            {
                var validation = await ApplyDocumentValidator.ValidateAsync(
                    ApplyDocument.Load(parsed.Positional(1, "Apply file")), cancellationToken: commandToken);
                ApplyDocumentValidator.ThrowIfInvalid(validation);
                output.Success(validation);
                return ExitCodes.Success;
            }
            if (parsed.Positionals[0].Equals("type", StringComparison.OrdinalIgnoreCase) &&
                parsed.Positional(1, "type subcommand").Equals("specialize", StringComparison.OrdinalIgnoreCase))
            {
                var openGeneric = parsed.Positional(2, "Open generic type");
                if (parsed.Positionals.Count < 4)
                    throw new RLoopException("ARGUMENT_REQUIRED", "At least one generic type argument is required.", ExitCodes.InvalidArguments);
                var typeArguments = parsed.Positionals.Skip(3).ToArray();
                var specialized = GenericTypeName.Specialize(openGeneric, typeArguments);
                output.Success(new { openGeneric, arguments = typeArguments, specialized }, writer => writer.WriteLine(specialized));
                return ExitCodes.Success;
            }
            if (parsed.Positionals[0].Equals("scene", StringComparison.OrdinalIgnoreCase))
            {
                if (!parsed.Positional(1, "scene subcommand").Equals("summary", StringComparison.OrdinalIgnoreCase))
                    throw UnknownCommand(string.Join(' ', parsed.Positionals));
                var summary = await SceneArtifactService.SummarizeAsync(ApplyDocument.Load(parsed.Positional(2, "Apply file")), commandToken);
                if (parsed.Option("output") is { } summaryPath)
                {
                    summaryPath = Path.GetFullPath(summaryPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(summaryPath)!);
                    await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + "\n", commandToken);
                }
                output.Success(summary);
                return ExitCodes.Success;
            }
            if (parsed.Positionals[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
            {
                var document = ApplyDocument.Load(parsed.Positional(1, "Apply file"));
                var camera = parsed.RequireOption("camera");
                var explicitCaptureOutput = parsed.Option("output");
                var captureOutput = explicitCaptureOutput ?? document.Cameras?.GetValueOrDefault(camera)?.Output;
                if (string.IsNullOrWhiteSpace(captureOutput))
                    throw new RLoopException("CAPTURE_OUTPUT_REQUIRED", "--output is required unless the camera bookmark declares output.", ExitCodes.InvalidArguments);
                if (!Path.IsPathFullyQualified(captureOutput))
                    captureOutput = explicitCaptureOutput is not null || document.SourcePath is null
                        ? Path.GetFullPath(captureOutput)
                        : Path.GetFullPath(captureOutput, Path.GetDirectoryName(document.SourcePath)!);
                var result = await SceneArtifactService.CaptureAsync(document, camera, captureOutput,
                    parsed.Option("width") is null ? null : parsed.IntOption("width", 1280, 64, 8192),
                    parsed.Option("height") is null ? null : parsed.IntOption("height", 720, 64, 8192), commandToken);
                output.Success(result, writer => writer.WriteLine($"captured {result.Format} {result.Width}x{result.Height} -> {result.Output}"));
                return ExitCodes.Success;
            }

            var uri = ConfigResolver.RequireUrl(resolution.Config);
            await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(resolution.Config.TimeoutSeconds));
            await client.ConnectAsync(uri, TimeSpan.FromSeconds(resolution.Config.TimeoutSeconds), commandToken);
            var world = new WorldService(client);
            await RunResonite(parsed, output, client, world, commandToken);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested)
        {
            var error = new RLoopException("COMMAND_TIMEOUT", "The command exceeded its configured deadline.", ExitCodes.Timeout,
                suggestions: ["Increase --command-timeout only after checking progress and Resonite responsiveness."]);
            output.Error(error);
            return error.ExitCode;
        }
        catch (OperationCanceledException)
        {
            var error = new RLoopException("CANCELLED", "Operation was cancelled.", ExitCodes.OperationFailed);
            output.Error(error);
            return error.ExitCode;
        }
        catch (RLoopException ex) when (ex.Code == "APPLY_CANCELLED" &&
                                             commandCancellation?.IsCancellationRequested == true &&
                                             !userCancellation.IsCancellationRequested)
        {
            var error = new RLoopException("COMMAND_TIMEOUT",
                "The apply command exceeded its configured deadline; completed operations were checkpointed.",
                ExitCodes.Timeout, ex.Context,
                ["Re-run the same apply command to resume, or increase --command-timeout after checking Resonite responsiveness."], ex);
            output.Error(error);
            return error.ExitCode;
        }
        catch (RLoopException ex)
        {
            output.Error(ex);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            var wrapped = new RLoopException("UNEXPECTED_ERROR", ex.Message, ExitCodes.OperationFailed,
                parsed.Has("verbose") ? new Dictionary<string, object?> { ["exception"] = ex.ToString() } : null,
                ["Re-run with --verbose and inspect stderr."], ex);
            output.Error(wrapped);
            return wrapped.ExitCode;
        }
        finally
        {
            commandCancellation?.Dispose();
        }
    }

    private static async Task<int> RunDoctor(OutputWriter output, RLoopConfig config, IFluxTool flux,
        CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>();
        Uri? uri = null;
        try
        {
            uri = ConfigResolver.RequireUrl(config);
            checks.Add(new DoctorCheck("resonite-link-url", "pass", true, uri.ToString()));
        }
        catch (RLoopException ex)
        {
            checks.Add(new DoctorCheck("resonite-link-url", "fail", true, ex.Message, ex.Suggestions.FirstOrDefault()));
        }

        if (uri is not null)
        {
            try
            {
                await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                await client.ConnectAsync(uri, TimeSpan.FromSeconds(config.TimeoutSeconds), cancellationToken);
                var session = await client.GetSessionInfoAsync(cancellationToken);
                checks.Add(new DoctorCheck("resonite-connection", "pass", true,
                    $"Connected to Resonite {session.ResoniteVersion ?? "unknown"} through ResoniteLink {session.ResoniteLinkVersion ?? "unknown"}."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                checks.Add(new DoctorCheck("resonite-connection", "fail", true, ex.Message,
                    "Confirm that ResoniteLink is enabled in the target world and refresh the current port."));
            }
        }

        FluxToolStatus? fluxStatus = null;
        try
        {
            var status = await flux.GetStatusAsync(cancellationToken);
            fluxStatus = status;
            if (!status.Available)
                checks.Add(new DoctorCheck("flux-sdk", "warning", false, $"'{status.Executable}' is not available.",
                    "Install Papaltine.FluxSDK 1.9.0 when ProtoFlux development is needed."));
            else if (FluxCompatibility.Check(status.Version) is { Compatible: false } compatibility)
                checks.Add(new DoctorCheck("flux-sdk", "warning", false, $"{status.Executable} {status.Version}", compatibility.Message));
            else
                checks.Add(new DoctorCheck("flux-sdk", "pass", false, $"{status.Executable} {status.Version ?? "(version unknown)"}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("flux-sdk", "warning", false, ex.Message,
                "Check RLOOP_FLUX_EXECUTABLE or install Papaltine.FluxSDK 1.9.0."));
        }

        checks.Add(await ManagedDataCheckAsync(config.ResoniteManagedDataPath, fluxStatus, flux, cancellationToken));
        checks.Add(PathCheck("resonite-log", config.ResoniteLogPath,
            "Set RESONITE_LOG_PATH when rloop logs is needed."));

        var report = new DoctorReport(
            checks.Where(check => check.Required).All(check => check.Status == "pass"),
            Environment.CurrentDirectory,
            ConfigResolver.FindProjectConfigPath(Environment.CurrentDirectory),
            checks);
        output.Success(report, writer =>
        {
            foreach (var check in report.Checks)
            {
                writer.WriteLine($"[{check.Status}] {check.Name}: {check.Message}");
                if (check.Suggestion is not null) writer.WriteLine($"  next: {check.Suggestion}");
            }
            writer.WriteLine(report.Ready ? "ready: core Resonite development can start" : "not ready: resolve required checks above");
        });
        return ExitCodes.Success;
    }

    private static DoctorCheck PathCheck(string name, string? path, string suggestion)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new DoctorCheck(name, "warning", false, "Not configured.", suggestion);
        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) || Directory.Exists(fullPath)
                ? new DoctorCheck(name, "pass", false, fullPath)
                : new DoctorCheck(name, "warning", false, $"Configured path does not exist: {fullPath}", suggestion);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DoctorCheck(name, "warning", false, $"Configured path is invalid: {ex.Message}", suggestion);
        }
    }

    private static async Task<DoctorCheck> ManagedDataCheckAsync(string? path, FluxToolStatus? fluxStatus,
        IFluxTool flux, CancellationToken cancellationToken)
    {
        if (fluxStatus?.Available != true)
            return new DoctorCheck("resonite-managed-data", "warning", false,
                string.IsNullOrWhiteSpace(path)
                    ? "Not configured, and Flux-SDK availability was not confirmed."
                    : $"Configured path was not probed because Flux-SDK availability was not confirmed: {Path.GetFullPath(path)}.",
                "Install Flux-SDK or set RESONITE_MANAGED_DATA_PATH before ProtoFlux work.");
        try
        {
            var probe = await FluxManagedDataProbe.RunAsync(flux, path, cancellationToken);
            if (probe.Success)
                return new DoctorCheck("resonite-managed-data", "pass", false,
                    probe.AutoDiscovery
                        ? $"Not explicitly configured; Flux-SDK auto-discovery succeeded. {probe.Message}"
                        : $"Configured path resolved successfully: {probe.LibraryPath}. {probe.Message}");
            return new DoctorCheck("resonite-managed-data", "warning", false,
                probe.AutoDiscovery
                    ? $"Not explicitly configured; Flux-SDK auto-discovery failed: {probe.Message}"
                    : $"Configured path failed the Flux-SDK check/build probe: {probe.LibraryPath}. {probe.Message}",
                probe.AutoDiscovery
                    ? "Set RESONITE_MANAGED_DATA_PATH or --library-path to the active Resonite managed DLL directory."
                    : "Correct RESONITE_MANAGED_DATA_PATH or omit it to retry Flux-SDK auto-discovery.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DoctorCheck("resonite-managed-data", "warning", false,
                $"Flux-SDK check/build probe could not run: {ex.Message}",
                "Check the Flux-SDK installation and RESONITE_MANAGED_DATA_PATH.");
        }
    }

    private static async Task RunResonite(ParsedArguments args, OutputWriter output, IResoniteClient client,
        WorldService world, CancellationToken cancellationToken)
    {
        var command = args.Positionals[0].ToLowerInvariant();
        switch (command)
        {
            case "status":
            case "ping":
            {
                var sw = Stopwatch.StartNew();
                var info = await client.GetSessionInfoAsync(cancellationToken);
                sw.Stop();
                var data = new { info.Url, info.Connected, info.ResoniteVersion, info.ResoniteLinkVersion,
                    connectionId = info.UniqueSessionId, connectionIdScope = "ResoniteLink connection; do not use as a stable world identity",
                    latencyMs = sw.Elapsed.TotalMilliseconds };
                output.Success(data, w => w.WriteLine($"connected {info.Url} | Resonite {info.ResoniteVersion} | Link {info.ResoniteLinkVersion} | {sw.Elapsed.TotalMilliseconds:0.0} ms"));
                break;
            }
            case "hierarchy":
            {
                var depth = args.IntOption("depth", 2, -1, 64);
                var slot = await client.GetSlotAsync("Root", depth, args.Has("include-components"), cancellationToken);
                output.Success(slot, w => OutputWriter.Hierarchy(w, slot));
                break;
            }
            case "find":
            {
                var matches = await world.FindAsync(args.Option("name"), args.Has("exact"), args.Option("component"),
                    args.IntOption("depth", 8, -1, 64), cancellationToken,
                    new FindOptions(args.Option("under"), args.Has("direct-children"), args.Has("exclude-reference-only")));
                output.Success(matches, w => { foreach (var x in matches) w.WriteLine($"{x.Id}\t{x.Path}\t{string.Join(", ", x.Components.Select(c => c.Type))}"); });
                break;
            }
            case "inspect":
            {
                var componentFilter = args.Option("component");
                var memberFilter = args.Option("member");
                if (args.Has("components-only") || componentFilter is not null || memberFilter is not null)
                {
                    var components = await world.InspectComponentsAsync(args.Positional(1, "Slot ID or path"),
                        args.IntOption("depth", 1, 0, 64), componentFilter, memberFilter,
                        args.Has("exclude-reference-only"), cancellationToken);
                    output.Success(new { count = components.Count, components }, writer =>
                    {
                        foreach (var item in components) writer.WriteLine($"{item.Component.Id}\t{item.SlotPath}\t{item.Component.Type}");
                    });
                }
                else
                {
                    var slot = await world.InspectAsync(args.Positional(1, "Slot ID or path"),
                        args.IntOption("depth", 1, 0, 64), args.Has("members"), cancellationToken,
                        args.Has("exclude-reference-only"));
                    output.Success(slot);
                }
                break;
            }
            case "slot": await RunSlot(args, output, client, world, cancellationToken); break;
            case "component": await RunComponent(args, output, client, world, cancellationToken); break;
            case "type": await RunType(args, output, client, cancellationToken); break;
            case "apply":
            {
                var document = ApplyDocument.Load(args.Positional(1, "Apply file"));
                var result = await world.ApplyAsync(document, ApplyOptionsFrom(args, output), cancellationToken);
                output.Success(result, w => w.WriteLine($"applied slot {result.SlotId} (created={result.Created}, slots added={result.SlotsCreated}, slots updated={result.SlotsUpdated}, slots unchanged={result.SlotsUnchanged}, components added={result.ComponentsAdded}, updated={result.ComponentsUpdated}, unchanged={result.ComponentsUnchanged})"));
                break;
            }
            case "diff":
            case "plan":
            {
                var result = await world.PlanApplyAsync(ApplyDocument.Load(args.Positional(1, "Apply file")),
                    ApplyOptionsFrom(args, output), cancellationToken);
                var filters = new[] { "changes-only", "creates-only", "deletes-only", "summary" }.Where(args.Has).ToArray();
                if (filters.Length > 1)
                    throw new RLoopException("PLAN_FILTER_CONFLICT", "Use only one plan output filter at a time.", ExitCodes.InvalidArguments,
                        new Dictionary<string, object?> { ["filters"] = filters });
                var displayed = args.Has("summary") ? [] : args.Has("changes-only") ? result.Changes :
                    args.Has("creates-only") ? result.Operations.Where(operation => operation.Action == "create").ToArray() :
                    args.Has("deletes-only") ? result.Operations.Where(operation => operation.Action == "delete").ToArray() : result.Operations;
                var response = new
                {
                    result.Valid, result.SchemaVersion, result.OwnershipKey, result.StateFile,
                    connectionId = result.SessionId,
                    connectionIdScope = "ResoniteLink connection; stable keys and paths are used across connections",
                    operations = displayed,
                    changes = result.Changes,
                    result.Creates, result.Updates, result.NoOps, result.Renames, result.Deletes, result.Atomic, result.Recovery
                };
                output.Success(response, w =>
                {
                    foreach (var operation in displayed)
                        w.WriteLine($"{operation.Action,-7} {operation.Kind,-9} {operation.Path}");
                    w.WriteLine($"creates={result.Creates} updates={result.Updates} renames={result.Renames} deletes={result.Deletes} no-ops={result.NoOps}");
                    w.WriteLine($"atomic={result.Atomic}; recovery={result.Recovery}");
                });
                break;
            }
            case "validate":
            {
                var validation = await world.ValidateApplyAsync(ApplyDocument.Load(args.Positional(1, "Apply file")), true, cancellationToken);
                ApplyDocumentValidator.ThrowIfInvalid(validation);
                output.Success(validation);
                break;
            }
            case "test":
            {
                if (args.Has("probe") && !args.Has("yes")) RequireYes(args, "test --probe");
                var report = await world.TestAsync(ApplyDocument.Load(args.Positional(1, "Apply file")),
                    ApplyOptionsFrom(args, output), args.Has("probe"), cancellationToken);
                if (!report.Passed)
                    throw new RLoopException("APPLY_TEST_FAILED", $"{report.PassedCount}/{report.Total} tests passed.", ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["report"] = report });
                output.Success(report, writer =>
                {
                    foreach (var test in report.Tests) writer.WriteLine($"{(test.Passed ? "PASS" : "FAIL")} {test.Name} ({(test.StructuralOnly ? "structural-only" : "runtime")})");
                });
                break;
            }
            default: throw UnknownCommand(string.Join(' ', args.Positionals));
        }
    }

    private static async Task RunSlot(ParsedArguments args, OutputWriter output, IResoniteClient client, WorldService world, CancellationToken ct)
    {
        var sub = args.Positional(1, "slot subcommand").ToLowerInvariant();
        switch (sub)
        {
            case "create":
            {
                var parent = await world.ResolveSlotIdAsync(args.Option("parent") ?? "Root", ct);
                var result = await client.CreateSlotAsync(new SlotCreateRequest(parent, args.RequireOption("name"),
                    ParseVector(args, "position"), ParseQuaternion(args, "rotation"), ParseVector(args, "scale"), args.Option("id")), ct);
                output.Success(new { id = result, parentId = parent, name = args.Option("name") }, w => w.WriteLine(result));
                break;
            }
            case "set":
            {
                var id = await world.ResolveSlotIdAsync(args.Positional(2, "Slot ID or path"), ct);
                if (args.Option("name") is null && args.Option("position") is null && args.Option("rotation") is null && args.Option("scale") is null)
                    throw new RLoopException("UPDATE_EMPTY", "slot set requires at least one of --name, --position, --rotation, or --scale.", ExitCodes.InvalidArguments);
                await client.UpdateSlotAsync(new SlotUpdateRequest(id, args.Option("name"), ParseVector(args, "position"), ParseQuaternion(args, "rotation"), ParseVector(args, "scale")), ct);
                output.Success(new { id, updated = true });
                break;
            }
            case "delete":
            {
                RequireYes(args, "slot delete");
                var id = await world.ResolveSlotIdAsync(args.Positional(2, "Slot ID or path"), ct);
                if (id == "Root") throw new RLoopException("ROOT_DELETE_FORBIDDEN", "World Root cannot be deleted.", ExitCodes.ValidationFailed);
                await client.DeleteSlotAsync(id, ct);
                output.Success(new { id, deleted = true });
                break;
            }
            default: throw UnknownCommand($"slot {sub}");
        }
    }

    private static async Task RunComponent(ParsedArguments args, OutputWriter output, IResoniteClient client, WorldService world, CancellationToken ct)
    {
        var sub = args.Positional(1, "component subcommand").ToLowerInvariant();
        switch (sub)
        {
            case "list": output.Success(await world.ListComponentsAsync(args.Positional(2, "Slot ID or path"), ct)); break;
            case "inspect": output.Success(await client.GetComponentAsync(args.Positional(2, "Component ID"), ct)); break;
            case "add":
            {
                var slotId = await world.ResolveSlotIdAsync(args.Positional(2, "Slot ID or path"), ct);
                var type = args.Positional(3, "Component type");
                var fields = ParseAssignments(args.Options("set"));
                var result = await client.AddComponentAsync(slotId, type, fields, ct);
                output.Success(result, w => w.WriteLine(result.Id));
                break;
            }
            case "set":
            {
                var componentId = args.Positional(2, "Component ID");
                var member = args.Positional(3, "Member name");
                var value = args.Positional(4, "Member value");
                await client.SetComponentMemberAsync(componentId, member, value, ct);
                output.Success(new { componentId, member, value, updated = true });
                break;
            }
            case "remove":
            {
                RequireYes(args, "component remove");
                var componentId = args.Positional(2, "Component ID");
                await client.RemoveComponentAsync(componentId, ct);
                output.Success(new { componentId, removed = true });
                break;
            }
            default: throw UnknownCommand($"component {sub}");
        }
    }

    private static async Task RunType(ParsedArguments args, OutputWriter output, IResoniteClient client, CancellationToken ct)
    {
        var sub = args.Positional(1, "type subcommand").ToLowerInvariant();
        var query = args.Positional(2, sub == "search" ? "Search query" : "Type name");
        switch (sub)
        {
            case "search":
            {
                var types = await client.SearchComponentTypesAsync(query, args.IntOption("limit", 50, 1, 500), ct);
                output.Success(types, w => { foreach (var type in types) w.WriteLine(type); });
                break;
            }
            case "describe":
            {
                try { output.Success(await client.DescribeComponentTypeAsync(query, ct)); }
                catch (RLoopException ex) when (ex.Code == "COMPONENT_TYPE_NOT_FOUND") { output.Success(await client.DescribeTypeAsync(query, ct)); }
                break;
            }
            case "specialize":
            {
                if (args.Positionals.Count < 4)
                    throw new RLoopException("ARGUMENT_REQUIRED", "At least one generic type argument is required.", ExitCodes.InvalidArguments);
                var specialized = GenericTypeName.Specialize(query, args.Positionals.Skip(3).ToArray());
                output.Success(new { openGeneric = query, arguments = args.Positionals.Skip(3).ToArray(), specialized }, writer => writer.WriteLine(specialized));
                break;
            }
            default: throw UnknownCommand($"type {sub}");
        }
    }

    private static async Task<int> RunFlux(ParsedArguments args, OutputWriter output, RLoopConfig config, IFluxTool flux, CancellationToken ct)
    {
        var sub = args.Positional(1, "flux subcommand").ToLowerInvariant();
        if (sub == "status") { output.Success(await flux.GetStatusAsync(ct)); return ExitCodes.Success; }
        if (sub == "deploy-manifest" || sub == "watch" && args.Positional(2, "Flux source or manifest").EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var manifestPath = Path.GetFullPath(args.Positional(2, "Flux manifest"));
            var manifest = FluxManifestOrchestrator.Inspect(manifestPath);
            var uri = ConfigResolver.RequireUrl(config);
            await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            await client.ConnectAsync(uri, TimeSpan.FromSeconds(config.TimeoutSeconds), ct);
            var world = new WorldService(client);
            var currentSession = await client.GetSessionInfoAsync(ct);
            var parentSelector = args.Option("parent") ?? manifest.Parent ?? "Root";
            var stateSetting = args.Option("state") ?? manifest.WorldState;
            var statePath = string.IsNullOrWhiteSpace(stateSetting) ? null :
                Path.GetFullPath(stateSetting, Path.GetDirectoryName(manifestPath)!);
            string parentId;
            if (parentSelector.StartsWith("$slot:", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(statePath))
                    throw new RLoopException("FLUX_WORLD_STATE_REQUIRED", "A Flux parent using $slot:key requires worldState in the manifest or --state.", ExitCodes.ValidationFailed);
                parentId = (await world.ResolveStableReferenceAsync(statePath, parentSelector,
                    currentSession.UniqueSessionId, ct)).Id;
            }
            else parentId = await world.ResolveSlotIdAsync(parentSelector, ct);

            var resolvedBindings = new Dictionary<string, FluxResolvedModuleBindings>(StringComparer.Ordinal);
            foreach (var module in manifest.Modules.Where(module => module.Bindings is { Count: > 0 }))
            {
                if (string.IsNullOrWhiteSpace(statePath))
                    throw new RLoopException("FLUX_WORLD_STATE_REQUIRED",
                        $"Module '{module.Name}' declares bindings and requires worldState in the manifest or --state.", ExitCodes.ValidationFailed);
                var bindings = new List<FluxResolvedBinding>();
                foreach (var binding in module.Bindings!)
                {
                    var target = await world.ResolveStableReferenceAsync(statePath, binding.Value.Target,
                        currentSession.UniqueSessionId, ct);
                    bindings.Add(new FluxResolvedBinding(binding.Key, binding.Value.Mode, binding.Value.Target,
                        target.Id, target.Kind, target.Type));
                }
                resolvedBindings[module.Name] = new FluxResolvedModuleBindings(bindings);
            }
            var orchestrator = new FluxManifestOrchestrator(flux);
            async Task<string?> ResolveModuleSlot(FluxModuleSpec module, CancellationToken cancellationToken)
            {
                var parent = await client.GetSlotAsync(parentId, 1, false, cancellationToken);
                var names = new[]
                {
                    module.Module,
                    module.Module.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                }.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
                var matches = parent.Children.Where(child => names.Contains(child.Name, StringComparer.Ordinal)).ToArray();
                if (matches.Length > 1)
                    throw new RLoopException("FLUX_MODULE_SLOT_AMBIGUOUS",
                        $"Module '{module.Name}' matched multiple direct children below '{parentId}'.",
                        ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["module"] = module.Name, ["ids"] = matches.Select(match => match.Id).ToArray() });
                return matches.SingleOrDefault()?.Id;
            }
            var report = sub == "watch"
                ? await orchestrator.WatchAsync(manifestPath, parentId, uri, args.Option("library-path") ?? config.ResoniteManagedDataPath,
                    config.FluxDeployerPath, currentSession.UniqueSessionId,
                    TimeSpan.FromMilliseconds(args.IntOption("poll-ms", 500, 100, 10000)), resolvedBindings, ResolveModuleSlot, ct)
                : await orchestrator.DeployAsync(manifestPath, parentId, uri, args.Option("library-path") ?? config.ResoniteManagedDataPath,
                    config.FluxDeployerPath, currentSession.UniqueSessionId, resolvedBindings, ResolveModuleSlot, ct);
            if (!report.Success)
                throw new RLoopException("FLUX_MANIFEST_DEPLOY_FAILED", "One or more Flux modules failed; successful modules were checkpointed.", ExitCodes.ExternalToolFailed,
                    new Dictionary<string, object?> { ["report"] = report }, [report.Recovery]);
            output.Success(report, writer =>
            {
                foreach (var module in report.Modules) writer.WriteLine($"{module.Action,-7} {module.Name} build={module.BuildSucceeded} deploy={module.Deployed}");
                writer.WriteLine($"atomic={report.Atomic}; recovery={report.Recovery}");
            });
            return ExitCodes.Success;
        }
        FluxResult result;
        if (sub is "build" or "check" or "watch")
        {
            var request = new FluxBuildRequest(args.Positional(2, "ProtoGraph source"), args.Option("project"), args.Option("out"),
                args.Option("library-path") ?? config.ResoniteManagedDataPath, !args.Has("full-errors"));
            result = sub switch
            {
                "build" => await flux.BuildAsync(request, ct),
                "check" => await flux.CheckAsync(request, ct),
                _ => await flux.WatchAsync(request, ct)
            };
        }
        else if (sub == "deploy")
        {
            var uri = ConfigResolver.RequireUrl(config);
            var project = Path.GetFullPath(args.RequireOption("project"));
            var module = args.RequireOption("module");
            await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            await client.ConnectAsync(uri, TimeSpan.FromSeconds(config.TimeoutSeconds), ct);
            var parentId = await new WorldService(client).ResolveSlotIdAsync(args.Option("parent") ?? "Root", ct);
            result = await flux.DeployAsync(new FluxDeployRequest(project, module, parentId, uri,
                args.Option("library-path") ?? config.ResoniteManagedDataPath, config.FluxDeployerPath), ct);
        }
        else throw UnknownCommand($"flux {sub}");

        if (!result.Success)
        {
            var diagnosticChannels = (result.Diagnostics ?? []).Select(diagnostic => diagnostic.Channel).Distinct().ToArray();
            throw new RLoopException("FLUX_COMMAND_FAILED", $"Flux-SDK {sub} failed with exit code {result.ExitCode}.", ExitCodes.ExternalToolFailed,
                new Dictionary<string, object?>
                {
                    ["exitCode"] = result.ExitCode, ["diagnostics"] = result.Diagnostics ?? [],
                    ["primaryDiagnostics"] = result.PrimaryDiagnostics ?? [],
                    ["diagnosticChannels"] = diagnosticChannels,
                    ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError
                },
                diagnosticChannels.Length == 0
                    ? ["Inspect error.context.stdout and error.context.stderr for raw Flux-SDK output."]
                    : [$"Fix primaryDiagnostics first; parsed diagnostics came from {string.Join(" and ", diagnosticChannels)}."]);
        }
        output.Success(result, w => { if (!string.IsNullOrWhiteSpace(result.StandardOutput)) w.Write(result.StandardOutput); if (!string.IsNullOrWhiteSpace(result.StandardError)) w.Write(result.StandardError); });
        return ExitCodes.Success;
    }

    private static int RunLogs(ParsedArguments args, OutputWriter output, RLoopConfig config)
    {
        var path = args.Option("path") ?? args.Option("log-path") ?? config.ResoniteLogPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new RLoopException("RESONITE_LOG_PATH_MISSING", "No Resonite log path was configured.", ExitCodes.ConfigurationError,
                suggestions: ["Pass --path <log-file-or-directory> or set RESONITE_LOG_PATH."]);
        if (Directory.Exists(path))
            path = Directory.EnumerateFiles(path, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                   ?? throw new RLoopException("LOG_NOT_FOUND", $"No .log files were found in '{path}'.", ExitCodes.NotFound);
        if (!File.Exists(path)) throw new RLoopException("LOG_NOT_FOUND", $"Log file '{path}' was not found.", ExitCodes.NotFound);
        var tail = args.IntOption("tail", 200, 1, 10000);
        var lines = File.ReadLines(path).TakeLast(tail).ToArray();
        output.Success(new { path = Path.GetFullPath(path), lines }, w => { foreach (var line in lines) w.WriteLine(line); });
        return ExitCodes.Success;
    }

    private static IReadOnlyDictionary<string, string> ParseAssignments(IReadOnlyList<string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in values)
        {
            var equals = assignment.IndexOf('=');
            if (equals <= 0) throw new RLoopException("INVALID_ASSIGNMENT", $"Expected --set Member=value, got '{assignment}'.", ExitCodes.InvalidArguments);
            result[assignment[..equals]] = assignment[(equals + 1)..];
        }
        return result;
    }

    private static ApplyOptions ApplyOptionsFrom(ParsedArguments args, OutputWriter output) => new(
        args.Option("state"), args.Has("adopt"), args.Has("profile"),
        args.Has("quiet") ? null : progress => output.Progress(progress, args.Has("ndjson-progress")),
        args.Has("prune"), args.Has("yes"));

    private static Vector3Value? ParseVector(ParsedArguments args, string name) => args.Option(name) is { } text ? Vector3Value.Parse(text, $"--{name}") : null;
    private static QuaternionValue? ParseQuaternion(ParsedArguments args, string name) => args.Option(name) is { } text ? QuaternionValue.Parse(text, $"--{name}") : null;
    private static void RequireYes(ParsedArguments args, string operation)
    {
        if (!args.Has("yes")) throw new RLoopException("CONFIRMATION_REQUIRED", $"{operation} is destructive and requires --yes.", ExitCodes.ValidationFailed);
    }
    private static RLoopException UnknownCommand(string command) => new("UNKNOWN_COMMAND", $"Unknown command '{command}'.", ExitCodes.InvalidArguments,
        suggestions: ["Run rloop help to list commands."]);

    private static void PrintHelp(TextWriter writer, string? command = null)
    {
        var detail = command?.ToLowerInvariant() switch
        {
            "apply" => """
rloop apply FILE.json [--state FILE] [--adopt] [--profile] [--ndjson-progress] [--prune --yes]

Validates and plans the complete document before mutation. State checkpoints make a failed non-atomic apply resumable.
--adopt binds one verified existing root. --prune deletes stale owned targets and always requires --yes.
""",
            "plan" or "diff" => """
rloop plan|diff FILE.json [--state FILE] [--adopt]
  [--changes-only | --creates-only | --deletes-only | --summary]

Never changes the world. JSON output always includes a separate changes array; output filters affect only operations.
Review --deletes-only before apply --prune --yes.
""",
            "find" => """
rloop find (--name TEXT [--exact] | --component TYPE) [--under SLOT] [--direct-children]
  [--exclude-reference-only] [--depth 8] [--json]
""",
            "inspect" => """
rloop inspect SLOT [--depth 1] [--members] [--json]
rloop inspect SLOT [--component TYPE] [--member NAME] [--components-only]
  [--exclude-reference-only] [--depth 1] [--json]

Component/member filters return a bounded flat component view with count and Slot paths.
""",
            _ => null
        };
        if (detail is not null) { writer.WriteLine(detail); return; }
        writer.WriteLine("""
rloop 0.1 - agent-first Resonite CLI loop

Project setup:
  rloop init [DIRECTORY] [--json]
  rloop doctor [--url ws://localhost:PORT] [--json]

Connection and observation:
  rloop status|ping [--url ws://localhost:PORT] [--json]
  rloop hierarchy [--depth 2] [--include-components] [--json]
  rloop find (--name TEXT [--exact] | --component TYPE) [--under SLOT] [--direct-children] [--depth 8] [--json]
  rloop inspect SLOT [--depth 1] [--members] [--component TYPE] [--member NAME] [--components-only] [--json]
  rloop scene summary FILE.json [--output summary.json]
  rloop capture FILE.json --camera BOOKMARK [--output capture.svg] [--width 1280 --height 720]

Editing:
  rloop slot create --name NAME [--parent SLOT] [--position x,y,z] [--rotation x,y,z,w] [--scale x,y,z]
  rloop slot set SLOT [--name NAME] [--position x,y,z] [--rotation x,y,z,w] [--scale x,y,z]
  rloop slot delete SLOT --yes
  rloop component list SLOT
  rloop component inspect COMPONENT_ID
  rloop component add SLOT TYPE [--set Member=value ...]
  rloop component set COMPONENT_ID MEMBER VALUE
  rloop component remove COMPONENT_ID --yes
  rloop type search QUERY [--limit 50]
  rloop type describe TYPE
  rloop type specialize OPEN_GENERIC TYPE_ARGUMENT [...]
  rloop validate FILE.json [--strict]
  rloop plan|diff FILE.json [--state FILE] [--adopt] [--changes-only|--creates-only|--deletes-only|--summary]
  rloop apply FILE.json [--state FILE] [--adopt] [--profile] [--ndjson-progress] [--prune --yes]
  rloop test FILE.json [--state FILE] [--probe --yes]

ProtoFlux (Flux-SDK):
  rloop flux status
  rloop flux check|build|watch FILE.pg [--project DIR] [--out FILE] [--library-path DIR]
  rloop flux deploy --project DIR --module MODULE_PATH [--parent SLOT] [--library-path DIR]
  rloop flux deploy-manifest FILE.json [--parent SLOT|$slot:key] [--state WORLD_STATE]
  rloop flux watch FILE.json [--parent SLOT|$slot:key] [--state WORLD_STATE] [--poll-ms 500]

Diagnostics:
  rloop doctor
  rloop logs [--path FILE_OR_DIRECTORY] [--tail 200]

Global options: --url, --timeout SECONDS, --command-timeout SECONDS, --json, --verbose
Configuration priority: CLI > environment > .rloop.json > ~/.rloop/config.json
Environment: RESONITE_LINK_URL, RLOOP_TIMEOUT_SECONDS, RLOOP_COMMAND_TIMEOUT_SECONDS, RESONITE_MANAGED_DATA_PATH, RESONITE_LOG_PATH
""");
    }
}
