# ResoniteLink requests for non-mutating existing-content management

Issue drafts only; nothing has been posted upstream. Evidence: live Resonite `2026.9.18.82`, ResoniteLink `0.13.1.0`, client package `YellowDogMan.ResoniteLink` `0.13.1`, on 2026-10-15. The package's NuGet repository metadata pins commit `067afad3d1977b3806cbad077f4e8a534f56f451`. Public source/model documentation and live Reflection were used; no private engine implementation was copied.

Reviewed on 2026-10-16 against the current ResoLoop implementation, the pinned public API models/docs and the 2026-10-15 verification records. This is not a check of the latest upstream release. [Japanese version](RESONITELINK-IMPORT-ISSUES.ja.md).

P0/P1 already import 4,532 Slots and 8,054 Components with zero world writes and an initial zero-change diff. External/nested reference management, split-file reference resolution and explicit Slot attribute editing are implemented locally. The 19-file import also produces a zero-change diff when explicitly retaining 1,949 references and seven Slot attributes. The following limitations affect safe editing or future persistence/reconstruction; they are not blockers for that read-only import result. A private world snapshot is not needed for the proposed minimal reproductions. See [reference verification](EXISTING-REFERENCES.md) and [split-file/Slot verification](EXISTING-BUNDLES-SLOTS.md).

## 1. Persistent element identity and direct world incarnation identity

**Suggested title:** Expose world incarnation and persistent element identities for read-only tooling

**Problem:** A tool can observe duplicate sibling names without tagging or renaming the world, but cannot reliably identify the original objects after saving/reloading when their protocol IDs change. This world contains 480 duplicate sibling groups; some siblings can be structurally indistinguishable. Matching by index or mutable values is unsafe. The connection `uniqueSessionId` is not a world identity; its documentation does not guarantee uniqueness across worlds/restarts.

**Current workaround:** Use the official announcement's world session ID matched to the endpoint, then freshly check instance IDs, parent relationships and Component types. Refuse session changes and missing/ambiguous bindings. This also makes UDP discovery a prerequisite even with a known WebSocket endpoint.

**Requested API:** Return a world/session incarnation identifier directly on the connected WebSocket, with documented restart/load semantics. Expose an immutable element identity for Slots, Components and members that survives save/reload without adding content to the world. Specify behavior for duplication, world cloning, copied items, deletion/recreation and owner changes. Scope persistent IDs to an explicit world/asset identity to avoid conflating copies.

**Acceptance:** Two indistinguishable same-name siblings can be imported, saved/reloaded and uniquely reidentified. Duplicating or deleting/recreating one yields a distinct identity. A different world or incarnation is reliably rejected even when connection IDs/ports are reused.

**Sources:** [ID lifetime documentation](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/README.md#how-ids-work), [connection session response](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/Responses/SessionData.cs), [announcement model](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/ResoniteLinkSession.cs).

## 2. Reflection for existing legacy instances, with structured failure details

**Suggested title:** Reflect existing legacy Components by instance and distinguish unsupported members from missing types

**Observed:** Existing instances and their members can be read, but Reflection fails for six types:

| Runtime type (all in `[FrooxEngine]FrooxEngine`) | Observed error |
| --- | --- |
| `LegacyPanel` | Component type not found |
| `LegacyHorizontalChoiceBar` | Object reference not set to an instance of an object |
| `LegacySlider` | Same null-reference error |
| `LegacyVideoPlayer` | Same null-reference error |
| `PhotonDust.AlphaOverLifetimeLinearGradient` | Same null-reference error |
| `PhotonDust.ColorOverLifetimeLinearGradient` | Same null-reference error |

The adapter currently labels these failures `COMPONENT_TYPE_NOT_FOUND`; the error strings above are the returned diagnostic messages. That label does not prove the live instances are missing. The importer preserves them as observed, with no attempted creation or edits.

**Reproduction:** On a world containing one affected legacy instance, read its Component and exact runtime type. Request the flattened definition for that exact type via the official client. Compare the successful instance read against the failed definition request. Modern type lookup availability and ability to create a legacy type should not be prerequisites for inspecting an existing instance.

**Requested API/fix:** Support definition/capability lookup by existing instance ID, including deprecated types, or repair type-based Reflection for these cases. Return structured distinctions between type resolution failure, definition generation failure, unsupported member, and readable-but-not-creatable type. Describe per-member read/write/create support so unknown/event members are not inferred to be writable.

**Acceptance:** These existing Components return a useful partial or complete definition without an unclassified null-reference error. Unsupported members have explicit capability reasons; supported sibling fields remain manageable.

**Source:** [GetComponentDefinition contract](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/Messages/Reflection/GetComponentDefinition.cs). Runtime failure details are retained in local `snapshot.json` → `types`, or `snapshot/dependencies.json` → `types` for split imports; full private world content should not be attached to an issue by default.

## 3. Explicit opaque/event member capabilities

**Suggested title:** Expose readable metadata and supported operations for opaque SyncDelegate/event members

**Observed:** Reading the graph exposes members for which flattened Reflection has no matching entry, plus `empty` members. Examples from the prior inspection include UIX Button events (`Pressed`, `Released`, `HoverEnter`) and TextField editing events. The current import retains 467 top-level members absent from otherwise available definitions and 1,067 reflected `empty` members; these sets are not all proven to be events. Classification requires instance-aware capability metadata rather than assuming every empty member is a delegate.

**Requested API:** Identify the actual member kind and explicitly state whether its binding/target can be read and changed. For supported delegate/event bindings, expose a documented typed representation and update operation that preserves unrelated bindings. For intentionally inaccessible kinds, provide a stable reason/capability flag.

**Acceptance:** Tools can distinguish an empty value, an unsupported event, and unavailable Reflection. Round-trippable bindings can be read and changed on an isolated Component, while unsupported data is preserved rather than dropped. Triggering events and editing their bindings should have separate capabilities.

This is a request for missing metadata/operations, not an assertion that every omitted member can or should become writable.

## 4. Conditional field updates and consistent observation revisions

**Suggested title:** Add compare-and-set field updates and revision-aware subtree reads

**Problem:** Existing worlds continue running during import/diff/apply. Client-side baseline/current/desired checks detect a change before writing, but another update can occur between the last read and the write. Multi-request hierarchy snapshots are also not atomic. Re-reading structure detects structural changes but cannot prove that all captured values came from the same world revision.

**Requested API:** Conditional updates with expected member revision (or expected typed value) and a conflict response that performs no write. Include world incarnation identity. Provide revision/snapshot tokens for bounded subtree reads, or explicitly identify a read that spans changing revisions. For batches, document transaction/partial-success semantics and per-field results.

**Minimal reproduction:** An isolated numeric field is read by a client, changed by a running driver, and then written by the client using its stale baseline. Client-only checking cannot eliminate this race. The current implementation detects changes before writing and inspects afterward but does not claim atomicity.

**Acceptance:** A concurrent write results in a conflict with the newer value/revision preserved. Observation consumers can detect an inconsistent capture without adding synchronization Components to the scene.

**P1 evidence:** This applies to Component fields, references and Slot attributes. Reference updates should accept a structured source member path or member ID plus expected target/revision. The current reference-list update submits neighboring elements through the containing Component, leaving a concurrent-update window even with unchanged list length/IDs and immediate readback. Conditional per-member updates should reject a stale expectation without changing the requested element or neighbors. Client-side conflict detection and partial Slot updates are already implemented; this request is for server-side atomicity.

## 5. In-place list element removal without Component replacement

**Suggested title:** Support in-place list element removal with element identity and reference safety

**Problem:** The pinned integration has no documented operation for shortening a SyncList in place. Generated-content workflows can recreate a Component to shorten a list, but that is unsuitable for imported content because unrepresented members and external inbound references may be lost. P1 already updates reference-list elements while preserving list length, element IDs and Component identity. Imported list shrinking/recreation remains blocked; changing references in an existing list is not an upstream blocker.

**Requested API:** A documented in-place element-removal or list-shrinking operation with element identity, explicit index semantics, conditional revision support and clear handling of references to removed elements. Preserve Component identity and all unrelated fields. A reliable inbound-reference query would also help tools explain destructive edits. This draft does not establish that every insertion, value replacement or reorder operation needs a new API.

**Acceptance:** Shortening an isolated two-element list to one leaves the Component ID, unrelated state and references to retained elements unchanged; stale expected revisions fail without mutation.

**Source:** [SyncList payload](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/DataModel/SyncList.cs). This draft concerns the pinned 0.13.1 integration; check newer upstream work before filing to avoid duplicating an implemented feature.

## 6. Resolve reference targets by generic element ID, with owner/path/type metadata

**Suggested title:** Resolve any reference target ID to readable ownership and typed member metadata

**P1 observation:** The WorldRoot capture has 1,073 external references to 144 distinct targets. Exact-ID Component/Slot reads identify 143 targets. One target (`Reso_14E81` in this session, referenced by two edges) cannot be resolved by either operation. This does not establish that the target is missing: it may be a member or a non-Slot/Component runtime object. The pinned public message models provide Component/Slot reads, but no generic element-ID owner/path/type lookup. Resolving it by scanning unrelated content would be unbounded and may still miss runtime objects.

**Requested API:** A read-only exact-ID lookup returning element kind, actual/assignable type, owning Component/Slot when applicable, structured member path, lifetime scope and capability flags. Distinguish missing, unreadable, unsupported and runtime-only targets. Include Slot members and references inside SyncObjects/lists/dictionaries without requiring clients to reconstruct private Sync wrappers. Avoid adding markers or making dependencies owned.

**Minimal reproduction:** Create an isolated owned Component referring to a member of an external Component, a Slot member or a runtime User object. Read the source reference ID and resolve that ID directly without walking the entire world. A private world snapshot or the session-scoped example ID should not be required for the issue.

**Acceptance:** Tools can name and type-check these targets, verify their owners/structured paths, and distinguish unsupported from missing targets while respecting read permissions. List reordering preserves element identity evidence. A deleted target returns an explicit missing result.

**Current behavior:** P1 retains the opaque binding as a named dependency but refuses newly assigning it. Internal nested members and references across split files are already resolved locally from the captured graph and Reflection. This request is for bounded exact-ID discovery when the target's owner/path is unknown, not for symbolic reference management itself. If the owner is already known and readable, ordinary Component/Slot reads may provide the missing path locally. The one unresolved ID is evidence of the current tool's lookup limit, not proof that no existing read sequence could resolve it. See the pinned [DataModel message inventory](https://github.com/Yellow-Dog-Man/ResoniteLink/tree/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/Messages/DataModel).

## ResoLoop work, not upstream blockers

- Literal strings, large observation sidecars, duplicate-name storage, no-tag imports and baseline-vs-intent separation are addressed locally by P0.
- External dependency naming, structured nested member selectors, split-file references within one import, and explicit Slot attribute editing are implemented locally in P1. Direct references, partial SyncObjects and unchanged-length reference lists can be updated without replacing Components. Independent cross-bundle ownership and broader container writes remain ResoLoop work; do not describe those scope limits as proven upstream impossibilities. Slot `UpdateSlot` already preserves omitted fields; null-valued observed Tags are handled locally. No new upstream API is needed for this partial-update behavior. Conditional Slot updates are still part of request 4's atomicity gap.
- **SyncPlayback is already a supported upstream data model with partial update semantics.** P0 keeps its eight observed members read-only as a local scope restriction. Do not file an issue claiming playback state cannot be read/written. See the [public SyncPlayback model](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/DataModel/SyncPlayback.cs).
- Import preserves references to assets; it is not a portable asset backup or ProtoGraph reverse compiler. Those are separate workflows.
- Newly assigning a reference to a `slotMember` remains blocked by ResoLoop's target-type validation path. The public Slot model already exposes typed fields and their IDs, and existing bindings are retained. The current implementation does not prove this needs an upstream change; investigate adapter/type-evidence support locally before making an API request. This differs from issue 6's unknown-owner exact-ID lookup.
