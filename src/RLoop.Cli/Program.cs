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
                PrintHelp(Console.Out);
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

        try
        {
            var status = await flux.GetStatusAsync(cancellationToken);
            if (!status.Available)
                checks.Add(new DoctorCheck("flux-sdk", "warning", false, $"'{status.Executable}' is not available.",
                    "Install Papaltine.FluxSDK 1.9.0 when ProtoFlux development is needed."));
            else if (status.Version is { } version && !version.StartsWith("1.9.", StringComparison.Ordinal))
                checks.Add(new DoctorCheck("flux-sdk", "warning", false, $"{status.Executable} {version}",
                    "rloop's Flux deployer is pinned to Flux-SDK 1.9.0; update the global tool before deploying."));
            else
                checks.Add(new DoctorCheck("flux-sdk", "pass", false, $"{status.Executable} {status.Version ?? "(version unknown)"}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("flux-sdk", "warning", false, ex.Message,
                "Check RLOOP_FLUX_EXECUTABLE or install Papaltine.FluxSDK 1.9.0."));
        }

        checks.Add(PathCheck("resonite-managed-data", config.ResoniteManagedDataPath,
            "Set RESONITE_MANAGED_DATA_PATH when ProtoFlux Reflection or compilation is needed."));
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
                var data = new { info.Url, info.Connected, info.ResoniteVersion, info.ResoniteLinkVersion, info.UniqueSessionId, latencyMs = sw.Elapsed.TotalMilliseconds };
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
                    args.IntOption("depth", 8, -1, 64), cancellationToken);
                output.Success(matches, w => { foreach (var x in matches) w.WriteLine($"{x.Id}\t{x.Path}\t{string.Join(", ", x.Components.Select(c => c.Type))}"); });
                break;
            }
            case "inspect":
            {
                var slot = await world.InspectAsync(args.Positional(1, "Slot ID or path"), args.IntOption("depth", 1, 0, 64), args.Has("members"), cancellationToken);
                output.Success(slot);
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
            case "plan":
            {
                var result = await world.PlanApplyAsync(ApplyDocument.Load(args.Positional(1, "Apply file")),
                    ApplyOptionsFrom(args, output), cancellationToken);
                output.Success(result, w =>
                {
                    foreach (var operation in result.Operations)
                        w.WriteLine($"{operation.Action,-7} {operation.Kind,-9} {operation.Path}");
                    w.WriteLine($"creates={result.Creates} updates={result.Updates} no-ops={result.NoOps}");
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
            default: throw UnknownCommand($"type {sub}");
        }
    }

    private static async Task<int> RunFlux(ParsedArguments args, OutputWriter output, RLoopConfig config, IFluxTool flux, CancellationToken ct)
    {
        var sub = args.Positional(1, "flux subcommand").ToLowerInvariant();
        if (sub == "status") { output.Success(await flux.GetStatusAsync(ct)); return ExitCodes.Success; }
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
            throw new RLoopException("FLUX_COMMAND_FAILED", $"Flux-SDK {sub} failed with exit code {result.ExitCode}.", ExitCodes.ExternalToolFailed,
                new Dictionary<string, object?> { ["exitCode"] = result.ExitCode, ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError },
                ["Inspect the compiler diagnostics in error.context.stderr."]);
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
        args.Has("quiet") ? null : progress => output.Progress(progress, args.Has("ndjson-progress")));

    private static Vector3Value? ParseVector(ParsedArguments args, string name) => args.Option(name) is { } text ? Vector3Value.Parse(text, $"--{name}") : null;
    private static QuaternionValue? ParseQuaternion(ParsedArguments args, string name) => args.Option(name) is { } text ? QuaternionValue.Parse(text, $"--{name}") : null;
    private static void RequireYes(ParsedArguments args, string operation)
    {
        if (!args.Has("yes")) throw new RLoopException("CONFIRMATION_REQUIRED", $"{operation} is destructive and requires --yes.", ExitCodes.ValidationFailed);
    }
    private static RLoopException UnknownCommand(string command) => new("UNKNOWN_COMMAND", $"Unknown command '{command}'.", ExitCodes.InvalidArguments,
        suggestions: ["Run rloop help to list commands."]);

    private static void PrintHelp(TextWriter writer) => writer.WriteLine("""
rloop 0.1 - agent-first Resonite CLI loop

Project setup:
  rloop init [DIRECTORY] [--json]
  rloop doctor [--url ws://localhost:PORT] [--json]

Connection and observation:
  rloop status|ping [--url ws://localhost:PORT] [--json]
  rloop hierarchy [--depth 2] [--include-components] [--json]
  rloop find (--name TEXT [--exact] | --component TYPE) [--depth 8] [--json]
  rloop inspect SLOT [--depth 1] [--members] [--json]

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
  rloop validate FILE.json [--strict]
  rloop plan FILE.json [--state FILE] [--adopt]
  rloop apply FILE.json [--state FILE] [--adopt] [--profile] [--ndjson-progress]

ProtoFlux (Flux-SDK):
  rloop flux status
  rloop flux check|build|watch FILE.pg [--project DIR] [--out FILE] [--library-path DIR]
  rloop flux deploy --project DIR --module MODULE_PATH [--parent SLOT] [--library-path DIR]

Diagnostics:
  rloop doctor
  rloop logs [--path FILE_OR_DIRECTORY] [--tail 200]

Global options: --url, --timeout SECONDS, --command-timeout SECONDS, --json, --verbose
Configuration priority: CLI > environment > .rloop.json > ~/.rloop/config.json
Environment: RESONITE_LINK_URL, RLOOP_TIMEOUT_SECONDS, RLOOP_COMMAND_TIMEOUT_SECONDS, RESONITE_MANAGED_DATA_PATH, RESONITE_LOG_PATH
""");
}
