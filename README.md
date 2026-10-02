[English](README.md)｜[日本語](README_JA.md)

# resoloop

![resoloop_logo](./resource/resoloop_resonite_16_9.png)

resoloop is a CLI for controlling Resonite worlds from AI agents such as Codex and Claude Code.

Tell the AI what you want to create, and it will inspect the current world, describe the Slot and Component structure in files, and apply and verify the result through ResoniteLink. Because the work is stored as files, you can apply the same structure repeatedly and track changes with Git.

resoloop automatically adds `FrooxEngine.AI_GeneratedContent` to the root of the content it generates and records the running tool's name and version in `Source` (for example, `[resoloop 0.1.0-preview.9]`). The same tag is also added to portable and equippable roots within the declaration tree.

> [!NOTE]
> resoloop is currently in preview. ResoniteLink is also in Beta, so updates may change its behavior.

After saving and reloading a world, resoloop preserves imported assets' `resdb:///` URLs when the state records the matching `$asset:` declarations. Older state files without `assetFields` stop before mutation with `APPLY_ASSET_MIGRATION_UNVERIFIED` if a saved URL cannot be verified. Inspect the asset, then explicitly set its verified saved URI as the manifest asset source (for example, `"source": "resdb:///…"`) and run `diff` again. Do not infer this mapping from a changed declaration or delete the state to bypass the check. Interrupted field writes invalidate their old asset evidence before the remote mutation; rerun the same manifest to converge.

ResoniteLink cannot remove list elements. When a list member declared directly in `fields`, such as `MeshRenderer.Materials`, is shorter than the runtime list, `diff` shows `recreate`, and apply replaces the Component on the same Slot. Nested lists inside a syncObject are not recreated and are not guaranteed to converge when shortened. For direct list members, apply replaces the Component as follows: it creates the replacement with the declared fields, verifies the list, re-points managed references, and then removes the old Component without `--prune`. The replacement starts from `initialFields` and type defaults for undeclared members. Apply stops before mutation with `APPLY_LIST_SHRINK_REFERENCED` when something in the hierarchy apply read, other than a managed declared reference, points at the old Component, stops with `APPLY_LIST_SHRINK_NOT_CONVERGED` before deleting the old Component when the runtime keeps a replacement list longer (undoing only replacements it can safely remove), and stops before mutation with `APPLY_RECREATE_INTERRUPTED` when an interrupted recreate cannot resume in the same session. Follow that error's `recovery` and suggestions; an unchanged retry does not always converge. Before apply, `resoloop test` checks the currently existing Component even when `diff` plans a `recreate`.

## Installation

Requirements:

- Windows 10 or 11
- [`.NET 10 SDK`](https://dotnet.microsoft.com/download/dotnet/10.0)
- Resonite
- An AI coding agent such as Codex or Claude Code

Install resoloop in PowerShell:

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.15
resoloop --version
~~~

If resoloop is already installed, update it with the following command:

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.15
~~~

## Usage

### 1. Create a project

Create a dedicated project for each thing you want to build in Resonite.

~~~powershell
resoloop init MyResoniteProject
Set-Location MyResoniteProject
~~~

### 2. Configure ResoniteLink

1. Start Resonite and open the world you want to edit.
2. Open the `Settings` tab on the Dashboard's `Session` page.
3. Select `Enable ResoniteLink` in the lower-left corner.
4. Once `ResoniteLink running on port: ...` appears, ResoniteLink is ready.

The AI can then use resoloop to find the port and connect automatically.

You can also set the connection yourself using an environment variable, but this is optional.

For example, if the displayed port is `12449`:

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
resoloop doctor
~~~

The setup is complete when `ready` appears at the end.

### 3. Ask the AI to work on your project

Open the project you created in an AI agent. If you have continued using the same AI session since creating the project, reopen the session once so that the agent can discover the generated Skill.

Then describe what you want to build in Resonite using ordinary language. For example:

~~~text
Create a teleporter gun in Resonite. Make it an equippable item shaped like a gun. When fired, it should launch a projectile in an arc and teleport me to the point where the projectile lands.
~~~

## Blender modeling

When you ask for a complex model, resoloop may use Blender.

If Blender is installed on your PC, resoloop detects and uses it automatically.

You do not need to have Blender open.

## Efficient UIX authoring

UIX recipes provide reusable button, text-input, toggle, exclusive-choice, slider, shared-state, boolean binding and scroll-content structures. Choice/state bindings also compose tabs and open/closed panels. They leave shapes, colors, fonts, dimensions and feedback to the caller. Discover a recipe's parameters and connection points, then export it as a normal declaration prototype:

~~~powershell
resoloop uix recipe list --json
resoloop uix recipe describe button --json
resoloop uix recipe describe text-input --json
resoloop uix recipe export button --output content/recipes/button.json --json
resoloop diff content/main.json --brief --report artifacts/plan-01.json --json
resoloop apply content/main.json --brief --json
resoloop uix audit '$slot:panel' --state .resoloop/state/panel.json --brief --report artifacts/audit-01.json --json
~~~

For new content, declare `{"$recipe":"button","$with":{"key":"accept","rect":{}}}` directly in `children`; no include/export is needed. Generated keys use `uix-button--accept` as their prefix. Exported prototypes remain supported for pinned editable wiring; their existing keys do not change. See [Structural recipes](skills/codex/resonite-uix/references/recipes.md) for parameters and ports. Recipe export and reports require new filenames. `--brief` reduces displayed evidence, not validation: diff already performs offline and runtime checks, and apply repeats preflight against the current world. Standalone validate remains useful for offline authoring or diagnosis. Full reports preserve the existing JSON format.

Batch known managed fields with `resoloop observe '$member:state.Value' '$member:toggle.TargetValue' --state STATE --json`. This read-only command accepts up to 64 selectors and returns typed values and reference IDs, failing if any selected field is missing. It uses normal stable re-resolution after reconnect. Cross-Component values are sequential observations, not an atomic snapshot.

## Declaration and capture assistance

New declarations can start from an offline structural scaffold. Inspect only the schema section you need; examples come from the parser DTOs. Provider scaffolds contain no visual settings.

~~~powershell
resoloop manifest scaffold --key panel --output content/panel.json --json
resoloop schema describe camera --json
resoloop manifest scaffold --kind provider --key front-material --type '[FrooxEngine]FrooxEngine.UI_UnlitMaterial' --output content/front.node.json --json
resoloop validate content/panel.json --json
# After appending the provider node, designing and applying the panel:
resoloop capture content/panel.json --frame '$slot:canvas' --view front --output artifacts/front.jpg --json
resoloop capture content/panel.json --frame '$slot:canvas' --view rear --output artifacts/rear.jpg --json
~~~

`--frame` requires the exact Slot containing one live planar Canvas, and uses its collider plus ancestor transforms. It preserves content scale; overflowing children, curved geometry, mirrored scales and occlusion require an explicit camera. `validate`/`diff` return conservative identity warnings for same-type Components sharing a Slot; separate named providers avoid this ambiguity for new content. See [authoring assistance](docs/AUTHORING-ASSISTANCE.md) for contracts and verification.

## Further documentation

Batch required Reflection metadata with `type query --request FILE.json --json`; obtain a request example from `schema describe reflection --json`. Explicit `members` select output; `enums` optionally adds candidate values. Persistent definitions are trusted when engine/link versions and adapter/Core builds match, with no default expiry. Local port changes do not invalidate them. Check, diff, apply and primitive conversion share this cache. `type check --request FILE.json --brief --json` checks contracts; `type check --manifest FILE.json --brief --json` reuses strict declaration validation. `--refresh` re-fetches definitions; `--cache off` bypasses disk reads/writes; `--cache-dir DIR` overrides storage. Refresh after MOD/DLL changes. Instance IDs, values and reference targets are still observed live. `--profile` includes request and cache counts. See [Reflection caching and measurements](docs/REFLECTION-EFFICIENCY.md).

- [Detailed documentation](README-DETAILS.md) — commands, architecture, declaration format, Flux-SDK, and limitations
- [Quick start](docs/QUICKSTART.md) — detailed steps including applying, verifying, and using ProtoFlux
- [Declaration format](docs/DECLARATIVE.md) — specification for `content/*.json`
- [UIX agent efficiency](docs/AGENT-EFFICIENCY.md) — implementation and measured before/after token usage
- [Control recipe efficiency](docs/CONTROL-RECIPE-EFFICIENCY.md) — expanded controls and a separate before/after benchmark
- [Authoring assistance efficiency](docs/AUTHORING-EFFICIENCY.md) — two runs per version measuring declaration, material-reference and capture assistance
- [Batched observation and direct recipes](docs/OBSERVATION-RECIPE-EFFICIENCY.md) — follow-up implementation, fixed-operation replay and independent authoring comparison
- [Roadmap](docs/ROADMAP.md)

## License

[AGPL-3.0-or-later](LICENSE)
