# Development guide

## Architecture

Dependency direction is RLoop.Cli → RLoop.Core ← RLoop.ResoniteLink and RLoop.Cli → RLoop.Flux → IFluxDeployer ← RLoop.Flux.Deployer. Core must not reference ResoniteLink models or Flux-SDK types. Keep ResoniteLink Beta changes in the adapter and Flux-SDK API changes in the F# deployer.

## Commands

~~~powershell
dotnet build RLoop.slnx
dotnet test RLoop.slnx --no-build
dotnet run --project src/RLoop.Cli -- help
~~~

Live tests are opt-in:

~~~powershell
$env:RLOOP_RUN_INTEGRATION="1"
$env:RESONITE_LINK_URL="ws://localhost:<current-port>"
dotnet test tests/RLoop.IntegrationTests/RLoop.IntegrationTests.csproj --filter Category=Integration
~~~

## Resonite safety

- Never run destructive operations against Root or an unverified ID/path.
- Put experiments under an unmistakable RLoop_Test* Slot and clean only that exact Slot in finally.
- Do not change existing user content unless the task explicitly names it.
- CLI delete/remove must retain explicit --yes; do not add an implicit confirmation bypass.
- IDs are session-scoped. Re-observe after session restart.

## Upstream discipline

- Do not guess ResoniteLink messages, field wrappers, Component type names, or member names. Check the pinned upstream source/docs and verify through runtime Reflection.
- Keep official YellowDogMan.ResoniteLink behind IResoniteClient; never leak its raw JSON/models into Core.
- Do not implement a ProtoFlux compiler. Reuse Flux-SDK CLI or Papaltine.FluxSDK.Core.
- Public source and documented APIs may inform implementation. Local decompilation is for API behavior research only: never copy private Resonite implementation or extracted source into this repository.

## Tests and Skills

Maintain offline tests for argument parsing, configuration precedence, model mapping, serialization, value conversion, and error codes. Any live test must be opt-in and sandboxed.

When CLI behavior changes, update README examples and affected skills/codex/*/SKILL.md. Skills are workflows, not command mirrors: preserve Reflection-first behavior, bounded observation, exact-target destructive safety, and post-change inspection.
