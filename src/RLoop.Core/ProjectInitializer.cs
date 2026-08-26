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

        var slotName = "RLoop_Test_" + ToSafeName(projectName);
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
                }
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n",
            [Path.Combine("flux", "Main.pg")] = """
                module Main
                where {
                    "Hello from rloop!"->display
                }
                """ + "\n",
            [Path.Combine("flux", ".gitignore")] = """
                build/
                .protograph/
                *.brson
                """ + "\n",
            [Path.Combine(".rloop", ".gitignore")] = "state/\n"
        };

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
                ["Move or reconcile the listed files, then run rloop init again. Existing files are never overwritten."]);

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
            "Run rloop doctor, then rloop apply content/main.json --json.",
            "For ProtoFlux, set RESONITE_MANAGED_DATA_PATH and run rloop flux check flux/Main.pg --project flux --json."
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
