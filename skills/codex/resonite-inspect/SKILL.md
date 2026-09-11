---
name: resonite-inspect
description: Inspect or locate Resonite Slots, Components, transforms, and runtime type metadata through the read-only resoloop commands.
---

# Resonite Inspect

Use --json. Confirm connection, then start with `hierarchy --depth 1 --summary --include-components --json` for IDs, names, types and reference-only boundaries without field payloads. Scope with `--under` and the appropriate `--state` for managed selectors. A finite depth does not bound a wide hierarchy's child count; prefer find --name or --component scoped with --under and --direct-children. For member evidence, use inspect --component TYPE --member NAME, which returns a flat counted component view, instead of dumping a deep world with --members. Use --exclude-reference-only when generated references are irrelevant.

Use IDs from the active session for immediate follow-up commands. For managed content after reconnect, prefer `$slot:key` or `$component:key` with the exact apply `--state` file; resoloop re-resolves path, identity fields, managed reference topology, and member identity instead of trusting a stale raw ID. Use component list and component inspect only for relevant targets. If a type or field is unclear, use type search followed by type describe; do not infer names.

For a field's value type or enum values, use `type describe COMPONENT --member FIELD --json`, for example `FrooxEngine.StaticTexture2D --member PreferredProfile`. It follows the exact reflected valueType and unwraps Nullable; non-component types may need an assembly-qualified name and are not discoverable through component search. Do not guess enum numbers or assemblies. References without a valueType require inspection of their target metadata instead.

Slot inspection includes public Slot `members` and their current-session IDs (Position, Rotation, etc.). Use these observed IDs to identify field ownership; do not infer owner IDs arithmetically. Item audit counts fields of Slots inside the inspected root as internal targets. Scene summary reports `bounds.kind`: `geometry` uses known geometry, `partial` has missing renderer extents, and `pivots` encloses origins only. These are declaration/rest bounds, not animated swept bounds. Live capture returns `ownership` with its temporary Slot parent/ID/name and cleanup result; camera creation currently uses Root.

Return the important Slot path, IDs, transforms, Components, and member evidence. Do not edit or delete content.
