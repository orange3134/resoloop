---
name: resonite-inspect
description: Inspect or locate Resonite Slots, Components, transforms, and runtime type metadata through the read-only rloop commands.
---

# Resonite Inspect

Use --json. Confirm connection, then start with finite-depth hierarchy output. Prefer find --name or --component scoped with --under and --direct-children. For member evidence, use inspect --component TYPE --member NAME, which returns a flat counted component view, instead of dumping a deep world with --members. Use --exclude-reference-only when generated references are irrelevant.

Use IDs from the active session for immediate follow-up commands. For managed content after reconnect, prefer `$slot:key` or `$component:key` with the exact apply `--state` file; rloop re-resolves path, identity fields, managed reference topology, and member identity instead of trusting a stale raw ID. Use component list and component inspect only for relevant targets. If a type or field is unclear, use type search followed by type describe; do not infer names.

Return the important Slot path, IDs, transforms, Components, and member evidence. Do not edit or delete content.
