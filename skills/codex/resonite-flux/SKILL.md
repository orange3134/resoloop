---
name: resonite-flux
description: Develop, check, build, deploy, and diagnose Git-managed ProtoGraph source with resoloop and Flux-SDK.
---

# Resonite Flux

Keep ProtoFlux logic in .pg files. Confirm `resoloop flux status --json`, the active ResoniteLink URL, and that `resoloop doctor --json` reports a successful managed-data check/build probe through either the configured path or Flux-SDK auto-discovery.

Run flux check for fast semantic diagnostics, then flux build. On failure, fix primaryDiagnostics first; diagnostics identify whether their raw text came from stdout or stderr and separate parse/type/cascade categories. Resolve the destination with find/inspect, and deploy using a verified parent ID or path. Deployment replaces only a same-named module child under that parent; unrelated children must remain untouched.

Discover exact node identities and ports with `resoloop flux node search` and `resoloop flux node describe`; do not infer a node from its short name. The catalog preserves duplicate short names by full runtime identity and is cached by Flux-SDK/library identity. A successful build that packs zero nodes is not deployable: keep a reachable entrypoint such as CallInput, LocalUpdate, a Dynamic Impulse receiver, or another graph consumer.

For multiple modules or hot reload, keep a checked-in schema-v1 Flux module manifest with explicit `dependsOn`. Prefer a `$slot:key` parent plus the matching world apply state; resoloop verifies a same-connection ID or resolves the saved path after reconnect. Declare every module `in`/`out` in `bindings`: use `mode: source` for an `in` name targeting `$slot:key`, `$component:key`, or `$member:key.Member`, and `mode: drive` for an `out` name targeting `$member:key.Member`. Never paste session-scoped IDs into a manifest. Treat unbound ports, direction/type mismatch, and unresolved bindings as pre-deploy failures. Run `flux deploy-manifest` once, then use `flux watch MANIFEST.json` in the foreground. It topologically builds changed modules and deploys only successful builds. Preserve its deploy-state checkpoint and follow the non-atomic recovery report after failure.

After deployment, use `parentSlotId` as the deploy destination and `moduleSlotIdBefore` / `moduleSlotIdAfter` as the re-observed direct module child identities; re-inspect that child and each generated input/output reference target. Put the module below the Grabbable root when it must travel with an item, and confirm with `resoloop item audit ROOT --strict`. Stop watch on cancellation. Do not reimplement compilation or generate ProtoFlux nodes directly—use Flux-SDK. Treat an untested Flux-SDK series reported by doctor as a compatibility warning that must be resolved before live deployment. Flux-SDK 1.9.x interface globals such as `IButton global` are rejected before deploy; prefer a concrete Component `element` input and convert it inside the module with `asDrivenGlobal`, or use a Dynamic Impulse bridge for event-only coupling. ResoniteLink 0.13.1 cannot itself fire that bridge, so runtime verification still needs an in-world producer.
