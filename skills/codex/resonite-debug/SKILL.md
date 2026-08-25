---
name: resonite-debug
description: Diagnose Resonite content, ResoniteLink operations, or ProtoFlux builds through rloop without changing world state unless a fix is explicitly requested.
---

# Resonite Debug

Start with rloop status --json. Use bounded hierarchy, find, inspect --members, and component inspect to narrow the fault.

Use type search and type describe to verify runtime names and member types instead of relying on memory. For ProtoGraph, run flux check before flux build; inspect structured stderr from a JSON error. Use rloop logs --tail 200 --json only when a log path is configured.

Explain the evidence and root cause. Do not mutate the world during a diagnosis-only request. If a fix is requested, make the smallest scoped change, re-inspect, and confirm the observed result.
