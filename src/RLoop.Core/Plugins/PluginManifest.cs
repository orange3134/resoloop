using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

public sealed record PluginCommand(string Name, string Description, string Program, IReadOnlyList<string> Args, int? TimeoutSeconds);
public sealed record PluginSkill(string Name, string Path);
public sealed record PluginRecipe(string Name, string Path, string Prototype, string Purpose,
    IReadOnlyDictionary<string, string> Parameters, IReadOnlyDictionary<string, string> Ports, IReadOnlyList<string> Requirements);
public sealed record PluginTool(string Name, string Executable, IReadOnlyList<string> VersionArgs, bool Required, string? Hint);
public sealed record PluginCompatibility(string MinVersion, string? MaxVersionExclusive);
public sealed record PluginManifest(int SchemaVersion, string Name, string Version, PluginCompatibility Resoloop,
    string? Description, IReadOnlyList<PluginCommand> Commands, IReadOnlyList<PluginSkill> Skills,
    IReadOnlyList<PluginRecipe> Recipes, IReadOnlyList<PluginTool> Tools)
{
    private const string Invalid = "PLUGIN_MANIFEST_INVALID";
    private static readonly Regex CommandPattern = new(@"\A[A-Za-z0-9._+-]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex SubstitutionPattern = new(@"\$\{([^}]*)\}", RegexOptions.CultureInvariant);

    public static PluginManifest Read(string packageDirectory)
    {
        try
        {
            PluginPathPolicy.EnsureNoLinks(packageDirectory, Invalid);
            var file = PluginPathPolicy.Combine(packageDirectory, "resoloop-plugin.json", Invalid);
            if (!File.Exists(file)) throw PluginErrors.Create("PLUGIN_MANIFEST_NOT_FOUND", "Package has no resoloop-plugin.json.");
            using var document = PluginJson.Read(file, 256 * 1024, Invalid);
            var reader = new ManifestReader();
            var manifest = reader.Parse(document.RootElement);
            reader.ThrowIfInvalid();
            // No referenced filesystem path is used until every declaration has passed lexical validation.
            reader.CheckReferences(manifest, packageDirectory);
            reader.ThrowIfInvalid();
            return manifest;
        }
        catch (RLoopException ex) when (ex.Code == Invalid && !ex.Context.ContainsKey("errors"))
        {
            throw PluginErrors.Create(Invalid, "Plugin manifest validation failed.",
                new Dictionary<string, object?> { ["errors"] = Array.AsReadOnly(new[] { "$: " + ex.Message }) }, innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PluginErrors.Create(Invalid, "Could not read plugin manifest.", innerException: ex);
        }
    }

    public void EnsureCompatible(string resoloopVersion)
    {
        try
        {
            var current = SemanticVersion.Parse(resoloopVersion);
            if (current.CompareTo(SemanticVersion.Parse(Resoloop.MinVersion)) >= 0
                && (Resoloop.MaxVersionExclusive is null || current.CompareTo(SemanticVersion.Parse(Resoloop.MaxVersionExclusive)) < 0)) return;
        }
        catch (FormatException ex)
        {
            throw PluginErrors.Create("PLUGIN_INCOMPATIBLE_VERSION", "Invalid ResoLoop compatibility version.", innerException: ex);
        }
        throw PluginErrors.Create("PLUGIN_INCOMPATIBLE_VERSION", "Plugin does not support this ResoLoop version.");
    }

    private sealed class ManifestReader
    {
        private readonly List<string> _errors = [];
        private void Error(string path, string message) => _errors.Add(path + ": " + message);

        public PluginManifest Parse(JsonElement root)
        {
            PluginJson.FindDuplicateProperties(root, "$", _errors);
            Object(root, "$", ["schemaVersion", "name", "version", "resoloop", "description", "commands", "skills", "recipes", "tools"]);
            var schema = Integer(root, "schemaVersion", "$", required: true) ?? 0;
            var name = Name(root, "$");
            var version = Version(Text(root, "version", "$", required: true), "$.version");
            var compatibility = Property(root, "resoloop");
            Object(compatibility, "$.resoloop", ["minVersion", "maxVersionExclusive"]);
            var min = Version(Text(compatibility, "minVersion", "$.resoloop", required: true), "$.resoloop.minVersion");
            var maxText = Text(compatibility, "maxVersionExclusive", "$.resoloop");
            var max = maxText is null ? null : Version(maxText, "$.resoloop.maxVersionExclusive");
            if (TryVersion(min) is { } minimum && max is not null && TryVersion(max) is { } maximum && minimum.CompareTo(maximum) >= 0)
                Error("$.resoloop.maxVersionExclusive", "Must be greater than minVersion.");
            var description = Text(root, "description", "$", max: 500);
            var commands = Items(root, "commands", 50, Command);
            var skills = Items(root, "skills", 50, (item, path) =>
            {
                Object(item, path, ["name", "path"]);
                return new PluginSkill(Name(item, path), Relative(item, path));
            });
            var recipes = Items(root, "recipes", 50, Recipe);
            var tools = Items(root, "tools", 20, Tool);
            if (schema != 1)
            {
                // Structural problems remain collected instead of being hidden by the schema error.
                ThrowIfInvalid();
                throw PluginErrors.Create("PLUGIN_SCHEMA_UNSUPPORTED", "Unsupported plugin manifest schemaVersion.");
            }
            return new(schema, name, version, new(min, max), description, commands, skills, recipes, tools);
        }

        private PluginCommand Command(JsonElement item, string path)
        {
            Object(item, path, ["name", "description", "program", "args", "timeoutSeconds"]);
            var name = Name(item, path);
            var description = Text(item, "description", path, required: true, max: 500) ?? "";
            var program = Text(item, "program", path, required: true) ?? "";
            if (program.StartsWith("./", StringComparison.Ordinal)) ValidateRelative(program[2..], path + ".program");
            else if (!CommandPattern.IsMatch(program)) Error(path + ".program", "Expected a command name or ./package-relative-path.");
            var args = Strings(item, "args", path);
            foreach (var arg in args)
            {
                // Unclosed expressions are invalid too, rather than being handed to a later expander.
                var replaced = SubstitutionPattern.Replace(arg, match =>
                {
                    if (match.Groups[1].Value is not ("pluginRoot" or "pluginData")) Error(path + ".args", "Unsupported variable substitution.");
                    return "";
                });
                if (replaced.Contains("${", StringComparison.Ordinal)) Error(path + ".args", "Unclosed variable substitution.");
            }
            var timeout = Integer(item, "timeoutSeconds", path);
            if (timeout is < 1 or > 86400) Error(path + ".timeoutSeconds", "Expected a value from 1 to 86400.");
            return new(name, description, program, args, timeout);
        }

        private PluginRecipe Recipe(JsonElement item, string path)
        {
            Object(item, path, ["name", "path", "prototype", "purpose", "parameters", "ports", "requirements"]);
            var parameters = Dictionary(item, "parameters", path, required: true);
            if (!parameters.ContainsKey("key")) Error(path + ".parameters.key", "A stable key parameter is required.");
            return new(Name(item, path), Relative(item, path), Text(item, "prototype", path, required: true, max: 100) ?? "",
                Text(item, "purpose", path, required: true, max: 500) ?? "", parameters,
                Dictionary(item, "ports", path), Strings(item, "requirements", path));
        }

        private PluginTool Tool(JsonElement item, string path)
        {
            Object(item, path, ["name", "executable", "versionArgs", "required", "hint"]);
            var name = Name(item, path);
            var executable = Text(item, "executable", path, required: true) ?? "";
            if (!CommandPattern.IsMatch(executable)) Error(path + ".executable", "Expected a command name without separators.");
            var required = true;
            if (Has(item, "required"))
            {
                var value = Property(item, "required");
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) required = value.GetBoolean();
                else Error(path + ".required", "Expected a boolean.");
            }
            return new(name, executable, Strings(item, "versionArgs", path), required, Text(item, "hint", path, max: 500));
        }

        private IReadOnlyList<T> Items<T>(JsonElement root, string key, int maximum, Func<JsonElement, string, T> parse)
        {
            if (!Has(root, key)) return Array.AsReadOnly(Array.Empty<T>());
            var array = Property(root, key);
            if (array.ValueKind != JsonValueKind.Array) { Error("$." + key, "Expected an array."); return []; }
            if (array.GetArrayLength() > maximum) Error("$." + key, $"At most {maximum} items are allowed.");
            var result = new List<T>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var path = $"$.{key}[{index++}]";
                result.Add(parse(item, path));
                var name = Property(item, "name");
                if (name.ValueKind == JsonValueKind.String && !names.Add(name.GetString()!)) Error(path + ".name", "Duplicate name.");
            }
            return result.AsReadOnly();
        }

        private void Object(JsonElement value, string path, string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) { Error(path, "Expected an object."); return; }
            foreach (var property in value.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal)) Error(path + "." + property.Name, "Unknown property.");
        }

        private string Name(JsonElement item, string path)
        {
            var name = Text(item, "name", path, required: true) ?? "";
            if (!PluginNames.IsValid(name)) Error(path + ".name", "Expected a lowercase plugin name of 2 to 40 characters.");
            return name;
        }

        private string Relative(JsonElement item, string path)
        {
            var relative = Text(item, "path", path, required: true) ?? "";
            ValidateRelative(relative, path + ".path");
            return relative;
        }

        private void ValidateRelative(string relative, string path)
        {
            try { PluginPathPolicy.ValidateRelativePath(relative, Invalid); }
            catch (RLoopException ex) { Error(path, ex.Message); }
        }

        private string Version(string? value, string path)
        {
            if (value is null || TryVersion(value) is null) Error(path, "Expected a SemVer 2.0 version.");
            return value ?? "";
        }

        private static SemanticVersion? TryVersion(string value)
        {
            try { return SemanticVersion.Parse(value); }
            catch (FormatException) { return null; }
        }

        private string? Text(JsonElement parent, string key, string path, bool required = false, int max = int.MaxValue)
        {
            if (!Has(parent, key)) { if (required) Error(path + "." + key, "Required property."); return null; }
            var item = Property(parent, key);
            if (item.ValueKind != JsonValueKind.String) { Error(path + "." + key, "Expected a string."); return null; }
            var text = item.GetString()!;
            if (text.Length < 1 || text.Length > max) Error(path + "." + key, $"Expected 1 to {max} characters.");
            return text;
        }

        private int? Integer(JsonElement parent, string key, string path, bool required = false)
        {
            if (!Has(parent, key)) { if (required) Error(path + "." + key, "Required property."); return null; }
            var item = Property(parent, key);
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value)) return value;
            Error(path + "." + key, "Expected an integer.");
            return null;
        }

        private IReadOnlyList<string> Strings(JsonElement parent, string key, string path)
        {
            if (!Has(parent, key)) return Array.AsReadOnly(Array.Empty<string>());
            var array = Property(parent, key);
            if (array.ValueKind != JsonValueKind.Array) { Error(path + "." + key, "Expected a string array."); return []; }
            var values = new List<string>();
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) values.Add(item.GetString()!);
                else Error($"{path}.{key}[{index}]", "Expected a string.");
                index++;
            }
            return values.AsReadOnly();
        }

        private IReadOnlyDictionary<string, string> Dictionary(JsonElement parent, string key, string path, bool required = false)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Has(parent, key)) { if (required) Error(path + "." + key, "Required property."); }
            else
            {
                var item = Property(parent, key);
                if (item.ValueKind != JsonValueKind.Object) Error(path + "." + key, "Expected a string dictionary.");
                else foreach (var property in item.EnumerateObject())
                {
                    if (property.Name.Length == 0) Error(path + "." + key, "Dictionary keys must not be empty.");
                    if (property.Value.ValueKind != JsonValueKind.String) Error(path + "." + key + "." + property.Name, "Expected a string.");
                    else result.TryAdd(property.Name, property.Value.GetString()!);
                }
            }
            return new ReadOnlyDictionary<string, string>(result);
        }

        private static bool Has(JsonElement parent, string key) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out _);
        private static JsonElement Property(JsonElement parent, string key) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var result) ? result : default;

        public void CheckReferences(PluginManifest manifest, string root)
        {
            Check("$", () => PluginPathPolicy.EnumerateFiles(root, Invalid));
            for (var i = 0; i < manifest.Commands.Count; i++)
            {
                var command = manifest.Commands[i];
                if (command.Program.StartsWith("./", StringComparison.Ordinal))
                    Check($"$.commands[{i}].program", () => RequireFile(root, command.Program[2..]));
            }
            for (var i = 0; i < manifest.Skills.Count; i++)
            {
                var skill = manifest.Skills[i];
                Check($"$.skills[{i}].path", () => RequireFile(root, skill.Path + "/SKILL.md"));
            }
            for (var i = 0; i < manifest.Recipes.Count; i++)
            {
                var recipe = manifest.Recipes[i];
                Check($"$.recipes[{i}].path", () =>
                {
                    using var json = PluginJson.Read(RequireFile(root, recipe.Path), 200L * 1024 * 1024, Invalid);
                    var prototypes = Property(json.RootElement, "prototypes");
                    if (Property(prototypes, recipe.Prototype).ValueKind != JsonValueKind.Object)
                        Error($"$.recipes[{i}].prototype", "Referenced recipe prototype is not an object.");
                });
            }
        }

        private static string RequireFile(string root, string relative)
        {
            var path = PluginPathPolicy.Combine(root, relative, Invalid);
            if (!File.Exists(path)) throw PluginErrors.Create(Invalid, "Referenced package file is missing.");
            return path;
        }

        private void Check(string path, Action action)
        {
            try { action(); }
            catch (RLoopException ex) { Error(path, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(path, "Could not read referenced package content."); }
        }

        public void ThrowIfInvalid()
        {
            if (_errors.Count != 0)
                throw PluginErrors.Create(Invalid, "Plugin manifest validation failed.", new Dictionary<string, object?> { ["errors"] = _errors.AsReadOnly() });
        }
    }
}

/// <summary>Bounded UTF-8 JSON reader shared with plugin install records.</summary>
internal static class PluginJson
{
    public static JsonDocument Read(string path, long maximumBytes, string code)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) throw PluginErrors.Create(code, "Plugin JSON exceeds the size limit.");
            // Bound the read even if a non-cooperating writer grows the file after the length check.
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = stream.Read(block)) != 0)
            {
                if (buffer.Length + count > maximumBytes) throw PluginErrors.Create(code, "Plugin JSON exceeds the size limit.");
                buffer.Write(block, 0, count);
            }
            var bytes = buffer.ToArray();
            var start = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            _ = new UTF8Encoding(false, true).GetCharCount(bytes.AsSpan(start));
            var document = JsonDocument.Parse(bytes.AsMemory(start));
            try { ValidateStrings(document.RootElement, "$", code); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (DecoderFallbackException ex)
        {
            throw PluginErrors.Create(code, "Plugin JSON must be UTF-8.", new Dictionary<string, object?>
            {
                ["errors"] = Array.AsReadOnly(new[] { "$: Invalid UTF-8." })
            }, innerException: ex);
        }
        catch (JsonException ex)
        {
            throw PluginErrors.Create(code, "Invalid plugin JSON.", new Dictionary<string, object?>
            {
                ["errors"] = Array.AsReadOnly(new[] { (ex.Path ?? "$") + ": Invalid JSON." })
            }, innerException: ex);
        }
    }

    private static void ValidateStrings(JsonElement element, string path, string code)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String) _ = element.GetString();
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) ValidateStrings(property.Value, path + "." + property.Name, code);
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray()) ValidateStrings(item, $"{path}[{index++}]", code);
            }
        }
        catch (InvalidOperationException ex)
        {
            throw PluginErrors.Create(code, "Invalid Unicode in plugin JSON.", new Dictionary<string, object?>
            {
                ["errors"] = Array.AsReadOnly(new[] { path + ": Invalid Unicode string." })
            }, innerException: ex);
        }
    }

    public static void FindDuplicateProperties(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var child = path + "." + property.Name;
                if (!names.Add(property.Name)) errors.Add(child + ": Duplicate property.");
                FindDuplicateProperties(property.Value, child, errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray()) FindDuplicateProperties(item, $"{path}[{i++}]", errors);
        }
    }
}
