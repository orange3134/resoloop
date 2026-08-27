---
name: resonite-flux
description: Develop, check, build, deploy, and diagnose Git-managed ProtoGraph source with rloop and Flux-SDK.
---

# Resonite Flux

Keep ProtoFlux logic in .pg files. Confirm rloop flux status --json, the Resonite managed DLL path, and the active ResoniteLink URL.

Run flux check for fast semantic diagnostics, then flux build. On failure, fix primaryDiagnostics first; diagnostics identify whether their raw text came from stdout or stderr and separate parse/type/cascade categories. Resolve the destination with find/inspect, and deploy using a verified parent ID or path. Deployment replaces only a same-named module child under that parent; unrelated children must remain untouched.

For multiple modules or hot reload, keep a checked-in schema-v1 Flux module manifest with explicit `dependsOn`. Prefer a `$slot:key` parent plus the matching world apply state; rloop verifies a same-connection ID or resolves the saved path after reconnect. Declare module I/O in `bindings`: use `mode: source` for an `in` name targeting `$slot:key`, `$component:key`, or `$member:key.Member`, and `mode: drive` for an `out` name targeting `$member:key.Member`. Never paste session-scoped IDs into a manifest. Treat `FLUX_BINDINGS_UNRESOLVED` as a pre-deploy failure and correct the world state or binding declaration. Run `flux deploy-manifest` once, then use `flux watch MANIFEST.json` in the foreground. It topologically builds changed modules and deploys only successful builds. Preserve its deploy-state checkpoint and follow the non-atomic recovery report after failure.

After deployment, re-inspect the parent and confirm each module's before/after Slot identity and each generated input/output reference target. Stop watch on cancellation. Do not reimplement compilation or generate ProtoFlux nodes directly—use Flux-SDK. Treat an untested Flux-SDK series reported by doctor as a compatibility warning that must be resolved before live deployment. Flux-SDK 1.9.0 can return `Invalid component type` after wiring some interface-typed global inputs; report it as a non-atomic upstream deployment failure, inspect the exact partial module, and do not mark it successful.
