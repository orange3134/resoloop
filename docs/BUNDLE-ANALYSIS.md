# Offline bundle analysis and dependency capture

Use imported observations to investigate a port without repeatedly reading Resonite. `bundle` commands accept an imported directory or its `manifest.json`. They verify the original snapshot and all indexed parts; single schema-1/2 snapshots and schema-3 split bundles are supported. They do not overlay requested edits or checkpoint baselines onto observations.

## Offline queries

```powershell
resoloop bundle summary content/worldroot --json
resoloop bundle tree content/worldroot --depth 3 --limit 50 --json
resoloop bundle find content/worldroot --type MeshRenderer --json
resoloop bundle find content/worldroot --text EnemySpacing --json
resoloop bundle find content/worldroot --name Gun --json
# Replace these example keys with exact keys returned by find/tree.
resoloop bundle inspect content/worldroot --key c000123 --member Enabled --json
resoloop bundle refs content/worldroot --key c000123 --direction both --depth 2 --json
resoloop bundle refs content/worldroot --key c000123 --member-path '[{"member":"Values"},{"index":0}]' --direction in --json
```

These commands never create/connect a Resonite client. A running world, matching current session or discovery is not required to read old observations. `--name` filters Slot names, `--type` filters Component type substrings, and `--text` searches literal member names/keys and observed values, case-insensitively. Text matches return member paths and values, rather than silently inferring dynamic-variable bindings. `--under` restricts tree/find/extract to an exact owned Slot key. Name-array paths are descriptive, never unique selectors: duplicate names and literal slashes stay distinct by key.

`inspect` emits an entity header followed by individual top-level members and captured type capability. `--member` selects one literal top-level name. `--member-path` accepts the same structured member/index/key steps as imported references, allowing small reads inside large lists. False, zero and explicit null values survive projection; unknown entity state is null. The `enabled` member is retained, as is local Slot `isActive`. These are observed local values, not an inferred effective visibility or current runtime state.

`refs` follows structural references in either direction, including Slot-member targets and nested member paths. Incoming queries identify which captured members reference a particular value. Cycles are visited once. Depth counts reference hops; Slot `Parent` references are included in analysis, so the reference count exceeds the Component-only count in the import report. This is not ProtoFlux interpretation or proof of runtime execution. Unknown targets, identity-only external records and explicit null references have separate statuses.

Every entity/member/edge carries a source file, JSON Pointer, snapshot SHA-256, observation time and origin (`snapshot` or `dependency`). Search results are evidence to interpret, not instructions to execute. Pointers preserve literal member names using JSON Pointer escaping. Component capability is retained for unavailable types too; missing Reflection does not discard the observed instance.

`tree`, `find`, `inspect` and `refs` use `--offset 0 --limit 50 --max-bytes 65536`. Follow `nextOffset` until absent. Results include total rows, omitted rows and a limit reason. The byte budget counts serialized item JSON, excluding the small output envelope. A single oversized row fails with `BUNDLE_ROW_TOO_LARGE` instead of silently truncating its value; narrow to a member/path or explicitly raise the budget. Limits are at most 2,000 rows and 16 MiB per query page. The loader currently validates/loads the full bundle; pagination bounds output, not loading memory. Keep the snapshot and selected sidecar unchanged between pages.

## Collect external observations

```powershell
resoloop discover --json
# Import afresh after a world restart. Use the newly observed session/URL.
resoloop import Root/WorldRoot --output content/worldroot --split-slots 256 --url ws://localhost:PORT --command-timeout 600 --json
resoloop bundle collect-dependencies content/worldroot --depth 3 --max-objects 2000 --max-bytes 16777216 --seconds 120 --url ws://localhost:PORT --command-timeout 300 --json
resoloop bundle summary content/worldroot --json
```

Only `collect-dependencies` connects. It requires the original announced world session and verifies the imported root before reading dependency IDs. Session changes reject publication. Traversal reads exposed Component members, Slot attributes and missing Component type definitions. Every nested reference is eligible; it is not restricted to a hardcoded set of material/texture member names. Cycles and duplicate targets are fetched once per capture. Root is excluded. A Slot's parent, descendants and attached Components are not automatically traversed; explicit non-parent member references can be followed. External member IDs without a readable Component/Slot remain opaque; no world-wide owner search is attempted.

Collection is read-only: no create/update/remove/call/import-asset APIs, no expansion of edit ownership, no writes to manifest/snapshot/state. It saves a separate `dependencies.capture.json`, bound to the source snapshot hash and world session. Offline queries automatically load that file when present. `--output NEW_FILE.json` preserves alternative captures; use `--dependencies FILE` to select one for analysis. Never overwrite or merge captures from different snapshots or sessions. Re-running collection requires a new output filename; it re-observes dependencies rather than assuming cached observations are current.

Records distinguish `captured`, `opaque`, `read-error`, `excluded-root`, `depth-limit`, `time-limit` and `byte-limit`. Object/depth/byte/time limits and `stopReason` are saved. Targets omitted by the object/byte cap may have no captured record; reference inspection and excerpt `unresolved` lists still expose their boundaries. Failure to read a target is not proof it does not exist. The byte cap covers the final file; overflow removes type definitions and then payloads, marking omitted payloads, before reducing metadata records if necessary. The traversal time budget excludes initial/final session verification; `--command-timeout` caps the whole command. Cancellation or session verification failure publishes no capture.

The source snapshot and later dependency observations have separate timestamps. Neither is an atomic snapshot; drivers can change values during collection. A published capture is historical evidence, not an asset backup or proof of complete portability. Asset URL fields are saved, but asset bytes, downloads, mesh conversion, semantic interpretation and platform adapters are outside this feature.

## Extract a bounded analysis excerpt

```powershell
resoloop bundle extract content/worldroot --under s000123 --include-dependencies --depth 2 --max-objects 2000 --max-bytes 16777216 --output artifacts/gun-excerpt.json --json
```

Extraction includes the selected subtree, required ancestors, and optionally outgoing reference dependencies up to the specified depth. Parent links never cause dependency traversal into unrelated world branches. Ancestors of referenced Components are included as context without their other descendants/Components. Full exposed member data and state flags are retained. References leaving the selection or pointing to uncollected/opaque dependencies are listed in `unresolved`; reaching the depth boundary never claims a complete dependency closure. Exceeding object/byte limits fails before creating a file. Output uses create-new semantics and is explicitly `kind: analysis-excerpt`, not an apply manifest. Source pointers refer to the original observations, which should be retained alongside the excerpt.

## Validation

Offline tests exercise single/split loading, literal strings/null/false/zero, provenance, reverse member references, pagination, cyclic external references, ownership isolation, session mismatch, bounds, timeout, extraction and CLI routing without a reachable endpoint. Live validation uses the user's explicitly selected WorldRoot only for observation; the source snapshot/state are preserved and empty diff must remain zero after dependency collection.

Release build completed with zero warnings/errors; all 521 offline tests passed, including 13 new bundle-analysis cases. Project initialization and bundled skill synchronization also passed.

2026-10-17 live evidence: discovery selected the reopened **曲射! Curving Bullets** session at `ws://localhost:22625/` (session-scoped, not a reusable configuration). A fresh split import retained 4,532 Slots and 8,054 Components. Dependency capture at depth 3 saved 167 external Component/Slot payloads, excluded Root, and retained one opaque target. All 179 recorded capture requests were reads. The post-capture diff had zero creates, updates, renames and deletes; its 631 recorded requests were reads. Every snapshot part still matched its original hash, and edit baselines remained empty.

Offline verification with an unreachable endpoint found `EnemySpacing`, read its saved value `0.5`, and followed an incoming nested-member reference. A gun-subtree excerpt included 1,073 entities, 1,386 reference edges and two unresolved boundary references. All 1,386 source JSON Pointers resolved to the correct saved reference members. Evidence files are local, ignored artifacts: `artifacts/bundle-live-20261017/`, `artifacts/bundle-live-diff.json` and `artifacts/bundle-verification.json`. They are not bundled into the distributable package.

A second world, **Sticker Studio**, was verified on 2026-10-17 at the user-selected `ws://localhost:24079/`. Its `Root/WorldRoot` imported in 49.4 seconds: 3,585 Slots, 8,652 Components, 296 duplicate sibling-name groups and 485 types with no unavailable Reflection definitions. Dependency capture completed in 31.3 seconds, retaining 817 payloads plus one excluded Root and one opaque target referenced by nested User members, without hitting collection limits. The subsequent diff completed in 20.0 seconds with zero creates, updates, renames or deletes. All recorded requests were reads, and the original 20 bundle files and empty edit baselines remained unchanged.

Offline commands using an unreachable endpoint found 753 Sticker-name matches, inspected a stored texture URL and found six incoming references to that texture. The Visual subtree itself contains 848 Slots and 2,488 Components, exceeding the default 2,000-object extraction budget even without dependencies; reducing reference depth does not reduce the selected subtree. Raising `--max-objects` to 5,000 allowed extraction at depth 3: 4,043 entities including 706 dependency observations, and 3,845 reference edges whose source pointers all verified. The 25 unresolved excerpt edges explicitly point to excluded Root or the opaque target. No product-code changes were needed. Local evidence is retained in `artifacts/world24079-import-20261017/` and `artifacts/world24079-verification.json`; neither contains a claim of an atomic live snapshot.
