"""Minimal ResoLoop plugin command: print what the host passed to the process."""

import json
import os
import sys

VARIABLES = (
    "RESOLOOP_PLUGIN_NAME",
    "RESOLOOP_PLUGIN_COMMAND",
    "RESOLOOP_PLUGIN_ROOT",
    "RESOLOOP_PLUGIN_DATA",
    "RESOLOOP_PLUGIN_PROJECT_ROOT",
    "RESOLOOP_PLUGIN_HOST_ARGV",
    "RESOLOOP_PLUGIN_HOST_VERSION",
)


def main(argv):
    # The host does not interpret anything after the command name, so a plugin
    # that wants machine-readable output has to implement --json itself.
    env = {name: os.environ.get(name, "") for name in VARIABLES}
    if "--json" in argv:
        print(json.dumps({"argv": argv, "env": env}, ensure_ascii=False))
    else:
        for argument in argv:
            print(argument)
        for name in VARIABLES:
            print(f"{name}={env[name]}")
    return 0


if __name__ == "__main__":
    # Keep the output UTF-8 even when stdout is a pipe on a non-UTF-8 console.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main(sys.argv[1:]))
