---
name: resonite-build
description: Build or modify Resonite world content through rloop when a request requires observing, editing, validating, and iterating on Slots, Components, or ProtoFlux.
---

# Resonite Build

Use rloop with --json as the primitive interface and keep source artifacts in the repository.

1. Run rloop status --json. If connection fails, report the actionable error; do not invent a port.
2. Observe with a bounded hierarchy, then find and inspect the relevant target. Prefer stable IDs returned by the current session.
3. Before adding a Component or setting an unfamiliar member, run type search and type describe. Never guess Resonite type or member names.
4. Make only changes authorized by the request. For experiments, create an unmistakable RLoop_Test Slot and keep all edits below it.
5. Use Slot/Component primitives or a checked-in JSON apply document. Treat delete/remove as destructive and pass --yes only for an exact verified target.
6. Re-inspect the changed Slot and Component. Compare observed values with the requested outcome.
7. If ProtoFlux is needed, edit .pg source, run flux check, then flux build, then flux deploy to a verified parent. Re-inspect after deployment.
8. On failure, use the JSON error code, context, suggested next action, Reflection, and logs when configured. Change one hypothesis at a time and stop if repeated attempts would broaden scope or risk unrelated content.

IDs are session-scoped. Refresh them after a Resonite session restart.
