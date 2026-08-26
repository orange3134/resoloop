using RLoop.Core;

namespace RLoop.Cli;

public sealed class ParsedArguments
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "verbose", "help", "exact", "include-components", "members", "yes", "full-errors",
        "strict", "adopt", "profile", "ndjson-progress", "quiet"
    };
    private readonly Dictionary<string, List<string>> _options;

    private ParsedArguments(IReadOnlyList<string> positionals, Dictionary<string, List<string>> options)
    {
        Positionals = positionals;
        _options = options;
    }

    public IReadOnlyList<string> Positionals { get; }
    public bool Has(string name) => _options.ContainsKey(name);
    public string? Option(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;
    public IReadOnlyList<string> Options(string name) => _options.TryGetValue(name, out var values) ? values : [];

    public int IntOption(string name, int defaultValue, int min = int.MinValue, int max = int.MaxValue)
    {
        var text = Option(name);
        if (text is null) return defaultValue;
        if (!int.TryParse(text, out var value) || value < min || value > max)
            throw new RLoopException("INVALID_OPTION", $"--{name} must be an integer between {min} and {max}; got '{text}'.", ExitCodes.InvalidArguments);
        return value;
    }

    public string RequireOption(string name)
    {
        var value = Option(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new RLoopException("OPTION_REQUIRED", $"--{name} is required.", ExitCodes.InvalidArguments);
        return value;
    }

    public string Positional(int index, string description)
    {
        if (Positionals.Count <= index)
            throw new RLoopException("ARGUMENT_REQUIRED", $"{description} is required.", ExitCodes.InvalidArguments);
        return Positionals[index];
    }

    public static ParsedArguments Parse(IReadOnlyList<string> args)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }
            var body = token[2..];
            if (string.IsNullOrWhiteSpace(body))
                throw new RLoopException("INVALID_ARGUMENT", "A bare '--' is not supported.", ExitCodes.InvalidArguments);
            var equals = body.IndexOf('=');
            var name = equals >= 0 ? body[..equals] : body;
            string value;
            if (equals >= 0) value = body[(equals + 1)..];
            else if (!BooleanOptions.Contains(name) && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            else value = "true";
            if (!options.TryGetValue(name, out var values)) options[name] = values = [];
            values.Add(value);
        }
        return new ParsedArguments(positionals, options);
    }
}
