# Split existing-content bundles and Slot attributes (P1)

## Split observations, one management boundary

```powershell
resoloop import Root/WorldRoot --output artifacts/worldroot-split --split-slots 256 --url ws://localhost:PORT --command-timeout 600 --json
resoloop diff artifacts/worldroot-split/manifest.json --summary --profile --report artifacts/split-plan.json --json
```

`--split-slots N` groups at most N observed Slots and their Components/source reference edges into each `snapshot/part-*.json`. `snapshot/dependencies.json` holds type capabilities and external dependency definitions. The schema-3 `snapshot.json` index contains common ownership/session/root identity, relative part paths and SHA-256 hashes. The manifest hashes that index. Omitting the option (or using zero) retains the schema-2 single snapshot format. Older schema-1/2 observations and state remain readable.

The loader verifies every part, merges the complete namespace, and then resolves source owners and reference targets. References can point forward or form cycles across data files without copying raw IDs. Slot, Component, reference and external keys retain their original meaning. There is one manifest, one imported ownership boundary and one checkpoint. Splitting does not import unrelated objects or combine independently captured worlds. Keys cannot be transferred between independent imports.

Keep the entire directory, including hashed parts and checkpoint, together. Never edit observation parts or recompute their hashes to bypass a conflict. Missing files, changed hashes, duplicate keys, unknown owners/targets and invalid paths stop before world mutation. Paths are relative to their index/manifest directory and cannot escape it or traverse symlinks/junctions. A file list is capped at 4,096 paths and 512 MiB of input; generated-manifest template expansion limits are not applied to these literal observations. Groups are by Slot count, not a fixed byte size; an exceptionally large individual Slot remains one group. The loader currently loads all parts for validation, rather than promising streaming or arbitrary-size imports.

## Split explicit edits

Add relative paths to the main manifest's `existing.editFiles`:

```json
"existing": {
  "snapshot": "snapshot.json",
  "sha256": "HASH_FROM_IMPORT",
  "changes": {},
  "referenceChanges": {},
  "slotChanges": {},
  "editFiles": ["edits/wiring.json", "edits/attributes.json"]
}
```

Example `edits/wiring.json`:

```json
{
  "schemaVersion": 1,
  "ownershipKey": "OWNERSHIP_KEY_FROM_MANIFEST",
  "snapshotSha256": "HASH_FROM_IMPORT",
  "referenceChanges": {
    "r000003": {
      "kind": "member", "key": "c000012",
      "path": [{"member":"Values"},{"index":1}]
    }
  }
}
```

Example `edits/attributes.json`:

```json
{
  "schemaVersion": 1,
  "ownershipKey": "OWNERSHIP_KEY_FROM_MANIFEST",
  "snapshotSha256": "HASH_FROM_IMPORT",
  "slotChanges": {
    "s000042": { "Tag": "${0}:literal", "IsActive": false, "OrderOffset": 10 }
  }
}
```

Replace all example keys and hashes with verified entries from that import. Edit parts may contain any combination of `changes`, `referenceChanges` and `slotChanges`. Main-file and part-file edits are merged at the individual attribute/field level; different fields of one object may live in different files. Duplicate edits to the same field, attribute or reference fail even if their values are equal. There is no last-file-wins rule. Duplicate JSON properties are rejected in parts. Parts cannot include more files; there are no recursive includes or templates. `${0}:literal` stays literal. Applying the main manifest performs preflight across all files before any write, then advances one state checkpoint. An edit part alone is not an apply manifest.

## Slot attributes

| Attribute | Value | Scope |
| --- | --- | --- |
| `Position`, `Scale` | Three finite numbers, array or x/y/z object | Local transform |
| `Rotation` | Four finite numbers, array or x/y/z/w object | Normalized local quaternion |
| `IsActive`, `IsPersistent` | JSON boolean | Explicit activation/persistence setting |
| `Tag` | Literal JSON string | Empty text clears; an unset observed Tag may be null |
| `OrderOffset` | Signed 64-bit integer | Slot ordering offset |

Attribute names are exact and case-sensitive. `Name` and `Parent` remain identity evidence; this workflow does not rename or move Slots. Raw Slot IDs and external dependency keys cannot be used as `slotChanges` keys. Changing transforms can affect descendant world-space appearance; positions are not automatically converted to global coordinates.

The pinned official Slot model and `AddUpdateSlotData` document partial updates: omitted fields are left alone. ResoLoop verifies the live Slot identity, ancestors, and field instance IDs/types; it sends one requested attribute per update instead of restoring the full capture. Each write is read back. Unrequested attributes and runtime values are preserved. Baseline/current/desired comparisons detect conflicts; any conflict found in preflight blocks all field, reference and Slot writes. Successful writes and already-converged intents advance `state.slotBaselines`; lost responses can be recovered by repeating the same intent. No-op comparisons use single-precision tolerance for transforms and treat an unset Tag and empty Tag as equivalent. The immutable observation never changes.

These are sequential, non-atomic writes. A runtime driver can still change a field between its last read and update; conditional updates need the [upstream API request](RESONITELINK-IMPORT-ISSUES.md). Unsupported fields stay observed instead of triggering recreation.

## Verification

The 2026-10-15 running WorldRoot capture contains 4,532 Slots, 8,054 Components and 10,442 reference edges. With `--split-slots 256`, it produces **19 data files** (18 Slot groups and one dependencies file), about 29.7 MB total, with the largest part about 2.24 MB. There are **857 references between different Slot-group files**. Initial diff has zero creates, updates, renames and deletes, with 12,586 no-ops. Import uses only read APIs.

`artifacts/worldroot-p1-split-import/` contains the complete split bundle. `manifest.retention.json` additionally loads `retain-references.json` and `retain-slots.json`, explicitly retaining 1,949 references (all cross-file, external and nested targets, deduplicated) and all seven attributes on WorldRoot itself. This retention manifest is used only for read-only diff, never applied to WorldRoot. Evidence and final checks are summarized in `artifacts/worldroot-p1-split-verification.json`.

The explicit-retention diff also has zero changes, with 4,480 read requests and no mutation API calls. Initial import uses 2,267 read requests and its empty diff uses 631. All Slot IDs/names/parents and Component IDs/types/owning Slots match the previous P1 capture; the imported state still has empty field, reference and Slot baselines. Unknown Reflection for six types and one opaque external dependency remain reported separately, without reconstructing or discarding them.

The opt-in `ExistingBundleSlotIntegrationTests` uses a unique `ResoLoop_Test_SplitSlots_*` sandbox. A Component reference targets a field in another file's child Slot. Separate edit files change that reference and the seven attributes of the sandbox's owned Slot; checks cover exact partial-update counts, stable instance IDs/names/parents, preserved child structure, zero subsequent diff and no-op repeated apply. Cleanup verifies and deletes only that test root in `finally`. Offline tests cover parsing, file/hash/identity validation, duplicate edits, literal strings, forward references, old formats, Slot conflicts, untouched runtime state, invalid values, and lost responses.

Validation: solution build passes with zero warnings/errors; the full offline suite passes **508 tests**. After finalizing exclusive creation and size/count limits for data-part writes, all **66 related tests** pass again. The live sandbox test passes with exactly seven `slot.update` calls and one `component.update`, followed by zero writes on repeated apply and verified cleanup. Logs are `artifacts/p1-rest-final-build.log`, `artifacts/p1-rest-final-tests.log`, `artifacts/p1-rest-final-focused.log` and `artifacts/p1-rest-integration-final.log`.

Final-code re-import in `artifacts/worldroot-p1-split-reimport/` also creates 19 parts, retains all original Slot/Component bindings, calls no mutation APIs and produces a zero-change initial diff. Its report is `artifacts/worldroot-p1-split-reimport-diff.json`. Both imports keep all three edit-baseline dictionaries empty; only the sandbox was applied.
