using System.Text.Json;
using System.Text.Json.Serialization;
using RLoop.Core;

namespace RLoop.Cli;

public sealed class OutputWriter(bool json)
{
    private static readonly JsonSerializerOptions Compact = CreateOptions(false);
    private static readonly JsonSerializerOptions Indented = CreateOptions(true);

    public void Success(object? data, Action<TextWriter>? human = null)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, data }, Compact));
            return;
        }
        if (human is not null) human(Console.Out);
        else Console.Out.WriteLine(JsonSerializer.Serialize(data, Indented));
    }

    public void Error(RLoopException error)
    {
        var payload = new
        {
            ok = false,
            error = new { code = error.Code, message = error.Message, context = error.Context, suggestions = error.Suggestions }
        };
        if (json) Console.Error.WriteLine(JsonSerializer.Serialize(payload, Compact));
        else
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
            foreach (var suggestion in error.Suggestions) Console.Error.WriteLine($"  next: {suggestion}");
        }
    }

    public void Progress(ApplyProgress progress, bool ndjson)
    {
        if (ndjson)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { eventType = "progress", data = progress }, Compact));
            return;
        }
        var amount = progress.Total > 0 ? $"[{progress.Current}/{progress.Total}] " : string.Empty;
        var target = string.IsNullOrWhiteSpace(progress.Path) ? string.Empty : $" {progress.Path}";
        Console.Error.WriteLine($"{amount}{progress.Stage}: {progress.Message}{target}");
    }

    public static void Hierarchy(TextWriter writer, SlotInfo root)
    {
        void Walk(SlotInfo slot, string prefix, bool last)
        {
            writer.Write(prefix);
            if (prefix.Length > 0) writer.Write(last ? "└─ " : "├─ ");
            writer.WriteLine($"{slot.Name} [{slot.Id}]" + (slot.Components.Count > 0 ? $" ({slot.Components.Count} components)" : string.Empty));
            for (var i = 0; i < slot.Children.Count; i++)
                Walk(slot.Children[i], prefix + (prefix.Length == 0 ? string.Empty : last ? "   " : "│  "), i == slot.Children.Count - 1);
        }
        Walk(root, string.Empty, true);
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = indented
    };
}
