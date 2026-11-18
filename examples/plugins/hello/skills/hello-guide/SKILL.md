---
name: hello-guide
description: Use when you need to try the hello example plugin or check how a ResoLoop plugin command receives its arguments and RESOLOOP_PLUGIN_* variables.
---

# hello-guide

The `hello` plugin adds one command, `hello`. It prints the arguments it received (one per line) and the `RESOLOOP_PLUGIN_*` variables.

- Run it with `resoloop [--project DIR] hello [ARGS...]`. Everything after `hello` goes to the plugin unchanged, including `--json`, `--` and empty strings.
- `resoloop hello --json` prints one JSON object, `{"argv": [...], "env": {...}}`.
- Check that the plugin is installed and unmodified with `resoloop plugin list`.
- The recipe is `hello:hello-card`. Look at it with `resoloop uix recipe describe hello:hello-card`.
