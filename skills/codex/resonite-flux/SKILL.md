---
name: resonite-flux
description: Develop, check, build, deploy, and diagnose Git-managed ProtoGraph source with rloop and Flux-SDK.
---

# Resonite Flux

Keep ProtoFlux logic in .pg files. Confirm rloop flux status --json, the Resonite managed DLL path, and the active ResoniteLink URL.

Run flux check for fast semantic diagnostics, then flux build. Resolve the destination with find/inspect, and deploy using a verified parent ID or path. Deployment replaces only a same-named module child under that parent; unrelated children must remain untouched.

After deployment, re-inspect the parent and confirm the module appeared. For a watch loop use flux watch; keep it foreground and stop on cancellation. Do not reimplement compilation or generate ProtoFlux nodes directly—use Flux-SDK.
