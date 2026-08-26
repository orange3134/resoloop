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
6. Use Slot/Component primitives or a checked-in schema v1 JSON apply document with `ownership.key` and a stable root `slot.key`. Use `$ref:key` for Component references and `$member:key.MemberName` for field references; forward references are allowed. Give every same-type Component on one Slot an explicit unique key, and give Slots explicit keys when rename stability matters.
7. Before mutation, run `rloop validate FILE --json`, `rloop validate FILE --strict --json`, and `rloop plan FILE --json`. If an existing root has no state binding, inspect the exact target and use `--adopt` only when the user intended rloop to manage it. Never use adoption to bypass ambiguity or ownership uncertainty.
8. Run apply and observe stderr progress; use `--profile` when performance matters and `--ndjson-progress` for machine-readable progress. Preserve the reported state file after cancellation or timeout and re-run the same apply to resume. Treat delete/remove as destructive and pass --yes only for an exact verified target.
9. Re-inspect the changed Slot and Component. Compare observed values with the requested outcome.
10. If ProtoFlux is needed, require a compatible Flux-SDK and managed-data check, edit `.pg` source, run flux check, then flux build, then flux deploy to a verified parent. Re-inspect after deployment.
11. On failure, use the JSON error code, context, suggested next action, Reflection, and logs when configured. Change one hypothesis at a time and stop if repeated attempts would broaden scope or risk unrelated content.

IDs are session-scoped. Refresh them after a Resonite session restart.
