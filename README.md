[English](README.md)｜[日本語](README_JA.md)

# resoloop

![resoloop_logo](./resource/resoloop_resonite_16_9.png)

resoloop is a CLI for controlling Resonite worlds from AI agents such as Codex and Claude Code.

Tell the AI what you want to create, and it will inspect the current world, describe the Slot and Component structure in files, and apply and verify the result through ResoniteLink. Because the work is stored as files, you can apply the same structure repeatedly and track changes with Git.

resoloop automatically adds `FrooxEngine.AI_GeneratedContent` to the root of the content it generates and records the running tool's name and version in `Source` (for example, `[resoloop 0.1.0-preview.9]`). The same tag is also added to portable and equippable roots within the declaration tree.

> [!NOTE]
> resoloop is currently in preview. ResoniteLink is also in Beta, so updates may change its behavior.

## Installation

Requirements:

- Windows 10 or 11
- [`.NET 10 SDK`](https://dotnet.microsoft.com/download/dotnet/10.0)
- Resonite
- An AI coding agent such as Codex or Claude Code

Install resoloop in PowerShell:

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.9
resoloop --version
~~~

If resoloop is already installed, update it with the following command:

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.9
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
4. When `ResoniteLink running on port: ...` appears, note the port number.
5. In the PowerShell session for your project, set the displayed port in an environment variable.

For example, if the displayed port is `12449`:

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
resoloop doctor
~~~

The setup is complete when `ready` appears at the end.

Alternatively, tell the AI which port to use when making your request:

~~~text
Create a box in Resonite that glows in rainbow colors. ResoniteLink is running on port 12449.
~~~

### 3. Ask the AI to work on your project

Open the project you created in an AI agent. If you have continued using the same AI session since creating the project, reopen the session once so that the agent can discover the generated Skill.

Then describe what you want to build in Resonite using ordinary language. For example:

~~~text
Create a teleporter gun in Resonite. Make it an equippable item shaped like a gun. When fired, it should launch a projectile in an arc and teleport me to the point where the projectile lands.
~~~

## Blender modeling

Preview.9 supports compact, scoped observation without member payloads:

~~~powershell
resoloop hierarchy --under Root --depth 1 --include-components --summary --json
~~~

In apply documents, use `$slot-member:crystal.Rotation` to reference a declared Slot field from a native driver; `$member:componentKey.MemberName` continues to refer to Component fields. Inspect the driver type before wiring it. See [declarative references](docs/DECLARATIVE.md) and the [blacksmith test improvements](docs/BLACKSMITH-FEEDBACK.md).

Preview.9 includes `resoloop blender find`, `blender run SCRIPT.py`, and `blender export FILE.blend --output NEW_DIRECTORY --name Prop --parent VERIFIED_PARENT`. Blender is discovered without requiring PATH. The bundled `resonite-blender` skill guides background Python modeling, VR resource budgets, and importing static meshes with UVs, normals, textures and materials. A missing Blender installation requires user permission; the CLI never installs it automatically.

Export produces a reviewable apply bundle, without rendering or changing the world:

~~~powershell
resoloop blender find --json
resoloop blender run modeling/model.py --arg=artifacts/prop.blend --json
resoloop blender export artifacts/prop.blend --output content/prop-v1 --name Prop --parent VERIFIED_PARENT --json
# For independent parts/pivots and supported direct PBR data images:
resoloop blender export artifacts/prop.blend --output content/prop-v2 --name Prop --parent VERIFIED_PARENT --preserve-hierarchy --pack-pbr --json
resoloop validate content/prop-v1/model.apply.json --strict --json
resoloop diff content/prop-v1/model.apply.json --json
resoloop apply content/prop-v1/model.apply.json --state .resoloop/state/prop.json --json
~~~

Use your project's Python script and an inspected parent. [Blender workflow and limitations](docs/BLENDER.md) covers discovery overrides, texture packing, supported shaders and tests.

UV-bearing n-gons and current generated/edited image buffers are handled during export. For texture color-profile enums, use `resoloop type describe FrooxEngine.StaticTexture2D --member PreferredProfile --json` to follow runtime Reflection. Item audits report external-role candidates and unused allow entries. See the [production-test feedback and fixes](docs/BLENDER-FEEDBACK.md).

New exports isolate providers in named Slots for interrupted-apply recovery; existing root-provider bundles can retain their layout with `--legacy-root-providers`. Strict validation checks write conversion before imports, including nullable enums. Nested SyncObject fields converge without suppressing drift detection, Slot field IDs are inspectable, and scene bounds identify geometry versus partial/pivot estimates. [Clock tower and tank improvements](docs/CLOCKTOWER-FEEDBACK.md) records the changes and verification. Modeling prioritizes visual quality before resource reduction; session FPS is not an individual-model acceptance criterion.

## Learn more

- [Detailed documentation](README-DETAILS.md) — commands, architecture, declaration format, Flux-SDK, and limitations
- [Quick start](docs/QUICKSTART.md) — detailed steps including applying, verifying, and using ProtoFlux
- [Declaration format](docs/DECLARATIVE.md) — specification for `content/*.json`
- [Roadmap](docs/ROADMAP.md)

## License

[AGPL-3.0-or-later](LICENSE)
