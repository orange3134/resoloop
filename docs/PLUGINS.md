# ResoLoop plugins

A plugin is a package that adds commands, skills, UIX recipes and tool checks to one ResoLoop project. This guide is for people who install plugins ([For users](#for-users)) and people who write them ([For authors](#for-authors)). It describes what the code does today, not what it may do later.

## Overview

A plugin can provide any of these four things. None of them is required: a package with only a manifest is valid.

| Feature | What it does | Manifest field |
| --- | --- | --- |
| External commands | `resoloop [--project DIR] COMMAND [ARGUMENTS...]` runs a program the plugin declares. Everything after `COMMAND` goes to that program unchanged. | `commands` |
| Skill distribution | `resoloop init` and `resoloop skills sync` copy the plugin's skill directories to `<project>/.agents/skills/<skill>/`. | `skills` |
| UIX recipes | `resoloop uix recipe list`, `describe` and `export` and the `$recipe` key in declaration files accept `<plugin-name>:<recipe-name>`. | `recipes` |
| Tool checks | `resoloop doctor` checks that the external tools a plugin needs are on `PATH` and reports them as `plugin:<plugin-name>:<tool-name>`. | `tools` |

Things to know first:

- **Plugins are a `feat/plugin-v2` feature.** They exist in the `feat/plugin-v2` branch of `usaturn/resoloop`. The ResoLoop 0.2.0 published on NuGet has no plugin support. A `feat/plugin-v2` build also reports `0.2.0` from `resoloop version`, so tell the two apart with `resoloop help`: only a build with plugin support has the `plugin install` line.
- **Plugins belong to one project.** A project is a directory that has a `.resoloop.json` (`resoloop init` creates it). A plugin is installed into `<project>/.resoloop/plugins/<name>/`, never globally.
- **Installing does not run plugin code.** Installing only copies files (or exports them from Git objects), validates the manifest and records SHA-256 hashes. Plugin code runs only when you run one of its commands.
- **Plugin commands run with your permissions.** A plugin is neither signed nor sandboxed. Read [Safety and limits](#safety-and-limits) before installing one you did not write.
- **Platforms.** Plugin commands run only on Linux and Windows. Everything in this guide was run on Linux. Windows was not run.

A minimal example that uses all four features is in [`examples/plugins/hello/`](../examples/plugins/hello/). The Quick start below installs it.

## Quick start

> Verified with ResoLoop 0.2.0 on feat/plugin-v2 (Linux). The outputs below were produced by running the commands in this order. Values that depend on the machine are replaced as listed here, and nothing else is edited. A line that contains only `…` stands for omitted lines.

| Placeholder | Stands for |
| --- | --- |
| `<project>` | Absolute path of your ResoLoop project (`$PROJECT`) |
| `<source>` | Absolute path of the plugin source directory (`$SOURCE`) |
| `<source-url>` | The `file://` URL of the Git copy of the plugin, percent-encoded |
| `<sha>` | A full Git commit SHA (40 hexadecimal characters) |
| `<sha256>` | A SHA-256 digest (64 hexadecimal characters) |
| `<uv-path>` | Absolute path of the `uv` found on `PATH` |
| `<uv-version-line>` | The first line printed by `uv --version` |
| `<python-version>`, `<python-path>` | The Python that `uv` picked |
| `<host-argv>` | The JSON array of strings that starts ResoLoop. It depends on how ResoLoop was started: one element for an installed tool or apphost, two for `dotnet resoloop.dll`. |

Prerequisites:

- A `resoloop` from `feat/plugin-v2` on your `PATH`. If you build it from a checkout (`dotnet build ResoLoop.slnx`), a wrapper script named `resoloop` containing `exec dotnet "<checkout>/src/RLoop.Cli/bin/Debug/net10.0/resoloop.dll" "$@"` is enough.
- `git` on `PATH`. The installer always uses it, also to classify a local directory.
- `uv` on `PATH`. The example's command runs through `uv`, which looks for a Python 3.10 or later and may download one when it finds none.
- `jq` is optional. The commands below use it only to pick fields out of JSON output.

The commands use shell variables, and every use is quoted, so paths with spaces or non-ASCII characters work.

**1. Make a project.** Use an existing project, or create one:

```bash
PROJECT="$HOME/worlds/my world"
resoloop init "$PROJECT"
```

```output
initialized <project>
  created   .resoloop.json
  created   content/main.json
…
```

**2. Copy the example to a work directory.** The example sits inside the ResoLoop repository, and `plugin install` accepts a Git directory only when it is the root of a work tree, so a subdirectory of that repository is refused with `PLUGIN_SOURCE_INVALID`. Copy it to a directory that is outside any Git repository and does not overlap the project:

```bash
RESOLOOP_REPO=/path/to/resoloop     # your checkout of usaturn/resoloop at feat/plugin-v2
SOURCE="$(mktemp -d)/hello"
cp -r "$RESOLOOP_REPO/examples/plugins/hello" "$SOURCE"
```

If you must keep the copy inside another Git repository, run `git init` in the copy and pass the copy's directory (its own root).

**3. Install it.**

```bash
resoloop plugin install "$SOURCE" --project "$PROJECT"
```

```output
installed hello 1.0.0
  <project>/.resoloop/plugins/hello
```

**4. List the installed plugins.**

```bash
resoloop plugin list --project "$PROJECT"
```

```output
hello  1.0.0
```

**5. Run the plugin command.** The command prints every argument it received, one per line (an empty argument is an empty line), then the `RESOLOOP_PLUGIN_*` variables:

```bash
resoloop --project "$PROJECT" hello one "two words" "" -- --project=x "日本語 引数"
```

```output
one
two words

--
--project=x
日本語 引数
RESOLOOP_PLUGIN_NAME=hello
RESOLOOP_PLUGIN_COMMAND=hello
RESOLOOP_PLUGIN_ROOT=<project>/.resoloop/plugins/hello/package
RESOLOOP_PLUGIN_DATA=<project>/.resoloop/plugins/hello/data
RESOLOOP_PLUGIN_PROJECT_ROOT=<project>
RESOLOOP_PLUGIN_HOST_ARGV=<host-argv>
RESOLOOP_PLUGIN_HOST_VERSION=0.2.0
```

Only `--project` may come before the command name. `--`, `--project=x` and the empty string above reached the plugin untouched. The first run also prints two progress lines from `uv` on stderr (the output above is stdout and does not change):

```output
Using CPython <python-version> interpreter at: <python-path>
Creating virtual environment at: .resoloop/plugins/hello/data/.venv
```

**6. Distribute the plugin's skill.** Before the first sync, `--check` fails, because the plugin's skill file is missing from `.agents/skills/`:

```bash
resoloop skills sync "$PROJECT" --check
```

```output
SKILL_SYNC_REQUIRED: Bundled skills or their lock need synchronization.
  next: Review the reported paths, then run resoloop skills sync --update.
```

The message talks about bundled skills only. Here the pending file is the plugin's (see `--json` below). Now write it:

```bash
resoloop skills sync "$PROJECT" --update
```

```output
skills update: synchronized=True
  current              .agents/skills/resonite-build/SKILL.md
…
```

The text output lists only the bundled skills. The plugin's result is in the `plugins` key of the JSON output:

```bash
resoloop skills sync "$PROJECT" --check --json | jq -c .data.plugins
```

```output
{"mode":"check","synchronized":true,"updated":[],"files":[{"plugin":"hello","skill":"hello-guide","path":".agents/skills/hello-guide/SKILL.md","status":"current","packageHash":"<sha256>","installedHash":"<sha256>","recordedHash":"<sha256>","recordedPlugin":"hello"}],"retained":[]}
```

(`jq` is used here only to pick one key.) The distributed file has the same bytes as the file in the package:

```bash
cmp "$PROJECT/.agents/skills/hello-guide/SKILL.md" "$SOURCE/skills/hello-guide/SKILL.md" && echo identical
```

```output
identical
```

**7. Look at the plugin's UIX recipe.** Plugin recipes are named `<plugin-name>:<recipe-name>`:

```bash
resoloop uix recipe describe hello:hello-card --project "$PROJECT"
```

```output
{
  "name": "hello:hello-card",
  "prototype": "hello.card",
  "purpose": "A Slot with a RectTransform and a Text that says hello.",
  "parameters": {
    "key": "Unique stable key prefix (also the default Slot name)."
  },
  "ports": {},
  "requirements": []
}
```

**8. Run doctor.** Doctor checks the plugin's declared tools (here `uv`) next to the built-in checks:

```bash
resoloop doctor --project "$PROJECT"
```

```output
…
[pass] plugin:hello:uv: <uv-path> <uv-version-line>
…
```

The built-in items and the final `ready` line depend on your environment (without a ResoniteLink URL, `resonite-link-url` fails and doctor ends with `not ready: resolve required checks above`). The exit code is 0 either way. Read the `plugin:*` items separately. See [Doctor](#doctor).

**9. Remove the plugin.** `plugin remove` needs `--yes`, because it deletes the plugin's `data/` directory too:

```bash
resoloop plugin remove hello --project "$PROJECT"
```

```output
CONFIRMATION_REQUIRED: Removing a plugin requires explicit confirmation.
```

```bash
resoloop plugin remove hello --yes --project "$PROJECT"
```

```output
removed hello
```

The installation directory is gone, and the skill that was distributed to `.agents/skills/` stays, recorded as retained:

```bash
resoloop plugin list --project "$PROJECT"
ls "$PROJECT/.agents/skills/hello-guide"
resoloop skills sync "$PROJECT" --check --json | jq -c .data.plugins.retained
```

```output
No plugins installed in <project>
SKILL.md
[{"plugin":"hello","path":".agents/skills/hello-guide/SKILL.md","recordedHash":"<sha256>"}]
```

**10. Install from a Git URL (optional).** A Git source needs a URL and `--ref`. Make a Git copy of the example and install it through a `file://` URL (`git commit` needs `user.name` and `user.email` to be configured):

```bash
GITSRC="$(mktemp -d)/hello"
cp -r "$SOURCE" "$GITSRC"
git -C "$GITSRC" init -q -b main
git -C "$GITSRC" add .
git -C "$GITSRC" commit -q -m "hello plugin"
resoloop plugin install "file://$GITSRC" --ref main --project "$PROJECT" --json
```

```output
{"ok":true,"data":{"name":"hello","version":"1.0.0","installDirectory":"<project>/.resoloop/plugins/hello","source":{"kind":"git","location":"<source-url>","ref":"main"},"commit":"<sha>"}}
```

`commit` is the commit that `--ref` resolved to. See [Verifying what you installed](#verifying-what-you-installed) for how to use it.

## For users

### Choosing the project

- `--project DIR` names the project directory. It must contain `.resoloop.json`, or the command stops with `PLUGIN_PROJECT_NOT_FOUND`.
- Without `--project`, `plugin install`, `plugin list`, `plugin remove`, plugin commands, `doctor` and `uix recipe` search for `.resoloop.json` from the current directory upward.
- `init [DIRECTORY]` and `skills sync [DIRECTORY]` use exactly the directory you give (default: the current directory) and never search upward. If that directory is not the project root, the plugins' skills are silently not distributed.
- A declaration file that uses a plugin recipe (`validate`, `diff`, `apply` and so on) finds its project from the directory of the file, upward. `--project` is not consulted.
- For a plugin command, `--project` is accepted only before the command name, and only once (`--project DIR` or `--project=DIR`).

### Installing

```output
  resoloop plugin install SOURCE [--ref GIT_REF] [--project DIR] [--json]
```

`SOURCE` is one of two kinds.

**A local directory.** It must be either the root of a Git work tree or a directory that is not inside any Git repository.

- A Git work tree installs the tracked files plus untracked files that are not ignored (`git ls-files --cached --others --exclude-standard`). Ignored files (for example `.venv/`) are not installed.
- A directory outside Git installs every file. A `.git` anywhere inside it is refused, not skipped.
- A subdirectory of a Git work tree is refused with `PLUGIN_SOURCE_INVALID` (`A local Git source must be the working tree root.`), and so is a bare repository. Copy the package out, or run `git init` in the copy.
- The source directory and the project directory must not contain one another (`PLUGIN_SOURCE_INVALID`: `Plugin source and project directories must not overlap.`). The same applies to a `file://` URL.
- `--ref` is not allowed with a directory.
- The copy is a snapshot of the files at that moment. The record has the source directory but no revision (`commit` is `null`).

**A Git URL, with `--ref`.**

- Accepted URL forms are `http://`, `https://`, `ssh://`, `git://` and `file://` URLs, and scp-style `[user@]host:path`. `--ref` is required for every one of them, `file://` included (`PLUGIN_GIT_REF_REQUIRED`).
- Do not put credentials in the URL. Userinfo is refused on `http`, `https`, `git` and `file` URLs, and a password is refused on `ssh` URLs. A query string or fragment is refused too. Configure authentication outside ResoLoop (an SSH agent, a credential helper). The installer never prompts.
- `--ref` is a branch, a tag, `HEAD`, a full ref name or a commit SHA. If a short name matches more than one ref and they point to different commits, the install fails with `PLUGIN_SOURCE_INVALID` and asks for a full ref name. To pin an exact revision, use the full commit SHA.
- The installer clones the repository bare into a staging directory with hooks disabled, then exports the files of the resolved commit from the Git objects. It never checks out the tree or runs package code. Only regular files are exported (mode 100644 or 100755): symlinks, submodules and any path with a `.git` component are refused.
- ResoLoop records the commit that the ref resolved to. It never follows the branch afterwards.
- Each Git operation has a 120 second limit.

Whatever the source, the installer does the same steps in a staging directory under `.resoloop/plugins/`: copy or export, read and validate the manifest, check the ResoLoop version, check names, write `install.json`, verify the result, and finally publish it with one rename. A failed install removes only its own staging directory. It never changes an installation that already exists.

### Where things are stored

```
<project>/.resoloop/plugins/
├── .gitignore            (contains "*": nothing under plugins/ is committed)
├── .lock                 (the lock that serializes installs, removals and plugin use)
└── <name>/
    ├── install.json      (the record: source, commit, installedAt, file hashes)
    ├── package/          (the plugin's files; verified by hash before every use)
    └── data/             (the plugin's persistent working directory; not verified)
```

`install.json` has `schemaVersion`, `name`, `version`, `source` (`kind` is `directory` or `git`, plus `location` and `ref`), `commit`, `installedAt` (UTC), `resoloopVersion` and `files` (the SHA-256 of every file in `package/`). The `source.location` of a Git source never contains credentials. If `plugins/.gitignore` already exists with different content, the install stops with `PLUGIN_IGNORE_CONFLICT` and leaves your file alone.

### Listing

```output
  resoloop plugin list [--project DIR] [--json]
```

`plugin list` verifies every installed plugin and prints one line each. A plugin that fails verification is still listed, with its problem, and the exit code stays 0:

```output
hello  PROBLEM PLUGIN_MODIFIED: Package content differs from the install record.
```

`--json` prints `{"ok":true,"data":[{"name":"hello","ok":true,"version":"1.0.0"}]}`. A broken plugin has `"ok":false` and a `problem` object with `code`, `message` and `suggestions`. With nothing installed the text output is `No plugins installed in <project>`. `plugin list` does not show commits (see [Verifying what you installed](#verifying-what-you-installed)).

### Updating and removing

```output
  resoloop plugin remove NAME --yes [--project DIR] [--json]   (also deletes the plugin's data/ directory)
```

- **There is no in-place update.** Installing a name that is already installed fails with `PLUGIN_ALREADY_INSTALLED`. To update, remove the plugin and install it again.
- **`plugin remove` deletes `<project>/.resoloop/plugins/<name>/` as a whole, `data/` included.** Whatever the plugin keeps there (caches, a virtual environment, saved results) is gone. Copy what you need first.
- Without `--yes`, the command fails with `CONFIRMATION_REQUIRED` (exit 6) and changes nothing.
- A plugin whose record or package is broken can still be removed.
- Links inside `data/` are deleted as links. What they point to is never touched.
- **Skills that were already distributed stay** in `.agents/skills/<skill>/`. ResoLoop does not delete files it copied. `skills sync --json` lists them under `plugins.retained`.

### Running plugin commands

```output
  resoloop [--project DIR] PLUGIN_COMMAND [ARGUMENTS...]   (arguments after the command name go to the plugin unchanged)
```

- Only `--project` may come before the command name. The first other token is the command name, and everything after it, including `--json`, `--`, empty strings and `--project=x`, goes to the plugin unchanged. ResoLoop does not switch to JSON output for a plugin command. A plugin that offers `--json` implements it itself.
- Built-in command names always win. A name that is neither built-in nor declared by an installed plugin fails with `UNKNOWN_COMMAND` (exit 2).
- Plugin commands never connect to Resonite.
- The program's stdout and stderr are passed through unchanged, and its exit code becomes ResoLoop's exit code. On Linux a program killed by a signal gives `128` plus the signal number. Errors of ResoLoop itself (`CODE: message`) go to stderr.
- A command that runs longer than its deadline is stopped, and ResoLoop exits with `PLUGIN_COMMAND_TIMEOUT` (exit 8). The deadline is the command's `timeoutSeconds` in the manifest. Without one it is `commandTimeoutSeconds`: the environment variable `RESOLOOP_COMMAND_TIMEOUT_SECONDS` first, then `commandTimeoutSeconds` in the project's `.resoloop.json`, then in `~/.resoloop/config.json`, and 900 seconds when none is set. The `--command-timeout` option does not apply, because options after the command name belong to the plugin.
- Ctrl+C, SIGTERM, SIGHUP and SIGQUIT stop the command's whole process tree and end with `PLUGIN_COMMAND_CANCELLED` (exit 7). The process tree is also stopped when the command ends, so a plugin cannot leave background processes behind.
- The project's plugins are verified before every call. If **any** installed plugin of the project fails verification, **every** plugin command, skill distribution and plugin recipe lookup of that project refuses to run with the first problem's code (for example `PLUGIN_MODIFIED: Installed plugins failed verification.`, exit 6). Run `plugin list` to see which plugin is broken, then remove or reinstall it.
- While a plugin command runs, ResoLoop holds the project's plugin lock. In the same project, `plugin install`, `plugin list`, `plugin remove`, `uix recipe list` and the other operations that read the plugin area fail at once with `PLUGIN_PROJECT_BUSY` (exit 7). They do not wait.

### Skills

```output
  resoloop init [DIRECTORY] [--json]
…
  resoloop skills sync [DIRECTORY] (--check | --update) [--json]
```

`init` and `skills sync` copy the skill directories of the verified plugins to `<project>/.agents/skills/<skill>/` (every file under the manifest's `skills[].path`). `.agents/skills/.resoloop-plugins.json` records which plugin owns each copied file and its SHA-256. The status of each file is:

| Status | Meaning | `--update` (and `init`) |
| --- | --- | --- |
| `missing` | The file is not there yet. | Created. |
| `current` | The file equals the package file and the record. | Nothing to do. |
| `update-available` | The package changed, and your copy is still what ResoLoop distributed. | Overwritten by `skills sync --update`. `init` never overwrites an existing file, so it reports a conflict. |
| `record-update-required` | The file equals the package file, but the record is missing or differs. | The record is rewritten. |
| `modified-conflict` | You edited the file after it was distributed. | Refused. |
| `untracked-conflict` | A different file with that name exists and was never distributed. | Refused. |
| `owner-conflict` | The record says another plugin owns the file. | Refused. |
| `unsafe-path-conflict` | The destination is a link, a special file, or sits where a file or directory is in the way. | Refused. |

Any conflict stops `skills sync` with `SKILL_SYNC_CONFLICT` (and `init` with `INIT_FILE_EXISTS`) before any file is written. ResoLoop never overwrites a skill you edited. A record entry is never deleted: files of a plugin that was removed, or that no longer declares them, are reported under `plugins.retained` and left in place.

The result for plugins is not printed in the text output of `init` and `skills sync`. Use `--json` and read `data.plugins`: `mode`, `synchronized`, `updated`, `files` (one entry per distributed file with `plugin`, `skill`, `path`, `status` and the hashes) and `retained`. `data.plugins` is absent when the project has no plugin skills and no record.

The text of `SKILL_SYNC_REQUIRED` (`Bundled skills or their lock need synchronization.`) was written for the bundled skills. It is also what you get when only a plugin's skill is pending, as in step 6 of the Quick start. Read `--json` to see which files.

### UIX recipes

```output
  resoloop uix recipe list [--project DIR]
  resoloop uix recipe describe NAME [--project DIR]   (plugin recipes: PLUGIN:RECIPE)
  resoloop uix recipe export NAME --output NEW_FILE.json [--project DIR] (offline structural prototypes; no visual defaults)
```

- A plugin recipe is named `<plugin-name>:<recipe-name>`. `uix recipe list` shows the built-in recipes followed by the plugin recipes. A name without `:` is always a built-in recipe.
- `describe` prints `name`, `prototype`, `purpose`, `parameters`, `ports` and `requirements`. `export` writes the recipe's prototypes to a **new** file (an existing file is never overwritten) and prints `name`, `output` and `prototype`:

  ```bash
  resoloop uix recipe export hello:hello-card --output "$PROJECT/content/recipes/hello-card.json" --project "$PROJECT"
  ```

  ```output
  {
    "name": "hello:hello-card",
    "output": "<project>/content/recipes/hello-card.json",
    "prototype": "hello.card"
  }
  ```

- You can use a recipe directly in a declaration file with `$recipe` and `$with`:

  ```json
  {
    "schemaVersion": "1",
    "ownership": { "key": "hello-example" },
    "slot": { "key": "root", "name": "ResoLoop_Test_Hello", "parent": "Root" },
    "children": [
      { "$recipe": "hello:hello-card", "$with": { "key": "greeting" } }
    ]
  }
  ```

  Save it inside the project, for example as `content/hello.json`, and check it offline:

  ```bash
  resoloop validate "$PROJECT/content/hello.json"
  ```

  ```output
  {
    "valid": true,
    "schemaVersion": "1",
    "slots": 2,
    "components": 2,
    "references": 0,
    "strict": false,
    "issues": []
  }
  ```

  `validate --strict` also needs a ResoniteLink connection (without a URL it fails with `RESONITE_LINK_URL_MISSING`, exit 3).
- `$with` must contain exactly the parameters the recipe declares (`APPLY_RECIPE_PARAMETERS` for a missing or unknown one). `$with.key` may use only ASCII letters, digits, `-` and `_` (`APPLY_RECIPE_KEY_INVALID`). The Slot and Component keys of the instance get the prefix `uix-plugin--<plugin-name>--<recipe-name>--`, so `greeting` above becomes `uix-plugin--hello--hello-card--greeting`. `$recipe` cannot be combined with `$prototype` or `$instance`. Other fields next to `$recipe` override the recipe's.
- Unknown names fail with `UIX_RECIPE_NOT_FOUND` (exit 5). A recipe of a plugin that is not installed fails with `PLUGIN_NOT_INSTALLED` (exit 5).

### Doctor

```output
  resoloop doctor [--url ws://localhost:PORT] [--project DIR] [--json]
```

- `doctor` checks the plugins' tools only when you run it. Nothing else runs a tool probe, and doctor never installs or repairs anything.
- For every tool in a plugin's manifest, it looks up `executable` in the absolute `PATH` entries and, when `versionArgs` is not empty, runs it with those arguments in the project directory (30 second limit, 64 KiB output limit). A plugin that fails verification shows up as one failing `plugin:<plugin-name>` item with the problem code.
- Each item is named `plugin:<plugin-name>:<tool-name>`:

  | Status | When |
  | --- | --- |
  | `pass` | The tool was found, and the version command exited with 0. The message is `<path> <first line of output>`. |
  | `fail` | The tool is `required` (the default) and is missing, exited non-zero, or did not finish in time. |
  | `warning` | The same problems, for a tool declared with `"required": false`. |

  The `next:` line is the tool's `hint`, or `Install <executable> and make it available on PATH.` without one.
- `--json` gives each item as `{"name","status","required","message"}` (plus `suggestion` when there is one). For the example plugin:

  ```bash
  resoloop doctor --project "$PROJECT" --json | jq -c '.data.checks[] | select(.name == "plugin:hello:uv")'
  ```

  ```output
  {"name":"plugin:hello:uv","status":"pass","required":true,"message":"<uv-path> <uv-version-line>"}
  ```
- **How to read it.** `ready` is true only when every required item passes, built-in items included. Doctor exits with 0 even when `ready` is false, so the exit code alone is not a success signal. Judge the plugin's tools by their own `plugin:*` items. A failing built-in item, such as a missing ResoniteLink URL, makes `ready` false without saying anything about the plugin's tools. A `warning` never changes `ready`.
- A missing tool looks like this. Install a temporary copy of the example whose `tools` has one more entry into a scratch project (`$SCRATCH`, made with `resoloop init`, without `hello` installed). The extra entry is not part of the example:

  ```bash
  MISSING="$(mktemp -d)/hello"
  cp -r "$SOURCE" "$MISSING"
  jq '.tools += [{"name": "missing-tool", "executable": "resoloop-hello-missing-tool", "versionArgs": ["--version"], "required": true}]' \
    "$SOURCE/resoloop-plugin.json" > "$MISSING/resoloop-plugin.json"
  resoloop plugin install "$MISSING" --project "$SCRATCH"
  resoloop doctor --project "$SCRATCH"
  ```

  ```output
  …
  [pass] plugin:hello:uv: <uv-path> <uv-version-line>
  [fail] plugin:hello:missing-tool: 'resoloop-hello-missing-tool' was not found in an absolute PATH entry.
    next: Install resoloop-hello-missing-tool and make it available on PATH.
  not ready: resolve required checks above
  ```

## For authors

### Package layout

A package is a directory whose root has `resoloop-plugin.json`. The example is:

```
hello/
├── resoloop-plugin.json     manifest
├── pyproject.toml           a virtual uv project (no dependencies, nothing to build)
├── uv.lock                  pinned by `uv lock`; the command runs with `--locked`
├── hello.py                 the command: prints its arguments and RESOLOOP_PLUGIN_* variables
├── skills/hello-guide/SKILL.md
└── recipes/hello-card.json
```

For a Git source, the root of the repository is the root of the package, so `resoloop-plugin.json` must be at the repository root. Everything in the tree of the chosen commit becomes part of the package.

### The manifest

`resoloop-plugin.json` is UTF-8 JSON of at most 256 KiB. Unknown properties and duplicate properties are errors. Validation collects every problem and reports them together in `PLUGIN_MANIFEST_INVALID`, with entries such as `$.commands[0].program: Required property.` in the error's `context.errors` (use `--json` to see them).

Top level:

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `schemaVersion` | integer | yes | Must be `1`. Any other value is `PLUGIN_SCHEMA_UNSUPPORTED`. |
| `name` | string | yes | A [name](#names). |
| `version` | string | yes | A SemVer 2.0 version. |
| `resoloop` | object | yes | See [Compatible ResoLoop versions](#compatible-resoloop-versions). |
| `description` | string | no | 1 to 500 characters. |
| `commands` | array | no | At most 50 items. |
| `skills` | array | no | At most 50 items. |
| `recipes` | array | no | At most 50 items. |
| `tools` | array | no | At most 20 items. |

`commands[]`:

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `name` | string | yes | A [name](#names). Unique among all installed plugins. |
| `description` | string | yes | 1 to 500 characters. |
| `program` | string | yes | Either a bare command name (`[A-Za-z0-9._+-]+`, looked up in the absolute `PATH` entries) or `./path/in/the/package`. |
| `args` | string array | no | Passed before the user's arguments. `${pluginRoot}` and `${pluginData}` are replaced; any other `${...}`, or an unclosed `${`, is invalid. |
| `timeoutSeconds` | integer | no | 1 to 86400. Without it the deadline is `commandTimeoutSeconds` (default 900). |

`skills[]`: `name` (a name, required) and `path` (required): a package-relative directory that contains `SKILL.md`.

`recipes[]`:

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `name` | string | yes | A [name](#names). |
| `path` | string | yes | A package-relative JSON file with a top-level `prototypes` object. |
| `prototype` | string | yes | 1 to 100 characters. It must be an object inside `prototypes`. |
| `purpose` | string | yes | 1 to 500 characters. |
| `parameters` | object of strings | yes | Parameter name to description. It must contain `key`. |
| `ports` | object of strings | no | Port name to description. |
| `requirements` | string array | no | Notes for the user. |

`tools[]`:

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `name` | string | yes | A [name](#names). |
| `executable` | string | yes | A command name without separators (`[A-Za-z0-9._+-]+`). |
| `versionArgs` | string array | no | Arguments that make the tool print its version. Empty or absent: doctor only checks that the tool exists. |
| `required` | boolean | no | Default `true`. |
| `hint` | string | no | 1 to 500 characters. Shown as `next:` when the check fails. |

#### Names

Plugin, command, skill, recipe and tool names share one rule: 2 to 40 characters, starting with a lowercase letter, then lowercase letters and digits, with parts separated by single hyphens (`[a-z][a-z0-9]*(-[a-z0-9]+)*`). Names are unique within their array, ignoring case. The following are refused with `PLUGIN_NAME_CONFLICT`:

- a plugin name equal to a built-in command or bundled skill name;
- a command name equal to a built-in command name or to a command of another installed plugin (built-in names include `apply`, `doctor`, `init`, `plugin`, `skills`, `uix`, `validate` and the rest of `resoloop help`);
- a skill name that starts with `resonite-`, equals a bundled skill name, or equals a skill of another installed plugin;
- a recipe name equal to a built-in recipe (`button`, `boolean-state`, `scroll-content`, `value-state`, `text-input`, `toggle`, `choice`, `slider`). Plugin recipes are otherwise namespaced by the plugin name.

#### Paths

Every package-relative path (in the manifest and every file in the package) must be portable: `/` separators; no empty, `.` or `..` parts; no part that ends with `.` or a space; none of `\ : < > " | ? *` or control characters; no Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `CONIN$`, `CONOUT$`, `COM1` to `COM9`, `LPT1` to `LPT9`); and no two paths that differ only in case. The package must not contain links or special files (FIFOs, sockets, devices). A package has at most 10,000 files and 200 MiB in total.

#### Compatible ResoLoop versions

```json
"resoloop": { "minVersion": "0.2.0" }
```

`minVersion` is required. `maxVersionExclusive` is optional and must be greater than `minVersion`; leave it out unless you know that a later version breaks the plugin. It is exclusive: a limit equal to the running ResoLoop's version excludes that version itself, so a plugin whose limit is the current version does not install on it. Versions are compared as SemVer 2.0 versions (`0.2.0-beta.1` is lower than `0.2.0`; build metadata after `+` is ignored in ordering). The check runs at install time and again each time the plugin is verified, so a plugin stops working when ResoLoop leaves its range (`PLUGIN_INCOMPATIBLE_VERSION`).

### Commands

ResoLoop starts a command like this:

- **No shell.** The program is started directly. `program` as a bare name is looked up in the absolute entries of `PATH` (relative and empty entries are skipped). A `./path` program is a regular file inside `package/`, and on Linux it must have its executable bit (mode 100755 in Git; local copies keep the mode). On Windows only `.exe` files are launched, so for scripts call an interpreter on `PATH` and pass the script with `${pluginRoot}`.
- **Arguments.** `args` with `${pluginRoot}` (absolute path of `package/`) and `${pluginData}` (absolute path of `data/`) replaced in one pass, then the user's arguments appended exactly as typed.
- **Working directory.** The project root.
- **Environment.** The inherited environment without any `RESOLOOP_PLUGIN_*` variable, plus:

  | Variable | Value |
  | --- | --- |
  | `RESOLOOP_PLUGIN_NAME` | The plugin's name. |
  | `RESOLOOP_PLUGIN_COMMAND` | The command's name. |
  | `RESOLOOP_PLUGIN_ROOT` | Absolute path of `package/`. Treat it as read-only. |
  | `RESOLOOP_PLUGIN_DATA` | Absolute path of `data/`. Writable, persistent between calls, deleted by `plugin remove`. |
  | `RESOLOOP_PLUGIN_PROJECT_ROOT` | The project root (also the working directory). |
  | `RESOLOOP_PLUGIN_HOST_ARGV` | A JSON array of strings: the argv that started this ResoLoop, so a plugin can call ResoLoop back. It depends on how ResoLoop was started. |
  | `RESOLOOP_PLUGIN_HOST_VERSION` | The running ResoLoop's version, for example `0.2.0`. |
  | `PYTHONPYCACHEPREFIX` | `<data>/.cache/python` |
  | `UV_PROJECT_ENVIRONMENT` | `<data>/.venv` |
- **Streams and exit code.** stdout and stderr go straight to the user. stdin is passed through when it is redirected, and a terminal becomes empty input. The exit code is returned unchanged. A command that is cancelled or exceeds its deadline never counts as a success: ResoLoop reports `PLUGIN_COMMAND_CANCELLED` or `PLUGIN_COMMAND_TIMEOUT` even if the program exits with 0 while being stopped.
- **`--json` is yours.** ResoLoop passes `--json` through like any other token and does not parse it. If your command should print JSON, implement that. The example does. Here the argument order is preserved and the values are strings:

  ```bash
  resoloop --project "$PROJECT" hello --json "two words" "" -- --project=x
  ```

  ```output
  {"argv": ["--json", "two words", "", "--", "--project=x"], "env": {"RESOLOOP_PLUGIN_NAME": "hello", "RESOLOOP_PLUGIN_COMMAND": "hello", "RESOLOOP_PLUGIN_ROOT": "<project>/.resoloop/plugins/hello/package", "RESOLOOP_PLUGIN_DATA": "<project>/.resoloop/plugins/hello/data", "RESOLOOP_PLUGIN_PROJECT_ROOT": "<project>", "RESOLOOP_PLUGIN_HOST_ARGV": "<host-argv>", "RESOLOOP_PLUGIN_HOST_VERSION": "0.2.0"}}
  ```

  (In JSON, `RESOLOOP_PLUGIN_HOST_ARGV` is the JSON array encoded again as a string.)
- **Process tree.** The command runs in its own process group (Linux) or job (Windows). When it ends, however it ends, everything left in that group is stopped, so do not rely on background processes.
- **Calling ResoLoop back.** While your command runs, ResoLoop holds the project's plugin lock. A callback that needs the plugin area (`plugin` subcommands, another plugin command, `skills sync`, `doctor`, `uix recipe`, or a document that uses a plugin recipe) fails with `PLUGIN_PROJECT_BUSY`.

#### Keep `package/` unchanged

`package/` is hashed before every use, and a changed file makes the plugin unusable (`PLUGIN_MODIFIED`). So a command must not write into `RESOLOOP_PLUGIN_ROOT`: no virtual environment, no `__pycache__`, no build output. Write into `RESOLOOP_PLUGIN_DATA` instead. For other runtimes, point their cache and build directories there.

The example does this with Python and uv:

```json
"program": "uv",
"args": ["run", "--project", "${pluginRoot}", "--locked", "python", "${pluginRoot}/hello.py"]
```

- ResoLoop sets `UV_PROJECT_ENVIRONMENT` to `<data>/.venv`, so uv creates the virtual environment in `data/`. It sets `PYTHONPYCACHEPREFIX` to `<data>/.cache/python`, so bytecode goes to `data/` too.
- `pyproject.toml` declares `[tool.uv] package = false`, a virtual project. uv installs no package and builds nothing, so nothing is created inside `package/` (a built package would leave artifacts such as `*.egg-info` there).
- `--locked` makes uv fail instead of rewriting `uv.lock` when it is out of date. Run `uv lock` in your source directory after you edit `pyproject.toml`, and commit `uv.lock`.
- Do not run `uv run` or `uv sync` inside the source directory you install from. They create a `.venv` there, which a Git-less install would copy. List `.venv/` in `.gitignore` for a Git source, or test from a scratch copy.

ResoLoop installs no dependencies for you. Tell your users what to install (here: `uv`) in your plugin's own documentation, and declare it in `tools` so `doctor` can check it.

### Skills

```json
"skills": [{ "name": "hello-guide", "path": "skills/hello-guide" }]
```

`skills/hello-guide/SKILL.md` must exist. Every file under `path` is distributed to `.agents/skills/hello-guide/`, keeping its relative path. Write `SKILL.md` with the front matter your agent expects (`name` and `description`, as in the example), and use the manifest entry's name as its `name`. See [Names](#names) for the reserved skill names.

### Recipes

```json
"recipes": [{
  "name": "hello-card",
  "path": "recipes/hello-card.json",
  "prototype": "hello.card",
  "purpose": "A Slot with a RectTransform and a Text that says hello.",
  "parameters": { "key": "Unique stable key prefix (also the default Slot name)." }
}]
```

- The JSON file has the same shape as an exported recipe: a top-level `prototypes` object whose entries are prototypes. `${key}` and every other declared parameter are substituted when a declaration uses the recipe.
- Declare in the manifest's `parameters` every value the caller must supply, and include `key`. A caller has to give exactly these parameters in `$with`.
- Use only Component type names you have verified against a running Resonite (for example with `resoloop type query`). Do not guess them. The example uses `FrooxEngine.UIX.RectTransform` and `FrooxEngine.UIX.Text`.
- Check a recipe offline with `uix recipe describe`, `uix recipe export` and `validate` on a declaration that uses it. The generated keys have the prefix `uix-plugin--<plugin-name>--<recipe-name>--<caller-key>`.

### Tools

```json
"tools": [{ "name": "uv", "executable": "uv", "versionArgs": ["--version"], "required": true, "hint": "Install uv from https://docs.astral.sh/uv/ and make sure it is on PATH." }]
```

Declare every external program your commands need. Give a `hint` that tells the user how to install it. Use `"required": false` for tools that only some commands need, so a missing one shows as a warning instead of failing `ready`.

### Trying your plugin

Develop in a directory of your own, outside the ResoLoop repository. Install it into a scratch project (`resoloop init` in a temporary directory), run the commands, and remove it again. Plugin names are per project, so you need `plugin remove NAME --yes` and a new `plugin install` after each change.

## Safety and limits

### Trust

Install only plugins from a source you trust, and read the code first. Installing does not execute anything, but the first command does:

- A plugin command runs as you. It can read and write any file you can, use the network and start processes.
- It inherits your environment (except stale `RESOLOOP_PLUGIN_*` variables), including any tokens or credentials in it.
- ResoLoop does not sign, scan or sandbox plugins.

### Verifying what you installed

- For a Git source, use `--ref` with the full SHA of a commit you have reviewed. Then compare the SHA that was installed with the SHA you reviewed. Right after installing, `plugin install --json` prints it as `commit`. Later, read the `commit` in the record:

  ```bash
  jq -r .commit "$PROJECT/.resoloop/plugins/hello/install.json"
  git -C "$GITSRC" rev-parse HEAD
  ```

  ```output
  <sha>
  <sha>
  ```

  Both lines must show the SHA you expect. `plugin list` shows the install state and verification problems, not commits.
- A local directory install records the source path only, and `commit` is `null`. Compare the files yourself if that matters.

### Verification against the record

Each plugin's `install.json` has the SHA-256 of every file in `package/`. Before the plugin is used (by `plugin list`, a command, skill distribution, a recipe lookup or `doctor`), ResoLoop reads the whole package again and compares it with the record. A changed file, an added file, a missing file, a file replaced by a link, or a different name or version in the manifest means the plugin is not used. For example:

```bash
echo "# edited" >> "$PROJECT/.resoloop/plugins/hello/package/hello.py"
resoloop plugin list --project "$PROJECT"
resoloop --project "$PROJECT" hello
```

```output
hello  PROBLEM PLUGIN_MODIFIED: Package content differs from the install record.
PLUGIN_MODIFIED: Installed plugins failed verification.
```

(The second line is on stderr and the command exits with 6.) A broken plugin does not heal itself. Remove it (`plugin remove NAME --yes` works on a broken plugin) and install it again.

What the verification does **not** cover: `data/`, and file permissions (it compares the file set and the content hash of each file).

### Trust, hash and sandbox are different things

- **The hash detects unintended differences.** It catches a package that was edited, truncated or half-written by accident, or tampered with by something that did not also update the record.
- **It is not a signature.** The hashes prove nothing about who wrote the plugin, only that the files are the files that were installed.
- **It is not a sandbox.** A verified plugin still runs with your full permissions.
- **It does not defend against whoever can write `install.json`.** Anyone with your permissions can change `package/` and `install.json` together, and the plugin then verifies fine. Full protection against a malicious user of your own account is out of scope.
- Stopping the process tree after a command is cleanup, not isolation.

### Current conditions

These are the conditions of the current implementation, verified on Linux.

- **Git sources must be the work tree root.** A subdirectory of a Git work tree, or a bare repository, is `PLUGIN_SOURCE_INVALID`. So is a Git source that contains a nested repository. Ignored files of a Git source are not installed.
- **A source outside Git is copied as a whole.** Any `.git` inside it, any link, and any special file is refused.
- **Source paths are resolved through links.** A local source path may be a link to a directory. ResoLoop resolves it to the real directory, and that real directory decides whether it is a Git root and which files are copied. The recorded `source.location` is the real path. The same is true of `--project`.
- **Links are refused where ResoLoop writes or copies.** A link inside the package that would be copied is refused, tracked or not (an ignored link in a Git source is not copied and does not matter). A link at any of `.resoloop`, `plugins`, a plugin's directory, `package`, or `data` is refused (`PLUGIN_PATH_UNSAFE`; a link found inside `package/` while verifying shows up as `PLUGIN_MODIFIED`), and install, remove and commands never follow it. Use ordinary directories.
- **The source and the project must not overlap.** Neither may contain the other (a `file://` URL included).
- **`--ref` is required for every Git URL**, `file://` included, and is refused with a directory.
- **Skill distribution looks at one directory only.** `init DIRECTORY` and `skills sync DIRECTORY` use the `.resoloop.json` of that exact directory. Install, list, remove, commands, doctor and recipes search upward from the current directory instead.
- **A failed install is cleaned up.** Only its own staging directory is removed. An existing installation, its record and its `data/` stay as they were, an occupied destination is never overwritten, and staging directories that belong to others are left alone.
- **Plugin commands run on Linux and Windows only.** On Windows only `.exe` programs are launched (no `.cmd` or `.bat`).

### Limits

| What | Limit |
| --- | --- |
| Files in a package | 10,000 |
| Size of a package | 200 MiB |
| Manifest size | 256 KiB |
| Manifest items | 50 commands, 50 skills, 50 recipes, 20 tools |
| Text fields | `description`, `purpose` and `hint`: 500 characters. `prototype`: 100. |
| `install.json`, skill record | 8 MiB each (`PLUGIN_SKILL_RECORD_TOO_LARGE` when the skill record would be larger) |
| Git operation | 120 seconds each |
| Command deadline | `timeoutSeconds` (1 to 86400), else `commandTimeoutSeconds` (default 900) |
| Doctor tool probe | 30 seconds, 64 KiB of output, first output line shown (at most 200 characters) |

### One operation at a time

Install, remove, listing, verification and every plugin command take the project's plugin lock (`.resoloop/plugins/.lock`) and fail at once with `PLUGIN_PROJECT_BUSY` (exit 7) when another operation holds it. A running plugin command holds it for its whole run. The lock is cooperative: it serializes ResoLoop's own operations, not other programs.

## Errors

Errors print as `CODE: message` on stderr, followed by `next:` lines. With `--json` they print as `{"ok":false,"error":{"code","message","context","suggestions"}}` on stderr.

| Code | Exit | Meaning and what to do |
| --- | --- | --- |
| `PLUGIN_PROJECT_NOT_FOUND` | 3 | No `.resoloop.json` in the `--project` directory, or none from the current directory upward. Run `resoloop init DIR` or pass `--project`. |
| `PLUGIN_SOURCE_NOT_FOUND` | 5 | `SOURCE` is neither an existing directory nor a URL. Check the path. |
| `PLUGIN_SOURCE_INVALID` | 6 | The source cannot be installed: a Git subdirectory, a bare repository, a nested repository or `.git`, a link or special file, overlap with the project, `--ref` with a directory, a bad or ambiguous ref, a refused URL (credentials, query, fragment, unsupported scheme), a non-regular Git entry, or too many files or bytes. Read the message and see [Current conditions](#current-conditions). |
| `PLUGIN_MANIFEST_NOT_FOUND` | 6 | `resoloop-plugin.json` is not at the package root. For Git, it must be at the repository root. |
| `PLUGIN_MANIFEST_INVALID` | 6 | The manifest or a file it references is invalid. See `context.errors` (`--json`) for each `$.path: message`. |
| `PLUGIN_SCHEMA_UNSUPPORTED` | 6 | `schemaVersion` is not `1`. It is reported when the rest of the manifest is otherwise valid. |
| `PLUGIN_INCOMPATIBLE_VERSION` | 6 | The running ResoLoop is outside the plugin's `minVersion` and `maxVersionExclusive`. Use a compatible ResoLoop or plugin version. |
| `PLUGIN_NAME_CONFLICT` | 6 | A plugin, command, skill or recipe name is reserved or already used by another installed plugin. Rename it in the manifest, or remove the other plugin. |
| `PLUGIN_ALREADY_INSTALLED` | 6 | A plugin with that name is installed. Remove it first. |
| `PLUGIN_GIT_REF_REQUIRED` | 2 | A Git URL was given without `--ref`. Add `--ref` (a full commit SHA is best). |
| `PLUGIN_GIT_REF_NOT_FOUND` | 5 | The ref does not exist in the repository. |
| `PLUGIN_GIT_FAILED` | 9 | `git` failed or timed out (clone, network, authentication, `git` missing). The message is deliberately generic and has no URL. Test the clone with `git` directly. |
| `PLUGIN_MODIFIED` | 6 | The package no longer matches `install.json`. Remove the plugin and install it again. While it exists, plugin commands, skill distribution and recipes of the project refuse to run. |
| `PLUGIN_RECORD_INVALID` | 6 | `install.json` is unreadable, malformed or contradicts the manifest. Remove and reinstall the plugin. |
| `PLUGIN_PATH_UNSAFE` | 6 | A link, special file, or non-directory is in a path ResoLoop manages (`.resoloop`, `plugins`, a plugin directory, `package`, `data`). Replace it with an ordinary directory. |
| `PLUGIN_IGNORE_CONFLICT` | 6 | `.resoloop/plugins/.gitignore` exists and is not the managed `*`. Move it away. |
| `PLUGIN_REMOVE_REFUSED` | 6 | `plugin remove` could not safely delete the directory. Check permissions, then try again. |
| `CONFIRMATION_REQUIRED` | 6 | `plugin remove` without `--yes`. Add `--yes` after checking the target. |
| `PLUGIN_NOT_INSTALLED` | 5 | There is no plugin with that name (or a recipe asks for a plugin that is not installed). Check `plugin list`. |
| `PLUGIN_NAME_INVALID` | 2 | The plugin name given to `plugin remove` does not follow the name rule. |
| `PLUGIN_PROJECT_BUSY` | 7 | Another operation (often a running plugin command) holds the project's plugin lock. Wait for it to finish and run the command again. |
| `PLUGIN_COMMAND_START_FAILED` | 9 | The program could not be started: not found in an absolute `PATH` entry, the package file is missing, not a regular file or not executable, a `.exe` rule on Windows, a NUL in an argument, or ResoLoop could not tell how to call itself back. Install the program, or fix the manifest or file mode. |
| `PLUGIN_COMMAND_TIMEOUT` | 8 | The command exceeded its deadline and was stopped. Raise `timeoutSeconds` in the manifest or `commandTimeoutSeconds`. |
| `PLUGIN_COMMAND_CANCELLED` | 7 | The command was interrupted (Ctrl+C or a termination signal) and its process tree was stopped. |
| `PLUGIN_COMMAND_STOP_FAILED` | 7 | ResoLoop could not confirm that the command's process tree stopped. Look for leftover processes before running it again. |
| `UNKNOWN_COMMAND` | 2 | The name is neither built-in nor a command of an installed plugin in the selected project. |
| `SKILL_SYNC_REQUIRED` | 6 | `skills sync --check` found files to write. Run `skills sync --update`. The wording mentions bundled skills even if only a plugin's skill is pending. |
| `SKILL_SYNC_CONFLICT` | 6 | A distributed skill was edited, is unowned, owned by another plugin, or its path is unsafe. Nothing was written. See `context.pluginConflicts`. |
| `UIX_RECIPE_NOT_FOUND` | 5 | No such recipe. Run `uix recipe list`. |

Other plugin codes you may meet: `PLUGIN_SKILL_RECORD_INVALID` (the skill record `.agents/skills/.resoloop-plugins.json` is broken; restore it from version control, because ResoLoop never treats a broken record as empty), `PLUGIN_SKILL_WRITE_FAILED` (a skill file could not be written; fix the permission and run `skills sync --update` again) and `PLUGIN_SKILL_RECORD_TOO_LARGE`.
