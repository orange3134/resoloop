using System.Text;
using System.Text.Json;

namespace RLoop.Core;

public sealed record ProjectInitResult(
    string RootDirectory,
    string ProjectName,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> NextSteps);

public static class ProjectInitializer
{
    public static ProjectInitResult Initialize(string targetDirectory)
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
                    ["main"] = new { position = new[] { 0f, 2.5f, -6f }, target = new[] { 0f, 1.5f, 2f }, fieldOfView = 60, width = 1280, height = 720, representative = true }
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

                Use `resoloop` as the only mutation interface for Resonite content in this project. Start with `resoloop doctor --json`, then use the project skills under `.agents/skills/` for the requested workflow.

                ## Required workflow

                - Inspect unfamiliar Component types, members, methods, and Flux nodes through Reflection or Flux-SDK metadata. Never guess runtime names.
                - Before mutation, run offline validation, strict validation when connected, and plan/diff. Re-run the same apply and confirm it converges without writes.
                - Keep declarative content in `content/`, ProtoGraph source in `flux/`, and session state under `.resoloop/`.
                - Prefer stable selectors such as `$slot:key`, `$component:key`, and `$member:key.Member`; pass `--state` after reconnecting.

                ## Resonite safety

                - Never delete or mutate `Root`, an unverified ID, or existing user content outside the requested ownership boundary.
                - Put experiments below an unmistakable `ResoLoop_Test_*` Slot and clean only its exact verified ID.
                - Review delete candidates before `--prune --yes`. Slot delete and Component remove always require explicit `--yes`.
                - Treat ResoniteLink IDs as session-scoped and re-observe after a session restart.
                """ + "\n";

        var skillHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var skillName in BundledSkillManager.Names)
        {
            var content = BundledSkillManager.LoadBundledSkill(skillName);
            files[Path.Combine(".agents", "skills", skillName, "SKILL.md")] = content;
            skillHashes[skillName] = BundledSkillManager.Hash(content);
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

        if (conflicts.Count > 0)
            throw new RLoopException(
                "INIT_FILE_EXISTS",
                "Project initialization would overwrite existing files.",
                ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["rootDirectory"] = root, ["conflicts"] = conflicts },
                ["Move or reconcile the listed files, then run resoloop init again. Existing files are never overwritten."]);

        var created = new List<string>();
        Directory.CreateDirectory(root);
        foreach (var (relativePath, content) in files)
        {
            var normalized = relativePath.Replace('\\', '/');
            if (unchanged.Contains(normalized, StringComparer.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            created.Add(normalized);
        }

        return new ProjectInitResult(root, projectName, created, unchanged,
        [
            "Set RESONITE_LINK_URL to the current ResoniteLink WebSocket URL.",
            "Run resoloop doctor, then validate, diff, and apply content/main.json.",
            "Restart Codex if it does not detect the project skills under .agents/skills immediately.",
            "Generate scene artifacts with resoloop capture content/main.json --camera main --output artifacts/main.svg --json.",
            "For ProtoFlux, set RESONITE_MANAGED_DATA_PATH and use flux/resoloop.flux.json."
        ]);
    }

    private static string ToSafeName(string name)
    {
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "Project" : safe;
    }

    private static bool ContentEquals(string left, string right) =>
        left.Replace("\r\n", "\n", StringComparison.Ordinal) == right.Replace("\r\n", "\n", StringComparison.Ordinal);
}
