# Structural UIX recipes

Choose the composition boundary before repeating recipes. The single-target boolean-state recipe is useful on its own, but repeating it for the same source adds a ValueCopy and a Slot per output. For two or more destinations without WriteBack, prefer one ValueMultiDriver<T> and group related state/drivers on one logic Slot; use its Value as owned state or feed it once from an external source. See [interaction and migration](interaction-and-migration.md) for the observed shared-button wiring and state-preserving consolidation. Keep independent controls and their layout boundaries separate. Existing applied recipe instances need an explicit reviewed migration rather than automatic flattening.

Use recipes for recurring native wiring, while designing visuals for the current request. They contain no Image, Text, font, sprite, material, color, fixed dimensions, or pressed animation. Recipe `rect`, `off` and `on` values are supplied by the caller. A recipe alone is not a finished visible control.

For new content, use `$recipe` directly in `children`; no export/include or prototype-copying helper is needed. `$with` takes the parameters in the table below. The compiler gives built-in instances the prefix `uix-<recipe>--<key>`: replace `<key>` in the port table with that entire prefix. Shared state and a control may use the same logical key without colliding. All parameters are required; missing and unknown names fail together before live mutation.

```json
{"children":[
  {"$recipe":"value-state","$with":{"key":"enabled","valueType":"bool","initial":false}},
  {"$recipe":"toggle","$with":{"key":"enabled","rect":{},"state":"$member:uix-value-state--enabled-state.Value"},
   "children":[{"slot":{"key":"enabled-face","name":"Caller-designed face"},"components":[]}]}
]}
```

The toggle Slot is `uix-toggle--enabled`, its button is `uix-toggle--enabled-button`, and its toggle Component is `uix-toggle--enabled-toggle`. Append caller-designed components/children normally. Override values and keys are literal; use explicit namespaced selectors. Keys use ASCII letters, digits, `-` or `_`. Same-recipe/same-key duplicates still fail global key validation. This syntax uses the installed CLI's recipes; retain exported prototypes when a project needs a pinned editable copy. Do not convert an already-applied prototype to `$recipe` without a deliberate key migration: its generated keys differ.

After apply, batch the known values/references needed for one verification phase:

```powershell
resoloop observe '$member:uix-value-state--enabled-state.Value' '$member:uix-toggle--enabled-toggle.TargetValue' --state STATE --json
```

It returns member IDs, typed values and target IDs, without unrelated fields. Combine other control selectors in this same call; avoid one `component inspect` process per control when only known members are needed. Different Components are read sequentially, not atomically. Keep wider structural inspections, captures and reversible probes where those provide different evidence.

Discover unfamiliar contracts or export a pinned copy without dumping source:

```powershell
resoloop uix recipe list --json
resoloop uix recipe describe button --json
resoloop uix recipe describe boolean-state --json
resoloop uix recipe export button --output content/recipes/button.json --json
resoloop uix recipe export boolean-state --output content/recipes/boolean-state.json --json
```

Export writes a new file only. Alternatively include the packaged `../.agents/skills/resonite-uix/recipes/button.json` from `content/main.json`. Export to keep project recipes independent from future skill syncs. Include paths are relative to the declaring file. Recipes are ordinary `prototypes`; they use existing include/parameters expansion and one ownership/state.

This is a composition fragment, not a complete panel. Its visual choices belong to the caller. Add it below a Canvas and provide the referenced `face-rect` and arbitrary visual children:

```json
{
  "include": ["recipes/button.json", "recipes/boolean-state.json"],
  "children": [
    {
      "$prototype": "uix.button",
      "$with": { "key": "accept", "rect": { "OffsetMin": [20, 20], "OffsetMax": [-20, -20] } },
      "components": [
        { "key": "accept-hit", "type": "[FrooxEngine]FrooxEngine.UIX.Image", "fields": { "InteractionTarget": true, "Tint": [0, 0, 0, 0] } }
      ]
    },
    {
      "$prototype": "uix.boolean-state",
      "$with": {
        "key": "accept-feedback", "valueType": "float2",
        "source": "$member:accept-button.IsPressed", "target": "$member:face-rect.OffsetMin",
        "off": [0, 0], "on": [2, -2]
      }
    }
  ]
}
```

Use another boolean-state instance for OffsetMax when translating a whole face. To change tint instead, use a runtime-verified `colorX` specialization and target the caller's Image.Tint with caller-defined colors. Omit feedback when no visual state change is desired. Logic can live anywhere within the ownership; keep it outside layout-controlled child lists.

The button exposes `<key>-button.IsPressed` and `.IsHovering`; its RectTransform is `<key>-rect`. Button ColorDrivers is explicitly empty to avoid taking ownership of caller-defined tint. Add your own interaction Graphic and choose hit geometry; movable visuals can live underneath a stable hit area. Do not add a second Button/RectTransform to replace the supplied one. Override its rectangle through `rect`; append visual components and children normally. Actual click actions need supported native/application wiring. These recipes do not invent public SyncDelegate writes.

`scroll-content` supplies `<key>-rect` and `<key>-scroll`, with `viewport` pointing to the caller's viewport RectTransform. The caller chooses Mask, input Graphic, content layout/fitting and children. It does not force direction, spacing, size or scroll position.

## Inputs, state and selection

Use `uix recipe describe NAME --json` for parameter/port contracts. Export only needed recipes; `list` is an index, not a request to read every definition. These recipes also contain no graphics or visual defaults:

| Recipe | Required `$with` parameters | Main connection |
| --- | --- | --- |
| `value-state` | key, valueType, initial | `<key>-state.Value` stores caller state; initialFields preserves edits on reapply |
| `text-input` | key, rect, text | text is a `$component:` selector for caller-styled UIX.Text; the recipe connects TextField and TextEditor |
| `toggle` | key, rect, state | state is a bool `$member:` selector; native ButtonToggle handles Button slot events |
| `choice` | key, rect, valueType, state, option | each option reads/writes the same typed state; `<key>-selected.Value` is its derived bool output |
| `slider` | key, rect, min, max, initial, direction, anchorOffset, handle | float Value; handle is a stable RectTransform component **key**, without selector prefix |

Example state and option fragments:

```json
{ "$prototype": "uix.value-state", "$with": { "key": "mode", "valueType": "int", "initial": 0 } }
{ "$prototype": "uix.choice", "$with": { "key": "basic", "rect": {}, "valueType": "int", "state": "$member:mode-state.Value", "option": 0 } }
{ "$prototype": "uix.choice", "$with": { "key": "advanced", "rect": {}, "valueType": "int", "state": "$member:mode-state.Value", "option": 1 } }
```

Place them as separate children in the normal declaration. Add arbitrary hit Graphics/visual children to each control. Distinct option values with one shared source give exclusive selection; a value matching no option selects none. The recipe does not enforce group uniqueness across independent declarations. Never write `<key>-selected.Value`: native CheckVisual drives it. Use it as a source for boolean-state, any visual feedback, or page visibility.

Tabs reuse `choice`: connect each selected bool to `$slot-member:page-key.IsActive` using `boolean-state` with valueType `bool`, off false and on true. Keep the selector buttons, state and visibility drivers outside the pages they disable. An open/closed panel uses `value-state<bool>` + `toggle` + the same visibility binding. Neither composition forces a tab bar, accordion shape, animation or popup position. Dropdown focus, outside-click dismissal and keyboard navigation are additional behaviors; do not label this composition a complete dropdown.

For text input, provide a separate UIX.Text (usually a descendant), set its Content in **initialFields**, and keep style/configuration in fields. The recipe owns Editor.Text, TextField.Editor and TextField.__text links, not text content, caret styling or graphics. The focus Button has ClearFocusOnPress=false and no ColorDrivers. Inspect generated/runtime components; do not remove engine-created helpers simply because they were not declared. Actual focus, typing, caret and selection need user-input checks.

For sliders, choose min < max and initial within that range. Reflection on the tested runtime exposes `Horizontal` and `Vertical` directions. Supply caller-owned `anchorOffset`: centered horizontal travel uses [0, 0.5], centered vertical travel uses [0.5, 0]. Changing only direction leaves the old offset and can move the handle outside the track. Caller-owned handle OffsetMin/OffsetMax determine geometry; its AnchorMin/AnchorMax are driven and must not also be managed in fields. The slider's Value is initialized only when created. Slider already handles input, so do not add another Button to its Slot. Use a separate caller-owned track interaction Graphic.

For verification templates covering these controls and state preservation, read [control verification](control-verification.md) when adding tests. Small scoped probes are sufficient during iteration; actual input, capture and save/reload remain separate evidence.

Run `diff --brief --report NEW_FILE --json` before apply. It already performs runtime Reflection validation of expanded types, fields and symbolic references; do not separately describe every known recipe member or repeat both validate modes. Use type describe for unfamiliar additions, enum choices or a reported incompatibility. Apply repeats preflight against current runtime state. Bundled examples are not a runtime compatibility guarantee.

Check one representative of each functional pattern before repeating it. Probe both source transitions and downstream values, restore values, and verify reapply convergence. Captures verify visual choices; actual clicking/typing and save/reload remain separate checks. Recipe provenance does not replace them. Contracts were checked through public runtime Reflection with pinned ResoniteLink 0.13.1; raw IDs are never included.
