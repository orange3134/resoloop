# Existing-content import

`import` registers an existing subtree without changing it. The graph is an observation baseline, not a request to restore every runtime value. Normal generated-content manifests retain their previous validation, ownership, tagging and lifecycle behavior.

## Workflow

1. Confirm the endpoint/session and inspect the intended boundary with a shallow hierarchy. Import an exact content Slot; world `Root` is rejected.
2. Run `resoloop import Root/WorldRoot --output NEW_DIRECTORY --url ws://localhost:PORT --command-timeout 600 --json`. Depth-four requests follow reference-only child stubs until the entire subtree is observed; the default 50,000-Slot ceiling stops oversized captures. Use `--max-slots` only after reviewing scope. The command re-observes structure and session before publishing.
3. Keep these files together:

| File | Purpose |
| --- | --- |
| `manifest.json` | `origin: existing`, snapshot path/hash and explicit `changes`, `referenceChanges`, `slotChanges`, optional `editFiles` |
| `snapshot.json` | Immutable observed graph or schema-3 index into hashed `snapshot/*.json` parts with common keys |
| `state.json` | Snapshot identity and last successfully applied values; preserved across interrupted edits |
| `report.json` | Counts, observation-only categories, external-reference count and read-operation metrics |

4. Run `diff manifest.json --summary --profile --report NEW_PLAN.json --json`. Empty changes preserve live values. Unknown types are separate warnings, not reasons to fabricate or omit instances.
5. Select the exact Component by its owning Slot and ID in the snapshot. Verify current Reflection before choosing a field. Edit only the intended member:

```json
"changes": {
  "c000042": { "Intensity": 2 }
}
```

This is illustrative: the actual key, member and type must come from this bundle/runtime. Strings are literal, including `${0}:`; imports do not run authoring template expansion. Large observations are outside the generated-manifest expansion budget.

6. Review `diff`, then `apply manifest.json`. P0 supports direct reflected fields; P1 adds [symbolic external and nested reference management](EXISTING-REFERENCES.md), [split bundles and explicit Slot attribute edits](EXISTING-BUNDLES-SLOTS.md). Apply reads back each write. Repeating a converged edit sends no write. A second edit uses the prior successful value as baseline.

## Safety and identity

- Import and re-import call only observation/Reflection APIs. Re-import requires a new directory; it cannot overwrite snapshots or silently clear pending edits.
- No import path calls create, delete, recreate, move, asset import or generated-content tagging. `--adopt` and `--prune` are rejected for imported manifests even with `--yes`.
- Names are descriptive. Within the same announced world session, fresh IDs, names, parent relationships and Component types verify saved bindings. Same-name siblings are never resolved by order. A missing/moved/renamed target is reported separately; unrelated verified targets can still be edited.
- `SessionInfo.UniqueSessionId` is connection-scoped and is recorded only as evidence. Reconnect uses the official discovery announcement's world session ID matched to the endpoint. Missing/ambiguous announcements stop with `IMPORT_WORLD_IDENTITY_UNAVAILABLE`; a different session stops with `IMPORT_SESSION_CHANGED`. There is no persistent cross-world-load matching in P0. Re-import after restart and manually review pending edits before transferring them: different bundles' keys are not interchangeable.
- A field with no requested change is not restored. If current equals desired, it is a no-op. Otherwise current must match the saved baseline, including member identity/type, or `IMPORT_EDIT_CONFLICT` stops preflight before any writes. Apply checks again immediately before writing and inspects the result. These operations are not atomic; concurrent edits inside the remaining read/write window require upstream compare-and-set support.
- Baselines advance only after observed successful writes or already-converged edits. Preserve the immutable snapshot and edit checkpoint after an interruption. Retrying the same desired edit recovers a successful write whose response/checkpoint was lost. Changing intent first can cause a conflict, which is preferable to silently overwriting unknown state.
- Unknown Components and empty/event/playback/nested/list/reference members stay in the snapshot. Unsupported edits fail with `IMPORT_MEMBER_OBSERVATION_ONLY`; no Component replacement occurs. P0's direct-field restriction is narrower than the upstream API and is not a claim that every other member kind is unwritable upstream.
- Imports are not asset backups, portable item closures, ProtoGraph decompilations or complete reconstructible manifests. P1 permits explicit edits to Slots' local transforms, activation, persistence, tag and order offset, with separate baseline checkpoints. Names and parent relationships remain identity evidence and cannot be edited by this workflow. The dedicated imported state is not accepted as generated-content stable-selector state for primitive commands.

## WorldRoot verification, 2026-10-15 (JST)

Target: the already-running session **曲射! Curving Bullets**, `Root/WorldRoot`, endpoint `ws://localhost:57086/`. Runtime Resonite `2026.9.18.82`, ResoniteLink `0.13.1.0`; client package `YellowDogMan.ResoniteLink` `0.13.1`.

| Observation | Result |
| --- | ---: |
| Slots | 4,532 |
| Components | 8,054 |
| Component types | 581 |
| Reflection unavailable types | 6 |
| Same-name sibling groups | 480 |
| External references | 1,073 |
| Import/re-import world mutation API calls | 0 / 0 |
| Initial diff create / update / rename / delete | 0 / 0 / 0 / 0 |
| Initial diff no-ops | 12,586 |

Both import runs recorded only `session.get`, `slot.get` and Reflection/type enumeration requests. The snapshot retained all exposed data (about 27 MB) instead of omitting unreflected fields or interpreting strings as templates. These are sequential observations, not an atomic freeze of running gameplay.

Local evidence is under ignored `artifacts/worldroot-p0-import/`, `artifacts/worldroot-p0-reimport/` and the accompanying diff reports. WorldRoot was never used for a mutation test. The opt-in `ExistingContentIntegrationTests` creates only `ResoLoop_Test_ExistingImport_*`, imports duplicate siblings, reconnects, changes one reflected `Light.Intensity`, verifies exactly one `component.update`, verifies the sibling's unchanged value and all original names/Component counts, then deletes only its verified test root in `finally`.

Offline regressions cover no-op/re-import, unknown types, external-reference/literal preservation, explicit one-field edits, three-way conflicts, session changes, missing instances, unsupported member kinds, lifecycle refusal, snapshot tampering, interrupted write recovery, CLI parsing and bounded capture. Build/test commands remain those in `AGENTS.md`; use `-m:1` if the local MSBuild worker sandbox cannot start parallel nodes.

Final validation: solution build passed with zero warnings/errors; all **464 offline tests** passed (including 16 import cases). The dedicated opt-in live import/edit test passed and completed its verified-root cleanup. Both initial and repeated import diff reports contain only `session.get` and `slot.get` requests, with zero changes and 12,586 no-ops. Compact machine-readable evidence is in `artifacts/worldroot-p0-verification.json`; full final logs are `artifacts/p0-final-tests.log` and `artifacts/p0-final-integration.log`.
