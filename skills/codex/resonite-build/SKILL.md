---
name: resonite-build
description: Build or modify Resonite world content through rloop when a request requires observing, editing, validating, and iterating on Slots, Components, or ProtoFlux.
---

# Resonite Build

Use rloop with --json as the primitive interface and keep source artifacts in the repository.

1. For a new repository without `.rloop.json`, run `rloop init` only in the user-approved project directory. It must not replace a conflicting file; resolve `INIT_FILE_EXISTS` with the user.
2. Run `rloop doctor --json`, then `rloop status --json`. If connection fails, report the actionable error; do not invent a port. Optional Flux or log warnings do not block Slot/Component work.
3. Observe with a bounded hierarchy, then find and inspect the relevant target. Prefer stable IDs returned by the current session.
4. Before adding a Component or setting an unfamiliar member, run type search and type describe. Never guess Resonite type or member names. When a described member is a list, pass supported field/reference elements as a JSON array and re-inspect the resulting element targets.
5. Make only changes authorized by the request. For experiments, create an unmistakable RLoop_Test Slot and keep all edits below it.
6. Use Slot/Component primitives or a checked-in schema v1 JSON apply document with `ownership.key` and a stable root `slot.key`. Prefer `$slot:key`, `$component:key`, `$member:key.MemberName`, and `$asset:key`; forward references are allowed. Use include/parameters/prototypes/repeat to keep repeated content maintainable. Give every same-type Component on one Slot an explicit unique key, and give Slots explicit keys when rename stability matters.
7. Before mutation, run `rloop validate FILE --json`, `rloop validate FILE --strict --json`, and `rloop diff FILE --json`. Treat create/update/rename/delete reasons as the review boundary. If an existing root has no state binding, inspect the exact target and use `--adopt` only when the user intended rloop to manage it. Never use adoption to bypass ambiguity or ownership uncertainty.
8. Run apply and observe stderr progress; use `--profile` when performance matters and `--ndjson-progress` for machine-readable progress. Preserve the reported state file after cancellation or timeout and re-run the same apply to resume. Normal apply does not delete stale content. Use `--prune --yes` only after the diff shows exclusively intended delete candidates inside the ownership root.
9. Re-inspect the changed Slot and Component. When cameras/tests are declared, generate `scene summary` and `capture` artifacts, then run `rloop test`. Treat `screenshotAvailable: false` as a wireframe-only visual check. Execute an interaction probe only when its method was verified through Reflection, the manifest marks it safe, and the user authorized `--probe --yes`; otherwise report structural-only verification.
10. If ProtoFlux is needed, require the tested Flux-SDK series and managed-data check, edit `.pg` source, and run flux check. Prefer a module manifest for multiple modules or watch: it topologically builds and deploys only successful changes to a verified parent, and can resolve `$slot:key` through world state. Re-inspect after deployment and retain deploy state for non-atomic recovery.
11. On failure, use the JSON error code, context, suggested next action, Reflection, and logs when configured. Change one hypothesis at a time and stop if repeated attempts would broaden scope or risk unrelated content.

IDs are session-scoped. Refresh them after a Resonite session restart.
