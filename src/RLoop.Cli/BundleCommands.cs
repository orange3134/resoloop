using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Cli;

public static partial class Program
{
    private static async Task<int> RunBundle(ParsedArguments args, OutputWriter output, RLoopConfig config, CancellationToken ct)
    {
        var operation = args.Positional(1, "bundle operation");
        var path = args.Positional(2, "bundle directory or manifest.json");
        if (args.Positionals.Count != 3)
            throw new RLoopException("UNEXPECTED_ARGUMENT", "bundle takes an operation and a bundle directory/manifest.", ExitCodes.InvalidArguments);
        if (operation is not ("summary" or "tree" or "find" or "inspect" or "refs" or "extract" or "collect-dependencies"))
            throw UnknownCommand("bundle " + operation);
        var bundle = BundleAnalysis.Load(path, args.Option("dependencies"));
        var depth = args.IntOption("depth", operation == "tree" ? 3 : 2, 0, 16);
        IReadOnlyList<MemberPathStep>? member = null;
        if (args.Option("member") is { } name) member = [new(Member: name)];
        if (args.Option("member-path") is { } memberText)
        {
            if (member is not null) throw new RLoopException("INVALID_OPTION", "Choose --member or --member-path.", ExitCodes.InvalidArguments);
            member = JsonSerializer.Deserialize<MemberPathStep[]>(memberText, BundleAnalysis.Json);
            ExistingReferences.ValidatePath(member);
        }
        var maxBytes = args.IntOption("max-bytes", operation is "extract" or "collect-dependencies" ? 16 * 1024 * 1024 : 65536,
            operation == "collect-dependencies" ? 4096 : 1024, operation is "extract" or "collect-dependencies" ? 256 * 1024 * 1024 : 16 * 1024 * 1024);
        switch (operation)
        {
            case "summary": output.Success(bundle.Summary()); break;
            case "extract":
                output.Success(bundle.Extract(args.RequireOption("under"), args.Has("include-dependencies"), depth,
                    args.IntOption("max-objects", 2000, 1, 10000), maxBytes, args.RequireOption("output")));
                break;
            case "collect-dependencies":
                var destination = args.Option("output") ?? Path.Combine(bundle.DirectoryPath, "dependencies.capture.json");
                if (File.Exists(destination)) throw new RLoopException("BUNDLE_OUTPUT_EXISTS", "Choose a new --output file; observations are never overwritten.", ExitCodes.InvalidArguments);
                var limits = new DependencyLimits(args.IntOption("depth", 3, 1, 16), args.IntOption("max-objects", 2000, 1, 10000),
                    maxBytes, args.IntOption("seconds", 120, 1, 3600));
                var uri = await ResolveConnectionUrlAsync(args, config, ct);
                await using (var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(config.TimeoutSeconds), ReflectionCacheFrom(args)))
                {
                    await client.ConnectAsync(uri, TimeSpan.FromSeconds(config.TimeoutSeconds), ct);
                    var captured = await DependencyCollector.CollectAsync(bundle, client, destination, limits, ct);
                    output.Success(new { output = Path.GetFullPath(destination), captured.SnapshotSha256, captured.StartedAt, captured.CompletedAt,
                        captured.StopReason, captured.Limits, records = captured.Records.Count,
                        statuses = captured.Records.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()), captured.Metrics });
                }
                break;
            default:
                var rows = operation switch
                {
                    "tree" => bundle.Tree(args.Option("under"), depth),
                    "find" => bundle.Find(args.Option("name"), args.Option("type"), args.Option("text"), args.Option("under")),
                    "inspect" => bundle.Inspect(args.RequireOption("key"), member),
                    "refs" => bundle.References(args.RequireOption("key"), args.Option("direction") ?? "both", depth, member),
                    _ => throw UnknownCommand(operation)
                };
                output.Success(bundle.Page(rows, args.IntOption("offset", 0, 0), args.IntOption("limit", 50, 1, 2000), maxBytes));
                break;
        }
        return ExitCodes.Success;
    }
}
