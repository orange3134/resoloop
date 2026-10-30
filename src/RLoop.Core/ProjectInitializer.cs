using System.Text;
using System.Text.Json;
using RLoop.Core.Plugins;

namespace RLoop.Core;

/// <summary>Created and Unchanged list base files only; plugin skills are reported in Plugins.</summary>
public sealed record ProjectInitResult(
    string RootDirectory,
    string ProjectName,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> NextSteps,
    PluginSkillSyncResult? Plugins = null);

public static class ProjectInitializer
{
    public static ProjectInitResult Initialize(string targetDirectory) => Initialize(targetDirectory, plugins: null);

    /// <summary>
    /// Also creates the missing skill files of the verified plugins installed in exactly this project. Like the
    /// base files, an existing plugin skill file is never overwritten; updates belong to skills sync --update.
    /// </summary>
    public static ProjectInitResult Initialize(string targetDirectory, string resoloopVersion, PluginNameReservations reservations)
    {
        using var plugins = PluginSkillManager.Open(targetDirectory, resoloopVersion, reservations);
        return Initialize(targetDirectory, plugins);
    }

    private static ProjectInitResult Initialize(string targetDirectory, PluginSkillPlan? plugins)
    {
        var root = Path.GetFullPath(targetDirectory);
        var projectName = new DirectoryInfo(root).Name;
        if (string.IsNullOrWhiteSpace(projectName)) projectName = "Project";

        var slotName = "ResoLoop_Test_" + ToSafeName(projectName);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigResolver.ProjectFileName] = """
                {
                  "timeoutSeconds": 30,
                  "commandTimeoutSeconds": 900,
                  "fluxExecutable": "flux-sdk"
                }
                """ + "\n",
            [Path.Combine("content", "main.json")] = JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                ownership = new { key = ToSafeName(projectName).ToLowerInvariant() },
                slot = new
                {
                    key = "root",
                    name = slotName,
                    parent = "Root",
                    position = new[] { 0f, 1.5f, 2f },
                    scale = new[] { 1f, 1f, 1f }
                },
                components = new[]
                {
                    new
                    {
                        type = "FrooxEngine.Grabbable",
                        fields = new Dictionary<string, object?> { ["Scalable"] = true }
                    }
                },
                cameras = new Dictionary<string, object?>
                {
                    ["main"] = AuthoringSchema.CameraExample() with { Position = [0, 2.5f, -6], Target = [0, 1.5f, 2], Representative = true }
                },
                tests = new[]
                {
                    new { name = "root-exists", assertions = new[] { new { target = "$slot:root", exists = true } } }
                }
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n",
            [Path.Combine("flux", "Main.pg")] = """
                module Main
                where {
                    "Hello from resoloop!"->display
                }
                """ + "\n",
            [Path.Combine("flux", "protograph.toml")] = """
                name = "resoloop-project"
                version = "0.1.0"

                [build.dev]
                optimization_preset = "dev"

                [dependencies]
                """ + "\n",
            [Path.Combine("flux", "resoloop.flux.json")] = JsonSerializer.Serialize(new
            {
                schemaVersion = "1",
                projectDirectory = ".",
                parent = "$slot:root",
                worldState = "../.resoloop/state/" + ToSafeName(projectName).ToLowerInvariant() + ".json",
                deployState = "../.resoloop/flux-state/main.json",
                modules = new[] { new { name = "main", source = "Main.pg", module = "Main" } }
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n",
            [Path.Combine("flux", ".gitignore")] = """
                build/
                .protograph/
                *.brson
                """ + "\n",
            [Path.Combine(".resoloop", ".gitignore")] = "state/\nflux-state/\n"
        };

        var agentsPath = Path.Combine(root, "AGENTS.md");
        if (!File.Exists(agentsPath))
            files["AGENTS.md"] = """
                # ResoLoop project guide

                Use `resoloop` as the only mutation interface for Resonite content in this project. Use the project skills under `.agents/skills/` for the requested workflow. If no connection URL is configured, follow resonite-build's automatic discovery workflow: discover first, connect if there is one candidate, or ask the user to choose if there are multiple. Run `resoloop doctor --json` with the selected URL before world work.

                ## Required workflow

                - Inspect unfamiliar Component types, members, methods, and Flux nodes through Reflection or Flux-SDK metadata. Never guess runtime names.
                - Before mutation, review `diff --brief` targets and reasons. Diff already includes offline and runtime Reflection validation; standalone validate is for offline authoring or diagnosis. Apply repeats preflight. Re-run the same apply and confirm it converges without writes.
                - Keep declarative content in `content/`, ProtoGraph source in `flux/`, and session state under `.resoloop/`.
                - Prefer stable selectors such as `$slot:key`, `$component:key`, and `$member:key.Member`; pass `--state` after reconnecting.

                ## Resonite safety

                - Never delete or mutate `Root`, an unverified ID, or existing user content outside the requested ownership boundary.
                - Put experiments below an unmistakable `ResoLoop_Test_*` Slot and clean only its exact verified ID.
                - Review delete candidates before `--prune --yes`. Slot delete and Component remove always require explicit `--yes`.
                - Treat ResoniteLink IDs as session-scoped and re-observe after a session restart.
                """ + "\n";

        var skillHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in BundledSkillManager.Files)
        {
            var content = BundledSkillManager.LoadBundledFile(file);
            files[Path.Combine(".agents", "skills", file.Replace('/', Path.DirectorySeparatorChar))] = content;
            skillHashes[file] = BundledSkillManager.Hash(content);
        }
        files[BundledSkillManager.LockRelativePath.Replace('/', Path.DirectorySeparatorChar)] =
            BundledSkillManager.SerializeLock(skillHashes);

        var conflicts = new List<string>();
        var unchanged = new List<string>();
        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(root, relativePath);
            if (!File.Exists(path)) continue;
            if (ContentEquals(File.ReadAllText(path), content)) unchanged.Add(relativePath.Replace('\\', '/'));
            else conflicts.Add(relativePath.Replace('\\', '/'));
        }

        var pluginConflicts = plugins?.Conflicts(init: true) ?? [];
        conflicts.AddRange(pluginConflicts.Select(conflict => ".agents/skills/" + conflict.Path));
        if (conflicts.Count > 0)
        {
            var context = new Dictionary<string, object?> { ["rootDirectory"] = root, ["conflicts"] = conflicts };
            if (pluginConflicts.Count > 0) context["pluginConflicts"] = pluginConflicts;
            throw new RLoopException(
                "INIT_FILE_EXISTS",
                "Project initialization would overwrite existing files.",
                ExitCodes.ValidationFailed,
                context,
                ["Move or reconcile the listed files, then run resoloop init again. Existing files are never overwritten."]);
        }

        var created = new List<string>();
        Directory.CreateDirectory(root);
        foreach (var (relativePath, content) in files)
        {
            var normalized = relativePath.Replace('\\', '/');
            if (unchanged.Contains(normalized, StringComparer.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, NormalizeLineEndings(content), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            created.Add(normalized);
        }
        var pluginUpdated = plugins?.Apply(init: true) ?? [];

        return new ProjectInitResult(root, projectName, created, unchanged,
        [
            "If no URL is configured, run resoloop discover --json; connect to one candidate or ask the user to choose among multiple, following resonite-build.",
            "Run resoloop doctor, then diff --brief and apply content/main.json; diff includes runtime validation.",
            "Restart Codex if it does not detect the project skills under .agents/skills immediately.",
            "Capture the live world with resoloop capture content/main.json --camera main --output artifacts/main.jpg --json; use .svg for offline projection.",
            "For ProtoFlux, set RESONITE_MANAGED_DATA_PATH and use flux/resoloop.flux.json."
        ], plugins?.Result("init", pluginUpdated, applied: true));
    }

    private static string ToSafeName(string name)
    {
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "Project" : safe;
    }

    private static bool ContentEquals(string left, string right) =>
        NormalizeLineEndings(left) == NormalizeLineEndings(right);

    private static string NormalizeLineEndings(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
}
