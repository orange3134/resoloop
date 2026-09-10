---
name: resonite-debug
description: Diagnose Resonite content, ResoniteLink operations, or ProtoFlux builds through resoloop without changing world state unless a fix is explicitly requested.
---

# Resonite Debug

Start with resoloop status --json. Use bounded hierarchy, find --under/--direct-children, inspect --component/--member, and component inspect to narrow the fault.

Use type search and type describe to verify runtime names and member types instead of relying on memory. For an open generic, use type specialize with the exact assembly-prefixed search result. For ProtoGraph, run `flux validate-manifest` before live resolution, use flux node search/describe for exact full node identities, run flux check before flux build, fix error.context.primaryDiagnostics first, and use each diagnostic's channel when consulting raw stdout/stderr. `FLUX_EMPTY_MODULE`, `FLUX_MODULE_PORT_UNBOUND`, `FLUX_BINDING_TYPE_MISMATCH`, and `FLUX_INTERFACE_GLOBAL_UNSUPPORTED` are pre-deploy evidence, not generic SDK crashes. Flux scalar aliases such as `int`/`System.Int32` and `bool`/`System.Boolean` are equivalent; a mismatch after normalization is evidence of a genuinely different target type. For saved items, use item audit to distinguish broken external references from runtime-context dependencies. Use resoloop logs --tail 200 --json only when a log path is configured.

For multiplayer-only faults, inventory every entrypoint's processing user and every mutable value's scope before changing the graph. `LocalUpdate` fires for every user, `LocalUser` identifies the current executor, `Local` is impulse-context state, `Store` is per-user non-networked state, and data-model fields or `Data Model Store` are normally synchronized. Look for per-user triggers contending on one synchronized field, an ambiguous listener/update user, high-frequency writes that should remain local, or a host gate that accidentally selects a headless host. Distinguish ProtoFlux `Drive` from synchronization and from manifest binding `mode: drive`. A successful single-user test does not rule out these faults; reproduce with at least two users when authorized, otherwise report the multiplayer hypothesis and verification gap.

`STABLE_COMPONENT_AMBIGUOUS` means the saved type/member/index evidence was insufficient; add an immutable `identityFields` value or a stable managed reference instead of choosing an ordinal by guess. For an equipped tool, `APPLY_RUNTIME_RELOCATABLE_ACTIVE` is a safety stop rather than a missing target: use its stable selector for read-only inspection, run `tool audit` when the whole item tree is observable, and return/drop it to the declared parent before apply. `STABLE_RELOCATABLE_SLOT_AMBIGUOUS` requires stronger same-Slot Component identity evidence; never pick a candidate by name alone. `SKILL_SYNC_CONFLICT` protects a user-edited or untrusted project skill and must be reconciled rather than overwritten.

Explain the evidence and root cause. Do not mutate the world during a diagnosis-only request. If a fix is requested, make the smallest scoped change, re-inspect, and confirm the observed result.
