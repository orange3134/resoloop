# Existing reference management (P1)

P1 extends the [existing-content import](EXISTING-IMPORT.md) with bundle-local reference keys, external dependencies and structured member paths. Import and diff remain read-only. Ownership is exactly the imported subtree; resolving an external dependency never enrolls it for editing.

## Data and editing

Single-file snapshots use schema version 2; `--split-slots` uses a schema-3 index with hashed parts. `slots`, `components` and external dependency definitions supply target keys. The reference graph maps each reference key to its owning Component key, structured source path and observed symbolic target (or null). All exposed Component references are captured, including null references and references nested in containers. Slot members can be targets; Slot attribute edits use the separate [slotChanges workflow](EXISTING-BUNDLES-SLOTS.md). The manifest stays schema version 1 with empty edit sets. P0 snapshots remain readable and field-editable; acquiring a reference graph requires a new import directory, never rewriting an old snapshot/hash.

Select the source edge by its exact Component/Slot and observed path. Copy only the intended reference edits into the manifest, leaving the snapshot untouched:

```json
"referenceChanges": {
  "r000003": { "kind": "external", "key": "e000002" },
  "r000004": {
    "kind": "member", "key": "c000012",
    "path": [{ "member": "Values" }, { "index": 1 }]
  },
  "r000005": null
}
```

These example keys and paths must be replaced with verified entries from the actual bundle. Supported target kinds are `component`, `slot`, `member`, `slotMember` and `external`. Only member targets take paths. Each path step has exactly one property: `member` for a literal member name, `index` for a zero-based list position, or `key` for a literal dictionary key. Names such as `a.b/[x]` and `${0}:` are not parsed as selectors/templates. Raw IDs are not accepted as editing targets.

Run `diff manifest.json --summary --profile --json` and review the exact affected Component and path before `apply manifest.json`. No requested reference change means preserve its current runtime state. Explicitly selecting its already-current target is also a no-op. Updated references advance a separate local checkpoint baseline; repeating a converged apply sends no world writes. Field edits and reference edits to the same top-level member cannot be combined.

## Resolution and write boundaries

- Resolve owned targets through their captured key, fresh Component/Slot identity and verified ancestor chain. Every structured path step must retain its observed member/container ID and kind/type. A moved/replaced list element fails rather than silently selecting the new occupant of its old index.
- Probe only the captured external target IDs with Component/Slot reads. Readable dependencies retain observed identity/type evidence. Their members are not editable and their outgoing references are not recursively imported. Dependencies that neither read can resolve remain opaque; this is not proof they are dangling. An opaque target may be retained where already referenced, but cannot be newly assigned elsewhere.
- For a changed reference, runtime Reflection must expose the source reference's accepted type and the target's compatible type. Component definitions, list/dictionary element definitions, SyncObject definitions and type interfaces/base types provide that evidence. Unknown types fail closed. New `slotMember` assignments currently lack adapter type evidence and are blocked; observed bindings can be retained.
- Supported sources are direct references, references inside partial SyncObjects, and terminal lists containing only references. Reference lists must keep their original length and element IDs/order. Each write builds a fresh payload preserving neighboring references. Dictionaries, mixed/nested lists and other unsupported source containers stay observed; no resize, Component replacement or reconstruction occurs.
- All field and reference edits undergo preflight before any mutation. Baseline/current/desired conflicts stop with `IMPORT_REFERENCE_CONFLICT`; altered identities, missing paths, unavailable targets and unproven types also stop. Apply rechecks ownership, source and target, reads back the result and checks neighboring references. Lost write responses can be recovered by retrying the same desired edit.
- Reads and writes are not atomic. Reference-list payloads may still race with running drivers between their final observation and update. Server-side member-level conditional updates are needed to eliminate this window; readback detects discrepancies but cannot undo unseen concurrent writes safely.

The old `observationOnlyReasons` report groups members outside the direct-field editing route; it is not a blanket claim that all those references are unwritable. The reference graph and per-edit Reflection checks determine P1 reference capability.

## WorldRoot verification, 2026-10-15 (JST)

The running **曲射! Curving Bullets** session at `ws://localhost:57086/`, `Root/WorldRoot` (`Reso_291`), was imported without world writes. ResoniteLink is `0.13.1.0`, client `YellowDogMan.ResoniteLink` is pinned to `0.13.1`.

| Observation | Result |
| --- | ---: |
| Owned Slots / Components | 4,532 / 8,054 |
| Same-name sibling groups | 480 |
| Component types / unavailable Reflection | 581 / 6 |
| Component reference edges (including null) | 10,442 |
| External reference edges / distinct dependencies | 1,073 / 144 |
| Readable / opaque external dependencies | 143 / 1 |
| References to nested member targets | 52 |
| Initial diff create / update / rename / delete | 0 / 0 / 0 / 0 |
| Explicitly retained external + nested references | 1,125 |
| Diff after declaring those 1,125 targets | 0 changes |
| WorldRoot mutation API calls | 0 |

The nested count means a target path with more than one structured step; this P1 census finds 52 rather than reusing the earlier prototype's 42. The sole opaque target is referenced by two edges. Its current bindings are retained, with an explicit capability warning.

Local evidence: `artifacts/worldroot-p1-import/` contains the snapshot, initial manifest, state, report and `manifest.references-verified.json`; `artifacts/worldroot-p1-initial-diff.json` and `artifacts/worldroot-p1-references-diff.json` contain full read-only plans and metrics. The explicit-retention manifest was only diffed, never applied to WorldRoot. Captures are sequential observations, not an atomic snapshot of gameplay.

The final re-import in `artifacts/worldroot-p1-reimport/` retains identical Slot IDs/names/parents and Component IDs/types/owners. Its initial diff is again zero. Final explicit-retention verification (`artifacts/worldroot-p1-references-final-diff.json`) uses 2,930 read requests and no mutations; within-preflight ancestor caching reduced redundant reads without caching across writes. Both import runs use 2,267 read requests each. All WorldRoot edit baselines remain empty. Compact checked evidence is in `artifacts/worldroot-p1-verification.json`.

The opt-in `ExistingReferenceIntegrationTests` uses only a unique `ResoLoop_Test_ExistingReferences_*` subtree. It changes a `ValueCopy<float>.Source` from `ValueMultiplexer<float>.Values[0]` to `[1]`, and one `MeshRenderer.Materials` element to another captured external material. Reflection verifies all types first. Assertions cover exactly two Component updates, unchanged member IDs/list length/neighbor reference/field values/external ownership, zero subsequent diff and zero writes on repeated apply. Its verified test root is removed in `finally`; WorldRoot is never a mutation fixture.

Offline regressions cover symbolic capture, literal paths, type mismatch, missing external targets, opaque retention, nested target reordering, source list shape changes, partial SyncObject updates, conflicts before field/reference mutation, multiple edits in one list, no-op retries and lost-response recovery. [Upstream issue drafts](RESONITELINK-IMPORT-ISSUES.md) distinguish remaining API gaps from local scope restrictions.

Final validation: solution build passed with zero warnings/errors; all **482 offline tests** passed, including P0 bundle compatibility and 18 P1 reference cases. The dedicated opt-in live test passed again on the final code and completed cleanup. Logs: `artifacts/p1-final-build.log`, `artifacts/p1-final-tests.log`, `artifacts/p1-final-integration.log`. README, CLI schema/help and the build/inspect skills document the workflow.
