using RLoop.Core.Plugins;

namespace RLoop.Core;

public sealed record UixRecipe(string Name, string Prototype, string Purpose,
    IReadOnlyDictionary<string, string> Parameters, IReadOnlyDictionary<string, string> Ports,
    IReadOnlyList<string> Requirements);

/// <summary>Portable authoring assets; all expansion and runtime validation use the normal apply pipeline.</summary>
public static class UixRecipes
{
    public static IReadOnlyList<UixRecipe> Catalog { get; } =
    [
        new("button", "uix.button", "Native button semantics with caller-owned graphics and feedback.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix (also the default Slot name).", ["rect"] = "Caller-owned RectTransform fields object; {} leaves runtime defaults." },
            new Dictionary<string, string> { ["rect"] = "$component:<key>-rect", ["button"] = "$component:<key>-button", ["pressed"] = "$member:<key>-button.IsPressed", ["hovering"] = "$member:<key>-button.IsHovering" },
            ["Append a caller-designed interaction Graphic on this Slot or in its hit hierarchy; set its InteractionTarget explicitly.",
             "ColorDrivers is deliberately empty so the button does not own visual tint. Compose feedback separately.",
             "Append arbitrary visual children; keep the hit area stable when animating the inner face. No label, Image, material, size or color is supplied.",
             "Native click actions still need supported application wiring; field probes do not prove user input."]),
        new("boolean-state", "uix.boolean-state", "Connect any boolean field to a caller-selected typed field and two caller-defined values.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix.", ["valueType"] = "Reflection-verified generic argument, e.g. float2 or colorX.", ["source"] = "Boolean $member selector.", ["target"] = "Typed $member selector to drive.", ["off"] = "Caller-defined value when false.", ["on"] = "Caller-defined value when true." },
            new Dictionary<string, string> { ["driver"] = "$component:<key>-driver", ["copy"] = "$component:<key>-copy" },
            ["The target must have exactly one driver. Do not manage its driven value in fields; use initialFields only when needed.",
             "This is optional feedback/application binding, not a fixed pressed animation. Omit it for a button without visual feedback.",
             "Verify both transitions downstream with reversible probes. Keep logic Slots outside layout-controlled child lists."]),
        new("scroll-content", "uix.scroll-content", "Native scrollable content referencing a caller-owned viewport.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix.", ["rect"] = "Caller-owned content RectTransform fields object.", ["viewport"] = "$component selector for the caller's viewport RectTransform." },
            new Dictionary<string, string> { ["rect"] = "$component:<key>-rect", ["scroll"] = "$component:<key>-scroll", ["position"] = "$member:<key>-scroll.NormalizedPosition" },
            ["Caller owns viewport, Mask, interaction Graphic, content sizing/layout and visual children.",
             "Scroll position is runtime-owned and is not reset by the recipe. Test both endpoints with actual captures."]),
        new("value-state", "uix.value-state", "Shared typed state for toggles, exclusive choices and application bindings; initialized only on creation.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix.", ["valueType"] = "Reflected ValueField generic argument, e.g. bool or int.", ["initial"] = "Initial typed value; reapply preserves the current Value." },
            new Dictionary<string, string> { ["state"] = "$member:<key>-state.Value", ["provider"] = "$component:<key>-state" },
            ["Keep state in the saved ownership root, outside layout. Share one state among related controls.",
             "Do not also manage Value in fields or use multiple drivers on the state."]),
        new("text-input", "uix.text-input", "Native TextField, focus Button and TextEditor connected to caller-owned Text.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix.", ["rect"] = "Caller-owned hit rectangle fields.", ["text"] = "$component selector for the caller's UIX.Text." },
            new Dictionary<string, string> { ["rect"] = "$component:<key>-rect", ["button"] = "$component:<key>-button", ["input"] = "$component:<key>-input", ["editor"] = "$component:<key>-editor" },
            ["Provide an interaction Graphic and a separate styled UIX.Text, preferably below the input Slot. Put Content in that Text's initialFields so editing survives reapply.",
             "The recipe supplies no text, caret color, font, background or layout. Use native caret; do not add a static duplicate.",
             "Check Editor.Text, TextField.__text and Editor references; actual focus/typing remain manual checks. Field edits are not keyboard input."]),
        new("toggle", "uix.toggle", "Native ButtonToggle editing a shared caller-selected boolean field.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix.", ["rect"] = "Caller-owned hit rectangle fields.", ["state"] = "Boolean $member selector, often a value-state port." },
            new Dictionary<string, string> { ["rect"] = "$component:<key>-rect", ["button"] = "$component:<key>-button", ["toggle"] = "$component:<key>-toggle" },
            ["Add an interaction Graphic. Button.SendSlotEvents is enabled for native ButtonToggle handling; no SyncDelegate payload is needed.",
             "Bind state to any caller-designed feedback using boolean-state. State is not owned/reset by this control.",
             "Setting IsPressed does not simulate native button events. Field probes verify state/feedback, not click-to-toggle."]),
        new("choice", "uix.choice", "One native mutually-exclusive option; share a typed state among options, radios or tabs.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix.", ["rect"] = "Caller-owned hit rectangle fields.", ["valueType"] = "Reflected ValueRadio generic argument, e.g. int.", ["state"] = "Shared typed $member selector.", ["option"] = "This option's distinct typed value." },
            new Dictionary<string, string> { ["rect"] = "$component:<key>-rect", ["button"] = "$component:<key>-button", ["choice"] = "$component:<key>-choice", ["selected"] = "$member:<key>-selected.Value" },
            ["Use distinct option values with one state source. No matching option means none selected; duplicate values select multiple options.",
             "Selected is a derived boolean, driven by native CheckVisual. Do not write it or drive it from another component.",
             "Add an interaction Graphic. Bind selected to caller-defined visuals or a page Slot.IsActive for tabs. Keep controls/state outside pages they disable.",
             "Field probes can prove exclusive downstream state, not native click delivery."]),
        new("slider", "uix.slider", "Native float slider driving a caller-owned handle rectangle; current value survives reapply.",
            new Dictionary<string, string> { ["key"] = "Unique stable key prefix.", ["rect"] = "Caller-owned track/hit rectangle fields.", ["min"] = "Numeric range minimum.", ["max"] = "Numeric range maximum greater than min.", ["initial"] = "Initial float Value only on creation.", ["direction"] = "Reflected SlideDirection enum, observed Horizontal; verify other values in the runtime.", ["anchorOffset"] = "Caller-owned float2 travel offset; centered horizontal [0,0.5], vertical [0.5,0].", ["handle"] = "Stable component key of caller's handle RectTransform, WITHOUT $component: prefix." },
            new Dictionary<string, string> { ["rect"] = "$component:<key>-rect", ["slider"] = "$component:<key>-slider", ["value"] = "$member:<key>-slider.Value" },
            ["Provide track interaction Graphic and handle hierarchy/geometry. No extra Button is needed; Slider handles interaction.",
             "Handle AnchorMin/AnchorMax are driven. Do not manage them in fields or connect another driver; caller may specify handle OffsetMin/OffsetMax and visuals.",
             "Check minimum/midpoint/maximum and value preservation; real dragging is a separate manual check."])
    ];

    public static IReadOnlyList<UixRecipe> List(PluginRecipeResolver? resolver = null) => resolver is null
        ? Catalog : Array.AsReadOnly(Catalog.Concat(resolver.Catalog).ToArray());

    public static UixRecipe Describe(string name, PluginRecipeResolver? resolver) => name.Contains(':') && resolver is not null
        ? resolver.Describe(name) : Describe(name);

    public static string Read(string name, PluginRecipeResolver? resolver) => name.Contains(':') && resolver is not null
        ? resolver.Read(name) : Read(name);

    public static UixRecipe Describe(string name) => Catalog.FirstOrDefault(recipe => recipe.Name == name)
        ?? throw new RLoopException("UIX_RECIPE_NOT_FOUND", $"Unknown UIX recipe '{name}'.", ExitCodes.NotFound,
            suggestions: ["Run resoloop uix recipe list --json."]);

    public static string Read(string name)
    {
        _ = Describe(name);
        return BundledSkillManager.LoadBundledFile($"resonite-uix/recipes/{name}.json");
    }

    public static string Export(string name, string output) => ExportContents(Read(name), output);

    public static string Export(string name, string output, PluginRecipeResolver? resolver) =>
        ExportContents(Read(name, resolver), output);

    private static string ExportContents(string contents, string output)
    {
        var path = Path.GetFullPath(output);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
            writer.Write(contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RLoopException("UIX_RECIPE_EXPORT_FAILED", "Recipe export requires a new writable file: " + path,
                ExitCodes.InvalidArguments, suggestions: ["Include an existing recipe file or choose a new export path."], innerException: ex);
        }
        return path;
    }
}
