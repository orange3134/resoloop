[English](PLUGINS.md)｜[日本語](PLUGINS.ja.md)

# ResoLoop のプラグイン

プラグインは1つの ResoLoop プロジェクトにコマンド、スキル、UIX レシピ、tools の検査を追加するパッケージです。このガイドはプラグインを導入する人（[利用者向け](#利用者向け)）と、プラグインを書く人（[作者向け](#作者向け)）に向けた文書です。今のコードの動作を書いたもので、今後の予定は書いていません。

## 概要

プラグインは次の4つの機能のどれでも提供できます。どれも必須ではありません。manifest だけのパッケージも有効です。

| 機能 | できること | manifest の欄 |
| --- | --- | --- |
| 外部コマンド | `resoloop [--project DIR] COMMAND [ARGUMENTS...]` で、プラグインが宣言したプログラムを実行します。`COMMAND` より後ろはすべて、そのプログラムへそのまま渡ります。 | `commands` |
| スキルの配布 | `resoloop init` と `resoloop skills sync` が、プラグインのスキルのディレクトリを `<project>/.agents/skills/<skill>/` へコピーします。 | `skills` |
| UIX レシピ | `resoloop uix recipe list`、`describe`、`export` と、宣言ファイルの `$recipe` キーが `<plugin-name>:<recipe-name>` を受け付けます。 | `recipes` |
| tools の検査 | `resoloop doctor` が、プラグインに必要な外部ツールが `PATH` にあるかを調べ、`plugin:<plugin-name>:<tool-name>` として報告します。 | `tools` |

最初に知っておくことは次のとおりです。

- **プラグインは `feat/plugin-v2` の機能です。** `usaturn/resoloop` の `feat/plugin-v2` ブランチにあります。NuGet で公開されている ResoLoop 0.2.0 には、プラグインの機能がありません。`feat/plugin-v2` のビルドも `resoloop version` は `0.2.0` と表示するため、2つの見分けには `resoloop help` を使ってください。プラグインの機能があるビルドだけが、`plugin install` の行を持ちます。
- **プラグインは1つのプロジェクトに属します。** プロジェクトは `.resoloop.json` があるディレクトリです（`resoloop init` が作ります）。プラグインは `<project>/.resoloop/plugins/<name>/` へ導入され、グローバルには導入されません。
- **導入してもプラグインのコードは実行されません。** 導入ではファイルのコピー（または Git のオブジェクトからの書き出し）、manifest の検証、SHA-256 のハッシュの記録だけを行います。プラグインのコードが動くのは、そのコマンドを実行したときだけです。
- **プラグインのコマンドは利用者の権限で動きます。** プラグインは署名されておらず、サンドボックスの中でも動きません。自分で書いていないプラグインを導入する前に、[安全と制限](#安全と制限)を読んでください。
- **プラグインのコマンドは Linux と Windows でだけ動きます。** このガイドのコマンドと出力は Linux で実行したものです。規則と上限についての記述はコードで確かめ、可能なものは Linux で実行しました。Windows では実行していません。

4つの機能をすべて使う最小の例が、[`examples/plugins/hello/`](../examples/plugins/hello/) にあります。次のクイックスタートで、この例を導入します。

## クイックスタート

> 0.2.0 の feat/plugin-v2 で確認（Linux）。この注記と下の置き換えの規則は、クイックスタートだけでなく、このガイドのすべての出力例に当てはまります。クイックスタートの出力は、記載した順にコマンドを実行して得たものです。マシンによって変わる値は表のとおりに置き換えてあり、ほかは編集していません。`…` だけの行は、省略した行を表します。JSON の出力では、パスの ASCII 以外の文字が `\uXXXX` のエスケープで書かれるため、`作業 ワールド` のようなパスは、入力したとおりには表示されません。

| 表記 | 表すもの |
| --- | --- |
| `<project>` | ResoLoop プロジェクトの絶対パス（`$PROJECT`） |
| `<source>` | プラグインの導入元ディレクトリの絶対パス（`$SOURCE`） |
| `<source-url>` | プラグインの Git コピーの `file://` URL（パーセントエンコード済み） |
| `<sha>` | Git のコミット SHA の全体（16進数 40 文字） |
| `<sha256>` | SHA-256 のダイジェスト（16進数 64 文字） |
| `<uv-path>` | `PATH` で見つかった `uv` の絶対パス |
| `<uv-version-line>` | `uv --version` が出力する最初の行 |
| `<python-version>`、`<python-path>` | `uv` が選んだ Python |
| `<host-argv>` | ResoLoop を起動する文字列の JSON 配列です。ResoLoop の起動のしかたによって変わり、インストール済みのツールまたは apphost なら要素は1つ、`dotnet resoloop.dll` なら2つです。 |

前提条件は次のとおりです。

- `feat/plugin-v2` の `resoloop` が `PATH` にあること。チェックアウトからビルドする場合（`dotnet build ResoLoop.slnx`）は、`exec dotnet "<checkout>/src/RLoop.Cli/bin/Debug/net10.0/resoloop.dll" "$@"` を書いた、`resoloop` という名前のラッパースクリプトがあれば足ります。
- `git` が `PATH` にあること。導入処理は常に `git` を使い、ローカルのディレクトリの分類にも使います。
- `uv` が `PATH` にあること。例のコマンドは `uv` 経由で動きます。`uv` は Python 3.10 以降を探し、見つからなければダウンロードすることがあります。
- `jq` は任意です。以下のコマンドは JSON の出力から欄を取り出すためにだけ `jq` を使います。

コマンドはシェル変数を使っており、すべての使用箇所を引用符で囲んでいるため、空白や ASCII 以外の文字を含むパスでも動きます。

**1. プロジェクトを作ります。** 既存のプロジェクトを使うか、新しく作ります。

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

**2. 例を作業用ディレクトリへコピーします。** 例は ResoLoop のリポジトリの中にあり、`plugin install` が受け付ける Git のディレクトリは作業ツリーのルートだけなので、このリポジトリのサブディレクトリは `PLUGIN_SOURCE_INVALID` で拒否されます。どの Git リポジトリの外にもあり、プロジェクトと重ならないディレクトリへコピーしてください。

```bash
RESOLOOP_REPO=/path/to/resoloop     # your checkout of usaturn/resoloop at feat/plugin-v2
SOURCE="$(mktemp -d)/hello"
cp -r "$RESOLOOP_REPO/examples/plugins/hello" "$SOURCE"
```

コピーを別の Git リポジトリの中に置く必要がある場合は、コピーで `git init` を実行し、コピーのディレクトリ（そのコピー自身のルート）を渡してください。

**3. 導入します。**

```bash
resoloop plugin install "$SOURCE" --project "$PROJECT"
```

```output
installed hello 1.0.0
  <project>/.resoloop/plugins/hello
```

**4. 導入済みのプラグインの一覧を表示します。**

```bash
resoloop plugin list --project "$PROJECT"
```

```output
hello  1.0.0
```

**5. プラグインのコマンドを実行します。** このコマンドは受け取った引数を1行に1つずつすべて出力し（空の引数は空行になります）、そのあとに `RESOLOOP_PLUGIN_*` 変数を出力します。

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

コマンド名より前に置けるのは `--project` だけです。上の `--`、`--project=x`、空文字列は変更されずにプラグインへ届きました。最初の実行では、`uv` が次のような進捗の行も stderr に出力します。`uv` はほかの行を出力することもあります。たとえば `VIRTUAL_ENV` が設定されていると警告の行が出ますが、ResoLoop が `UV_PROJECT_ENVIRONMENT` を設定するため、この警告は影響しません。上の出力は stdout で、変わりません。

```output
Using CPython <python-version> interpreter at: <python-path>
Creating virtual environment at: .resoloop/plugins/hello/data/.venv
```

**6. プラグインのスキルを配布します。** 最初の同期の前は、プラグインのスキルのファイルが `.agents/skills/` にないため、`--check` が失敗します。

```bash
resoloop skills sync "$PROJECT" --check
```

```output
SKILL_SYNC_REQUIRED: Bundled skills or their lock need synchronization.
  next: Review the reported paths, then run resoloop skills sync --update.
```

このメッセージは同梱スキルのことしか述べていませんが、ここで同期が必要なのはプラグインのファイルです。`--json` を付けると、失敗した `--check` は **stderr** にエラーオブジェクトを出力し、stdout には何も出力しません。プラグインの結果は `error.context.report.plugins` にあります（`2>&1 >/dev/null` は stderr をパイプへ送り、stdout を捨てます）。

```bash
resoloop skills sync "$PROJECT" --check --json 2>&1 >/dev/null | jq -c .error.context.report.plugins
```

```output
{"mode":"check","synchronized":false,"updated":[],"files":[{"plugin":"hello","skill":"hello-guide","path":".agents/skills/hello-guide/SKILL.md","status":"missing","packageHash":"<sha256>"}],"retained":[]}
```

`status` は `missing` で、プラグインのスキルのファイルがまだ書かれていないことを示します。では書き込みます。

```bash
resoloop skills sync "$PROJECT" --update
```

```output
skills update: synchronized=True
  current              .agents/skills/resonite-build/SKILL.md
…
```

テキストの出力に並ぶのは同梱スキルだけです。コマンドが成功したときは、プラグインの結果が JSON 出力の `plugins` キー（`data.plugins`）にあります。上のように失敗した場合は、`error.context.report.plugins` へ移ります。

```bash
resoloop skills sync "$PROJECT" --check --json | jq -c .data.plugins
```

```output
{"mode":"check","synchronized":true,"updated":[],"files":[{"plugin":"hello","skill":"hello-guide","path":".agents/skills/hello-guide/SKILL.md","status":"current","packageHash":"<sha256>","installedHash":"<sha256>","recordedHash":"<sha256>","recordedPlugin":"hello"}],"retained":[]}
```

（ここで `jq` は、1つのキーを取り出すためにだけ使っています。）配布されたファイルは、パッケージのファイルと同じバイト列です。

```bash
cmp "$PROJECT/.agents/skills/hello-guide/SKILL.md" "$SOURCE/skills/hello-guide/SKILL.md" && echo identical
```

```output
identical
```

**7. プラグインの UIX レシピを見ます。** プラグインのレシピは `<plugin-name>:<recipe-name>` という名前です。

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

**8. doctor を実行します。** doctor は本体の検査と並べて、プラグインが宣言した tools（ここでは `uv`）を検査します。

```bash
resoloop doctor --project "$PROJECT"
```

```output
…
[pass] plugin:hello:uv: <uv-path> <uv-version-line>
…
```

本体の項目と最後の `ready` の行は、環境によって変わります（ResoniteLink の URL がないと `resonite-link-url` が失敗し、doctor は `not ready: resolve required checks above` で終わります）。どちらの場合も終了コードは 0 です。`plugin:*` の項目は別に読んでください。[doctor](#doctor) を参照してください。

**9. プラグインを削除します。** `plugin remove` は、プラグインの `data/` ディレクトリも削除するため、`--yes` が必要です。

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

導入ディレクトリはなくなりますが、`.agents/skills/` へ配布されたスキルは残り、retained として記録されます。

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

**10. Git URL から導入します（任意）。** Git の導入元には、URL と `--ref` が必要です。例の Git コピーを作り、`file://` URL から導入します（`git commit` には `user.name` と `user.email` の設定が必要です）。

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

`commit` は、`--ref` が解決したコミットです。使い方は[導入元の確かめ方](#導入元の確かめ方)を参照してください。

## 利用者向け

### プロジェクトの決め方

- `--project DIR` は、プロジェクトのディレクトリを指定します。そのディレクトリに `.resoloop.json` が必要で、なければコマンドは `PLUGIN_PROJECT_NOT_FOUND` で止まります。例外は `doctor` です。止まらずに、失敗した `plugins` の項目を1つ出力し（`[fail] plugins: PLUGIN_PROJECT_NOT_FOUND: …`）、終了コードは 0 のままです（[doctor](#doctor) を参照）。
- `--project` がないと、`plugin install`、`plugin list`、`plugin remove`、プラグインのコマンド、`doctor`、`uix recipe` は、カレントディレクトリから上へ向かって `.resoloop.json` を探します。見つからないと、`plugin install`、`plugin list`、`plugin remove` は `PLUGIN_PROJECT_NOT_FOUND` で失敗し、プラグインのコマンド名は未知の名前になります（`UNKNOWN_COMMAND`。`--project DIR` を渡してください）。`doctor` と `uix recipe` は失敗しません。`doctor` にはプラグインの項目がなく、`uix recipe` は本体のレシピだけを知っています。
- `init [DIRECTORY]` と `skills sync [DIRECTORY]` は指定したディレクトリ（既定はカレントディレクトリ）をそのまま使い、上へは探しません。そのディレクトリがプロジェクトのルートでないと、何も表示されないまま、プラグインのスキルが配布されません。
- プラグインのレシピを使う宣言ファイル（`validate`、`diff`、`apply` など）は、そのファイルのディレクトリから上へ向かって、プロジェクトを探します。`--project` は参照されません。
- プラグインのコマンドでは、`--project` はコマンド名より前にだけ、1回だけ指定できます（`--project DIR` または `--project=DIR`）。

### 導入

```output
  resoloop plugin install SOURCE [--ref GIT_REF] [--project DIR] [--json]
```

`SOURCE` は、次の2種類のどちらかです。

**ローカルのディレクトリ。** Git の作業ツリーのルートか、どの Git リポジトリの中にもないディレクトリのどちらかでなければなりません。

- Git の作業ツリーでは追跡されているファイルと、無視されていない未追跡のファイルを導入します（`git ls-files --cached --others --exclude-standard`）。無視されているファイル（たとえば `.venv/`）は導入されません。
- Git の外のディレクトリではすべてのファイルを導入します。その中のどこかに `.git` があると、読み飛ばされずに拒否されます。
- Git の作業ツリーのサブディレクトリは、`PLUGIN_SOURCE_INVALID` で拒否されます（`A local Git source must be the working tree root.`）。ベアリポジトリも同じです。パッケージを外へコピーするか、コピーで `git init` を実行してください。
- 導入元のディレクトリとプロジェクトのディレクトリは、互いを含んではいけません（`PLUGIN_SOURCE_INVALID`: `Plugin source and project directories must not overlap.`）。`file://` URL でも同じです。
- `--ref` は、ディレクトリと一緒には使えません。
- コピーはその時点のファイルのスナップショットです。記録には導入元のディレクトリはありますが、リビジョンはありません（`commit` は `null` です）。

**Git URL（`--ref` 付き）。**

- 受け付ける URL の形式は `http://`、`https://`、`ssh://`、`git://`、`file://` の URL と、scp 形式の `[user@]host:path` です。どの形式でも `--ref` が必要で、`file://` も例外ではありません（`PLUGIN_GIT_REF_REQUIRED`）。
- URL に資格情報を入れないでください。`http`、`https`、`git`、`file` の URL ではユーザー情報が、`ssh` の URL ではパスワードが拒否されます。クエリ文字列とフラグメントも拒否されます。認証は ResoLoop の外で設定してください（SSH エージェント、資格情報ヘルパー）。導入処理が入力を求めることはありません。
- `--ref` にはブランチ、タグ、`HEAD`、完全な ref 名、コミット SHA を指定できます。短い名前が複数の ref に一致し、それらが異なるコミットを指す場合、導入は `PLUGIN_SOURCE_INVALID` で失敗し、完全な ref 名を求めます。リビジョンを正確に固定するには、コミット SHA の全体を使ってください。
- 導入処理はフックを無効にして、リポジトリを staging ディレクトリへベアでクローンし、解決したコミットのファイルを Git のオブジェクトから書き出します。ツリーをチェックアウトすることも、パッケージのコードを実行することもありません。書き出すのは通常のファイル（モード 100644 または 100755）だけで、シンボリックリンク、サブモジュール、`.git` の要素を含むパスは拒否されます。
- ResoLoop は、ref が解決したコミットを記録します。その後にブランチを追従することはありません。
- Git の操作はそれぞれ 120 秒で打ち切られます。

どの導入元でも、導入処理は `.resoloop/plugins/` の下の staging ディレクトリで同じ手順を踏みます。コピーまたは書き出し、manifest の読み取りと検証、ResoLoop のバージョンの確認、名前の確認、`install.json` の書き込み、結果の検証を行い、最後に1回のリネームで公開します。失敗した導入が削除するのは、自分の staging ディレクトリだけです。すでにある導入物を変えることはありません。

### 保存先

```
<project>/.resoloop/plugins/
├── .gitignore            （"*" を含む。plugins/ の下はコミットされない）
├── .lock                 （導入、削除、プラグインの使用を直列にするロック）
└── <name>/
    ├── install.json      （記録: 導入元、commit、installedAt、ファイルのハッシュ）
    ├── package/          （プラグインのファイル。使う前に毎回ハッシュで検証する）
    └── data/             （プラグインの永続的な作業ディレクトリ。検証しない）
```

`install.json` には `schemaVersion`、`name`、`version`、`source`（`kind` は `directory` か `git`。ほかに `location` と `ref`）、`commit`、`installedAt`（UTC）、`resoloopVersion`、`files`（`package/` のすべてのファイルの SHA-256）があります。Git の導入元の `source.location` に、資格情報が入ることはありません。`plugins/.gitignore` がすでにあって内容が異なる場合、導入は `PLUGIN_IGNORE_CONFLICT` で止まり、そのファイルには触れません。

### 一覧

```output
  resoloop plugin list [--project DIR] [--json]
```

`plugin list` は、導入済みのすべてのプラグインを検証し、1行ずつ出力します。検証に失敗したプラグインも、問題とともに一覧に出ます。終了コードは 0 のままです。

```output
hello  PROBLEM PLUGIN_MODIFIED: Package content differs from the install record.
```

`--json` は `{"ok":true,"data":[{"name":"hello","ok":true,"version":"1.0.0"}]}` を出力します。壊れたプラグインは `"ok":false` になり、`code`、`message`、`suggestions` を持つ `problem` オブジェクトが付きます。何も導入されていないときのテキスト出力は `No plugins installed in <project>` です。`plugin list` は、コミットを表示しません（[導入元の確かめ方](#導入元の確かめ方)を参照）。

### 更新と削除

```output
  resoloop plugin remove NAME --yes [--project DIR] [--json]   (also deletes the plugin's data/ directory)
```

- **その場での更新はありません。** すでに導入済みの名前を導入すると、`PLUGIN_ALREADY_INSTALLED` で失敗します。更新するには、プラグインを削除してから導入し直してください。
- **`plugin remove` は `<project>/.resoloop/plugins/<name>/` を、`data/` を含めて丸ごと削除します。** プラグインがそこに置いたもの（キャッシュ、仮想環境、保存した結果）はすべてなくなります。必要なものは先にコピーしてください。
- `--yes` がないと、コマンドは `CONFIRMATION_REQUIRED`（終了コード 6）で失敗し、何も変更しません。
- 記録かパッケージが壊れているプラグインも、削除できます。ただし、プラグインのディレクトリか `package` 自体がリンクの場合は、`plugin remove` が `PLUGIN_PATH_UNSAFE` で拒否するため、先にリンクを通常のディレクトリに置き換えてください。
- `data/` の中のリンクはリンクとして削除されます。リンク先には触れません。
- **配布済みのスキルは `.agents/skills/<skill>/` に残ります。** ResoLoop は、自分がコピーしたファイルを削除しません。`skills sync --json` は、それらを `plugins.retained` に並べます。

### プラグインのコマンドの実行

```output
  resoloop [--project DIR] PLUGIN_COMMAND [ARGUMENTS...]   (arguments after the command name go to the plugin unchanged)
```

- `plugin list` と `resoloop help` は、プラグインのコマンド名を表示しません。コマンド名は `<project>/.resoloop/plugins/<name>/package/resoloop-plugin.json` の `commands[].name` にあり、プラグイン自身のスキルやドキュメントにも載っていることがあります。
- コマンド名より前に置けるのは `--project` だけです。それ以外で最初のトークンがコマンド名で、`--json`、`--`、空文字列、`--project=x` を含め、その後ろはすべて変更されずにプラグインへ渡ります。ResoLoop はプラグインのコマンドで JSON 出力に切り替えません。`--json` を提供するプラグインは自分で実装します。
- 本体のコマンド名が常に優先されます。本体のコマンドでも、導入済みのプラグインが宣言したものでもない名前は、`UNKNOWN_COMMAND`（終了コード 2）で失敗します。
- プラグインのコマンドは Resonite へ接続しません。
- プログラムの stdout と stderr は変更されずに渡され、プログラムの終了コードが ResoLoop の終了コードになります。Linux では、シグナルで終了させられたプログラムの終了コードは、`128` にシグナル番号を足した値です。ResoLoop 自身のエラー（`CODE: message`）は stderr に出ます。
- 期限より長く動いたコマンドは停止され、ResoLoop は `PLUGIN_COMMAND_TIMEOUT`（終了コード 8）で終了します。期限は manifest のコマンドの `timeoutSeconds` です。ない場合は `commandTimeoutSeconds` で、環境変数 `RESOLOOP_COMMAND_TIMEOUT_SECONDS`、プロジェクトの `.resoloop.json` の `commandTimeoutSeconds`、`~/.resoloop/config.json` の `commandTimeoutSeconds` の順に見て、どれも設定がなければ 900 秒です。コマンド名より後ろのオプションはプラグインのものなので、`--command-timeout` オプションは適用されません。
- Ctrl+C、SIGTERM、SIGHUP、SIGQUIT は、コマンドのプロセスグループ（Linux）またはジョブ（Windows）を停止し、`PLUGIN_COMMAND_CANCELLED`（終了コード 7）で終わります。コマンドが終了したときにも同じグループまたはジョブを停止するため、コマンドがその中に残したプロセスも停止されます。グループを抜けたプロセスは停止されません。Linux では、`setsid` か `setpgid` を呼んだ子プロセスは生き残ります。これは後始末であり、隔離ではありません（[コマンド](#コマンド)の「プロセスツリー」の項目を参照）。
- プロジェクトのプラグインは呼び出しのたびに検証されます。プロジェクトの導入済みのプラグインの**どれか1つ**が検証に失敗すると、そのプロジェクトの**すべての**プラグインのコマンド、`plugin install`（どのプラグインのものでも）、スキルの配布、プラグインのレシピの参照が、最初の問題のコードで実行を拒否されます（たとえば `PLUGIN_MODIFIED: Installed plugins failed verification.`、終了コード 6）。`plugin list`、`plugin remove`、`doctor` は引き続き動き、どのプラグインが壊れているかを示します（`doctor` では、失敗した `plugin:<plugin-name>` の項目として表示します）。壊れたプラグインは削除するか、導入し直してください。
- プラグインのコマンドの実行中、ResoLoop はプロジェクトのプラグインのロックを保持します。同じプロジェクトでは、`plugin install`、`plugin list`、`plugin remove`、`init`、`skills sync`、`uix recipe list`、プラグインのレシピに対する `describe` と `export`、プラグインのレシピを使う宣言ファイルの検証、別のプラグインのコマンドが待たずにすぐ `PLUGIN_PROJECT_BUSY`（終了コード 7）で失敗します。`doctor` もプラグインの領域を読めませんが、失敗はしません。`[fail] plugins: PLUGIN_PROJECT_BUSY: …` の項目を1つ出力して、終了コード 0 で終わります（[doctor](#doctor) を参照）。

### スキル

```output
  resoloop init [DIRECTORY] [--json]
…
  resoloop skills sync [DIRECTORY] (--check | --update) [--json]
```

`init` と `skills sync` は検証済みのプラグインのスキルのディレクトリ（manifest の `skills[].path` の下のすべてのファイル）を、`<project>/.agents/skills/<skill>/` へコピーします。`.agents/skills/.resoloop-plugins.json` には、コピーしたファイルごとに、どのプラグインのものかと SHA-256 が記録されます。各ファイルの状態は次のとおりです。

| 状態 | 意味 | `--update`（と `init`）での扱い |
| --- | --- | --- |
| `missing` | ファイルがまだありません。 | 作成されます。 |
| `current` | ファイルが、パッケージのファイルと記録の両方に等しい状態です。 | 何もしません。 |
| `update-available` | パッケージが変わり、利用者側のコピーは、ResoLoop が配布した内容のままです。 | `skills sync --update` が上書きします。`init` は既存のファイルを上書きしないため、競合として報告します。 |
| `record-update-required` | ファイルはパッケージのファイルと等しいが、記録がないか、記録が異なります。 | 記録が書き直されます。 |
| `modified-conflict` | 配布されたあとに、利用者がファイルを編集しました。 | 拒否されます。 |
| `untracked-conflict` | 同じ名前の別のファイルがあり、配布されたものではありません。 | 拒否されます。 |
| `owner-conflict` | 記録では、別のプラグインがそのファイルを所有しています。 | 拒否されます。 |
| `unsafe-path-conflict` | 配布先がリンクか特殊ファイルであるか、ファイルまたはディレクトリが邪魔をする場所にあります。 | 拒否されます。 |

どの競合もファイルを書き込む前に `skills sync` を `SKILL_SYNC_CONFLICT` で止め（`init` なら `INIT_FILE_EXISTS`）、何も書き込みません。ResoLoop が、利用者の編集したスキルを上書きすることはありません。記録の項目が削除されることもありません。削除されたプラグインのファイルや、もう宣言されなくなったファイルは `plugins.retained` に報告され、そのまま残ります。

プラグインの結果は `init` と `skills sync` のテキスト出力には出ません。`--json` を使ってください。コマンドが成功したときは、`data.plugins` を読みます。`mode`、`synchronized`、`updated`、`files`（配布したファイルごとに1項目。`plugin`、`skill`、`path`、`status` とハッシュを持ちます）、`retained` があります。プロジェクトにプラグインのスキルも記録もないときは、`data.plugins` はありません。

`skills sync --check` が `SKILL_SYNC_REQUIRED` で失敗したときは、stdout に何も出力されないため、`data.plugins` はありません。同じオブジェクトは **stderr** のエラーの `error.context.report.plugins` にあります。

```bash
resoloop skills sync "$PROJECT" --check --json 2>&1 >/dev/null | jq -c .error.context.report.plugins
```

`SKILL_SYNC_REQUIRED` のメッセージ（`Bundled skills or their lock need synchronization.`）は、同梱スキルを前提に書かれています。クイックスタートの手順 6 のように、プラグインのスキルだけが同期待ちのときも、同じメッセージが出ます。どのファイルが同期待ちかは、`--json` の出力（stderr）の `error.context.report.plugins` で確かめてください。

### UIX レシピ

```output
  resoloop uix recipe list [--project DIR]
  resoloop uix recipe describe NAME [--project DIR]   (plugin recipes: PLUGIN:RECIPE)
  resoloop uix recipe export NAME --output NEW_FILE.json [--project DIR] (offline structural prototypes; no visual defaults)
```

- プラグインのレシピの名前は `<plugin-name>:<recipe-name>` です。`uix recipe list` は、本体のレシピに続けてプラグインのレシピを表示します。`:` のない名前は常に本体のレシピです。
- `describe` は、`name`、`prototype`、`purpose`、`parameters`、`ports`、`requirements` を出力します。`export` は、レシピの prototypes を**新しい**ファイルへ書き出し（既存のファイルを上書きすることはありません）、`name`、`output`、`prototype` を出力します。

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

- 宣言ファイルでは `$recipe` と `$with` を使って、レシピを直接使えます。

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

  プロジェクトの中に（たとえば `content/hello.json` として）保存し、オフラインで確かめます。

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

  `validate --strict` は ResoniteLink の接続も必要とします（URL がないと、`RESONITE_LINK_URL_MISSING`、終了コード 3 で失敗します）。
- `$with` には、レシピが宣言したパラメーターを、過不足なく入れる必要があります（足りないものや未知のものは `APPLY_RECIPE_PARAMETERS`）。`$with.key` に使えるのは、ASCII の英字、数字、`-`、`_` だけです（`APPLY_RECIPE_KEY_INVALID`）。インスタンスの Slot と Component のキーには接頭辞 `uix-plugin--<plugin-name>--<recipe-name>--` が付くため、上の `greeting` は `uix-plugin--hello--hello-card--greeting` になります。`$recipe` は `$prototype` や `$instance` と組み合わせられません。`$recipe` の隣に書いたほかのフィールドはレシピのものを上書きします。
- 未知の名前は `UIX_RECIPE_NOT_FOUND`（終了コード 5）で失敗します。導入されていないプラグインのレシピは `PLUGIN_NOT_INSTALLED`（終了コード 5）で失敗しますが、プロジェクトが見つかったときだけです。プロジェクトの外で `--project` がなければ、`uix recipe` は本体のレシピだけを知っているため、`uix recipe describe hello:hello-card` は `UIX_RECIPE_NOT_FOUND`（`next: Run resoloop uix recipe list --json.`）になります。プロジェクトの中で実行するか、`--project` を渡してください。`.resoloop.json` のない `--project` を渡すと、`PLUGIN_PROJECT_NOT_FOUND` で失敗します。

### doctor

```output
  resoloop doctor [--url ws://localhost:PORT] [--project DIR] [--json]
```

- `doctor` がプラグインの tools を検査するのは、実行したときだけです。ほかの操作が tools の検査を走らせることはなく、doctor が何かを導入したり修復したりすることもありません。
- プラグインの manifest にある各 tool について、`executable` を絶対パスの `PATH` の項目から探し、`versionArgs` が空でなければ、その引数でプロジェクトのディレクトリの中で実行します（制限時間 30 秒、出力の上限 64 KiB）。検証に失敗したプラグインは、問題のコードを付けた、失敗した `plugin:<plugin-name>` の項目1つとして表れます。
- **doctor はプラグインの領域の問題を項目として報告します。失敗はしません。** doctor がプロジェクトのプラグインをまったく読めないときは、エラーコードとメッセージを付けた必須の項目 `plugins`（プラグイン名なし）を1つ追加し、終了コードは 0 のままです。必須の項目なので、`ready` は false になります。原因はプラグインの領域を読むときのエラーです。プロジェクトの別の操作（たとえば実行中のプラグインのコマンド）がプラグインのロックを保持している間の `PLUGIN_PROJECT_BUSY`、プロジェクトでないディレクトリを `--project DIR` で明示したときの `PLUGIN_PROJECT_NOT_FOUND`、そして `PLUGIN_PATH_UNSAFE` です。たとえば、同じプロジェクトのプラグインのコマンドの実行中は、次のようになります。

  ```output
  …
  [fail] plugins: PLUGIN_PROJECT_BUSY: Another operation is using this project's plugins.
  not ready: resolve required checks above
  ```

  `--project` がなく、カレントディレクトリから上へ向かってもプロジェクトが見つからないときは、doctor はプラグインの項目をまったく追加しません。これらを検出するために、0 以外の終了コードをあてにしないでください。
- 各 tool の項目は `plugin:<plugin-name>:<tool-name>` という名前です。

  | 状態 | 条件 |
  | --- | --- |
  | `pass` | tool が見つかり、バージョンのコマンドが終了コード 0 で終わりました。メッセージは `<path> <first line of output>` です（`<first line of output>` は stdout の最初の行で、stdout が空なら stderr の最初の行）。`versionArgs` が空のとき、またはバージョンのコマンドが何も出力しなかったときは、`<path>` だけです。 |
  | `fail` | tool が `required`（既定）で、見つからない、0 以外で終了した、または時間内に終わりませんでした。 |
  | `warning` | 同じ問題が、`"required": false` と宣言された tool で起きました。 |

  `next:` の行は tool の `hint` です。`hint` がない場合は `Install <executable> and make it available on PATH.` です。
- `--json` は、各項目を `{"name","status","required","message"}`（`suggestion` があるときはそれも）の形で出力します。例のプラグインでは次のようになります。

  ```bash
  resoloop doctor --project "$PROJECT" --json | jq -c '.data.checks[] | select(.name == "plugin:hello:uv")'
  ```

  ```output
  {"name":"plugin:hello:uv","status":"pass","required":true,"message":"<uv-path> <uv-version-line>"}
  ```
- **読み方は次のとおりです。** `ready` が true になるのは、本体の項目を含め、必須の項目がすべて pass のときだけです。doctor は `ready` が false でも終了コード 0 で終わるため、終了コードだけでは成功の目印になりません。プラグインの tools はそれ自身の `plugin:*` の項目で判断してください。ResoniteLink の URL がないといった本体の項目の失敗は `ready` を false にしますが、プラグインの tools については何も示しません。`warning` が `ready` を変えることはありません。
- tool が不足している場合は、次のようになります。例の `tools` に1項目を足した一時的なコピーを、スクラッチのプロジェクト（`$SCRATCH`。`resoloop init` で作り、`hello` を導入していないもの）へ導入します。足した項目は例の一部ではありません。

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

## 作者向け

### パッケージの構成

パッケージは、ルートに `resoloop-plugin.json` があるディレクトリです。例は次のとおりです。

```
hello/
├── resoloop-plugin.json     manifest
├── pyproject.toml           仮想の uv プロジェクト（依存なし、ビルドするものなし）
├── uv.lock                  `uv lock` で固定。コマンドは `--locked` で実行する
├── hello.py                 コマンド本体。引数と RESOLOOP_PLUGIN_* 変数を出力する
├── skills/hello-guide/SKILL.md
└── recipes/hello-card.json
```

Git の導入元では、リポジトリのルートがパッケージのルートになるため、`resoloop-plugin.json` はリポジトリのルートになければなりません。選んだコミットのツリーにあるものはすべて、パッケージの一部になります。

### manifest

`resoloop-plugin.json` は、256 KiB 以下の UTF-8 の JSON です。未知のプロパティと重複したプロパティは、エラーです。検証はすべての問題を集め、まとめて `PLUGIN_MANIFEST_INVALID` で報告します。エラーの `context.errors` に、`$.commands[0].program: Required property.` のような項目が入ります（見るには `--json` を使います）。

最上位の欄は次のとおりです。

| 欄 | 型 | 必須 | 規則 |
| --- | --- | --- | --- |
| `schemaVersion` | 整数 | はい | `1` でなければなりません。ほかの値は `PLUGIN_SCHEMA_UNSUPPORTED` です。 |
| `name` | 文字列 | はい | [名前](#名前)。 |
| `version` | 文字列 | はい | SemVer 2.0 のバージョン。 |
| `resoloop` | オブジェクト | はい | [対応する ResoLoop のバージョン](#対応する-resoloop-のバージョン)を参照。 |
| `description` | 文字列 | いいえ | 1〜500 文字。 |
| `commands` | 配列 | いいえ | 50 項目まで。 |
| `skills` | 配列 | いいえ | 50 項目まで。 |
| `recipes` | 配列 | いいえ | 50 項目まで。 |
| `tools` | 配列 | いいえ | 20 項目まで。 |

`commands[]` の欄は次のとおりです。

| 欄 | 型 | 必須 | 規則 |
| --- | --- | --- | --- |
| `name` | 文字列 | はい | [名前](#名前)。導入済みのすべてのプラグインの間で一意です。 |
| `description` | 文字列 | はい | 1〜500 文字。 |
| `program` | 文字列 | はい | 裸のコマンド名（`[A-Za-z0-9._+-]+`。絶対パスの `PATH` の項目から探します）か、`./path/in/the/package` のどちらか。 |
| `args` | 文字列の配列 | いいえ | 利用者の引数の前に渡されます。`${pluginRoot}` と `${pluginData}` は置き換えられます。それ以外の `${...}` や、閉じていない `${` は無効です。 |
| `timeoutSeconds` | 整数 | いいえ | 1〜86400。ない場合の期限は `commandTimeoutSeconds`（既定 900）です。 |

`skills[]` には、`name`（名前。必須）と `path`（必須）があります。`path` は、`SKILL.md` を含む、パッケージからの相対ディレクトリです。

`recipes[]` の欄は次のとおりです。

| 欄 | 型 | 必須 | 規則 |
| --- | --- | --- | --- |
| `name` | 文字列 | はい | [名前](#名前)。 |
| `path` | 文字列 | はい | パッケージからの相対の JSON ファイル。最上位に `prototypes` オブジェクトを持ちます。 |
| `prototype` | 文字列 | はい | 1〜100 文字。`prototypes` の中のオブジェクトでなければなりません。 |
| `purpose` | 文字列 | はい | 1〜500 文字。 |
| `parameters` | 文字列を値とするオブジェクト | はい | パラメーター名から説明への対応。`key` を含める必要があります。 |
| `ports` | 文字列を値とするオブジェクト | いいえ | ポート名から説明への対応。 |
| `requirements` | 文字列の配列 | いいえ | 利用者への注意書き。 |

`tools[]` の欄は次のとおりです。

| 欄 | 型 | 必須 | 規則 |
| --- | --- | --- | --- |
| `name` | 文字列 | はい | [名前](#名前)。 |
| `executable` | 文字列 | はい | 区切り文字のないコマンド名（`[A-Za-z0-9._+-]+`）。 |
| `versionArgs` | 文字列の配列 | いいえ | tool にバージョンを出力させる引数。空または省略の場合、doctor は tool が存在するかだけを確かめます。 |
| `required` | 真偽値 | いいえ | 既定は `true`。 |
| `hint` | 文字列 | いいえ | 1〜500 文字。検査が失敗したときに `next:` として表示されます。 |

#### 名前

プラグイン、コマンド、スキル、レシピ、tool の名前は、同じ規則に従います。2〜40 文字で、小文字で始まり、そのあとは小文字と数字を使い、部分の区切りは単独のハイフンです（`[a-z][a-z0-9]*(-[a-z0-9]+)*`）。名前はそれぞれの配列の中で、大文字と小文字を区別せずに一意でなければなりません。次のものは `PLUGIN_NAME_CONFLICT` で拒否されます。

- 本体のコマンド名または同梱スキルの名前と等しいプラグイン名
- 本体のコマンド名、または導入済みの別のプラグインのコマンドと等しいコマンド名（本体のコマンド名には、`apply`、`doctor`、`init`、`plugin`、`skills`、`uix`、`validate` と、`resoloop help` に出るそのほかの名前があります）
- `resonite-` で始まる、同梱スキルの名前と等しい、または導入済みの別のプラグインのスキルと等しいスキル名
- 本体のレシピ（`button`、`boolean-state`、`scroll-content`、`value-state`、`text-input`、`toggle`、`choice`、`slider`）と等しいレシピ名。プラグインのレシピは、それ以外ではプラグイン名で名前空間が分けられます。

#### パス

パッケージ内の相対パス（manifest の中のものと、パッケージのすべてのファイル）は、すべて移植可能でなければなりません。区切りは `/` です。空の部分、`.`、`..` の部分は使えません。`.` か空白で終わる部分も使えません。`\ : < > " | ? *` と制御文字も使えません。Windows のデバイス名（`CON`、`PRN`、`AUX`、`NUL`、`CONIN$`、`CONOUT$`、`COM1` から `COM9`、`LPT1` から `LPT9`）も使えません。大文字と小文字だけが異なる2つのパスも使えません。パッケージにはリンクや特殊ファイル（FIFO、ソケット、デバイス）を含めてはいけません。パッケージは最大 10,000 ファイル、合計 200 MiB です。

#### 対応する ResoLoop のバージョン

```json
"resoloop": { "minVersion": "0.2.0" }
```

`minVersion` は必須です。`maxVersionExclusive` は任意で、`minVersion` より大きくなければなりません。それより新しいバージョンがプラグインを壊すと分かっているとき以外は、書かないでください。この上限はその値を含みません。上限が実行中の ResoLoop のバージョンと等しいと、そのバージョン自身が除外されるため、上限が現在のバージョンであるプラグインは、そのバージョンの ResoLoop に導入できません。バージョンは SemVer 2.0 のバージョンとして比較します（`0.2.0-beta.1` は `0.2.0` より低く、`+` の後のビルドメタデータは、順序の比較では無視されます）。確認は導入時と、プラグインを検証するたびに行うため、ResoLoop のバージョンが範囲を外れたプラグインは動かなくなります（`PLUGIN_INCOMPATIBLE_VERSION`）。

### コマンド

ResoLoop はコマンドを次のように起動します。

- **シェルを使いません。** プログラムは直接起動されます。裸の名前の `program` は `PATH` の絶対パスの項目から探します（相対パスの項目と空の項目は読み飛ばします）。`./path` のプログラムは `package/` の中の通常のファイルで、Linux では実行ビットが必要です（Git ではモード 100755。ローカルのコピーはモードを保ちます）。Windows では `.exe` のファイルだけが起動されるため、スクリプトはインタープリターを `PATH` から呼び、スクリプトを `${pluginRoot}` で渡してください。
- **引数は次のように作られます。** `args` の `${pluginRoot}`（`package/` の絶対パス）と `${pluginData}`（`data/` の絶対パス）を1回の処理で置き換えたあとに、利用者の引数が入力したとおりに追加されます。
- **作業ディレクトリはプロジェクトのルートです。**
- **環境は次のとおりです。** 継承した環境から `RESOLOOP_PLUGIN_*` 変数をすべて除き、次を加えます。

  | 変数 | 値 |
  | --- | --- |
  | `RESOLOOP_PLUGIN_NAME` | プラグインの名前。 |
  | `RESOLOOP_PLUGIN_COMMAND` | コマンドの名前。 |
  | `RESOLOOP_PLUGIN_ROOT` | `package/` の絶対パス。読み取り専用として扱ってください。 |
  | `RESOLOOP_PLUGIN_DATA` | `data/` の絶対パス。書き込めて、呼び出しをまたいで残り、`plugin remove` で削除されます。 |
  | `RESOLOOP_PLUGIN_PROJECT_ROOT` | プロジェクトのルート（作業ディレクトリと同じ）。 |
  | `RESOLOOP_PLUGIN_HOST_ARGV` | 文字列の JSON 配列。この ResoLoop を起動した argv で、プラグインが ResoLoop を呼び戻せるようにします。ResoLoop の起動のしかたによって変わります。 |
  | `RESOLOOP_PLUGIN_HOST_VERSION` | 実行中の ResoLoop のバージョン（たとえば `0.2.0`）。 |
  | `PYTHONPYCACHEPREFIX` | `<data>/.cache/python` |
  | `UV_PROJECT_ENVIRONMENT` | `<data>/.venv` |
- **ストリームと終了コードは次のように扱われます。** stdout と stderr は利用者へ直接届きます。stdin はリダイレクトされていればそのまま渡され、端末なら空の入力になります。終了コードは変更されずに返ります。キャンセルされたコマンドや、期限を超えたコマンドが成功として扱われることはありません。停止される間にプログラムが 0 で終了しても、ResoLoop は `PLUGIN_COMMAND_CANCELLED` または `PLUGIN_COMMAND_TIMEOUT` を報告します。
- **`--json` は作者が実装します。** ResoLoop は `--json` をほかのトークンと同じように素通しし、解析しません。コマンドに JSON を出力させたい場合は、それを実装してください。例は実装しています。ここでは、引数の順序が保たれ、値は文字列です。

  ```bash
  resoloop --project "$PROJECT" hello --json "two words" "" -- --project=x
  ```

  ```output
  {"argv": ["--json", "two words", "", "--", "--project=x"], "env": {"RESOLOOP_PLUGIN_NAME": "hello", "RESOLOOP_PLUGIN_COMMAND": "hello", "RESOLOOP_PLUGIN_ROOT": "<project>/.resoloop/plugins/hello/package", "RESOLOOP_PLUGIN_DATA": "<project>/.resoloop/plugins/hello/data", "RESOLOOP_PLUGIN_PROJECT_ROOT": "<project>", "RESOLOOP_PLUGIN_HOST_ARGV": "<host-argv>", "RESOLOOP_PLUGIN_HOST_VERSION": "0.2.0"}}
  ```

  （JSON では、`RESOLOOP_PLUGIN_HOST_ARGV` は JSON 配列をもう一度文字列としてエンコードしたものです。）
- **プロセスツリーはコマンドの終了時に後始末されます。** コマンドは、自分のプロセスグループ（Linux）またはジョブ（Windows）の中で動きます。コマンドがどのように終わっても、そのグループまたはジョブにまだ残っているプロセスはすべて停止されるため、バックグラウンドのプロセスをあてにしないでください。これは後始末であり、保証ではありません。グループを抜けたプロセスは停止されません。Linux では、`setsid` か `setpgid` を呼んだ子プロセスが自分のグループを持ち、コマンドが戻ったあとも動き続けます。
- **ResoLoop を呼び戻すときは、ロックに注意してください。** コマンドの実行中、ResoLoop はプロジェクトのプラグインのロックを保持します。プラグインの領域を必要とする呼び戻し（`plugin` のサブコマンド、別のプラグインのコマンド、`init`、`skills sync`、`uix recipe list` かプラグインのレシピ、プラグインのレシピを使う宣言ファイル）は、`PLUGIN_PROJECT_BUSY` で失敗します。`doctor` は失敗しません。終了コード 0 で終わり、問題を `[fail] plugins: PLUGIN_PROJECT_BUSY: …` の項目として報告するため、`doctor` を実行する呼び戻しは終了コードではなく、その項目を（`--json` で）読む必要があります。

#### `package/` を変えない

`package/` は使う前に毎回ハッシュが計算され、ファイルが1つでも変わると、プラグインは使えなくなります（`PLUGIN_MODIFIED`）。そのため、コマンドは `RESOLOOP_PLUGIN_ROOT` に書き込んではいけません。仮想環境も `__pycache__` もビルドの出力もそこに置かないでください。代わりに `RESOLOOP_PLUGIN_DATA` へ書き込みます。ほかのランタイムではそのキャッシュとビルドのディレクトリを `data/` に向けてください。

例は Python と uv で次のようにしています。

```json
"program": "uv",
"args": ["run", "--project", "${pluginRoot}", "--locked", "python", "${pluginRoot}/hello.py"]
```

- ResoLoop は `UV_PROJECT_ENVIRONMENT` を `<data>/.venv` に設定するため、uv は仮想環境を `data/` に作ります。`PYTHONPYCACHEPREFIX` を `<data>/.cache/python` に設定するため、バイトコードも `data/` へ出ます。
- `pyproject.toml` は `[tool.uv] package = false` を宣言した、仮想プロジェクトです。uv はパッケージを導入せず、何もビルドしないため、`package/` の中には何も作られません（ビルドしたパッケージは、`*.egg-info` のような成果物をそこに残します）。
- `--locked` を付けると、`uv.lock` が古いとき、uv は書き換えずに失敗します。`pyproject.toml` を編集したら、導入元のディレクトリで `uv lock` を実行し、`uv.lock` をコミットしてください。
- 導入元のディレクトリの中で、`uv run` や `uv sync` を実行しないでください。そこに `.venv` ができます。仮想環境にはリンクがあり（`.venv/bin/python` はシンボリックリンクです）、Git の外のディレクトリの導入はリンクを拒否するため、コピーされずに、`PLUGIN_SOURCE_INVALID: Package contains a link.` で導入が失敗します。Git の導入元は無視されたファイルを導入しないため、その場合は `.gitignore` に `.venv/` を書いてください。または、スクラッチのコピーで試してください。

ResoLoop は依存するものを導入しません。利用者が何を導入すべきか（ここでは `uv`）はプラグイン自身の文書に書き、`tools` に宣言して、`doctor` が検査できるようにしてください。

### スキル

```json
"skills": [{ "name": "hello-guide", "path": "skills/hello-guide" }]
```

`skills/hello-guide/SKILL.md` が存在しなければなりません。`path` の下のすべてのファイルが相対パスを保ったまま `.agents/skills/hello-guide/` へ配布されます。`SKILL.md` には、使うエージェントが期待する front matter（例のように `name` と `description`）を書き、`name` には manifest の項目の名前を使ってください。予約されているスキル名は、[名前](#名前)を参照してください。

### レシピ

```json
"recipes": [{
  "name": "hello-card",
  "path": "recipes/hello-card.json",
  "prototype": "hello.card",
  "purpose": "A Slot with a RectTransform and a Text that says hello.",
  "parameters": { "key": "Unique stable key prefix (also the default Slot name)." }
}]
```

- JSON ファイルの形は書き出したレシピと同じです。最上位の `prototypes` オブジェクトの項目が prototype です。宣言がレシピを使うとき、`${key}` と、宣言したほかのすべてのパラメーターが置き換えられます。
- 呼び出し側が与える必要のある値は、すべて manifest の `parameters` に宣言し、`key` を含めてください。呼び出し側は `$with` でこれらのパラメーターを過不足なく与える必要があります。
- 実行中の Resonite で確かめた Component の型名だけを使ってください（たとえば `resoloop type query` で確かめます）。推測してはいけません。例は `FrooxEngine.UIX.RectTransform` と `FrooxEngine.UIX.Text` を使っています。
- レシピは `uix recipe describe`、`uix recipe export` と、そのレシピを使う宣言への `validate` で、オフラインで確かめます。生成されるキーの接頭辞は `uix-plugin--<plugin-name>--<recipe-name>--<caller-key>` です。

### tools

```json
"tools": [{ "name": "uv", "executable": "uv", "versionArgs": ["--version"], "required": true, "hint": "Install uv from https://docs.astral.sh/uv/ and make sure it is on PATH." }]
```

コマンドが必要とする外部プログラムは、すべて宣言してください。利用者に導入のしかたを伝える `hint` を付けます。一部のコマンドだけが必要とする tool には `"required": false` を使うと、その tool が不足していても `ready` は失敗せず、警告として表示されます。

### 作ったプラグインを試す

自分のディレクトリで、ResoLoop のリポジトリの外で開発します。スクラッチのプロジェクト（一時ディレクトリで `resoloop init`）へ導入し、コマンドを実行して、また削除します。プラグイン名はプロジェクトごとのものなので、変更のたびに `plugin remove NAME --yes` と新しい `plugin install` が必要です。

## 安全と制限

### 信頼

信頼できる導入元のプラグインだけを導入し、先にコードを読んでください。導入では何も実行されませんが、最初のコマンドは実行します。

- プラグインのコマンドは利用者として動きます。利用者が読み書きできるファイルはすべて読み書きでき、ネットワークを使い、プロセスを起動できます。
- 環境（古い `RESOLOOP_PLUGIN_*` 変数を除く）を継承するため、その中のトークンや資格情報も含みます。
- ResoLoop はプラグインに署名せず、危険なコードがないか検査せず、サンドボックスの中でも動かしません。

### 導入元の確かめ方

- Git の導入元ではレビューしたコミットの完全な SHA を `--ref` に使ってください。そのうえで、導入された SHA とレビューした SHA を比べます。導入の直後は、`plugin install --json` が `commit` として SHA を出力します。あとからは記録の `commit` を読みます。

  ```bash
  jq -r .commit "$PROJECT/.resoloop/plugins/hello/install.json"
  git -C "$GITSRC" rev-parse HEAD
  ```

  ```output
  <sha>
  <sha>
  ```

  どちらの行も期待する SHA を表示しなければなりません。`plugin list` が表示するのは、導入の状態と検証の問題であり、コミットではありません。
- ローカルのディレクトリからの導入は、導入元のパスだけを記録し、`commit` は `null` です。それが重要な場合は、ファイルを自分で比べてください。

### 記録との照合

各プラグインの `install.json` には、`package/` のすべてのファイルの SHA-256 があります。プラグインが使われる前に（`plugin list`、コマンド、スキルの配布、レシピの参照、`doctor` で）、ResoLoop はパッケージ全体をもう一度読み、記録と比べます。ファイルの変更、ファイルの追加、ファイルの欠落、リンクに置き換えられたファイル、manifest の名前やバージョンの違いがあると、そのプラグインは使われません。たとえば次のようになります。

```bash
echo "# edited" >> "$PROJECT/.resoloop/plugins/hello/package/hello.py"
resoloop plugin list --project "$PROJECT"
resoloop --project "$PROJECT" hello
```

```output
hello  PROBLEM PLUGIN_MODIFIED: Package content differs from the install record.
PLUGIN_MODIFIED: Installed plugins failed verification.
```

（2行目は stderr に出て、コマンドは終了コード 6 で終わります。）壊れたプラグインが自然に直ることはありません。削除して（`plugin remove NAME --yes` は、壊れたプラグインでも動きます。ただし、プラグインのディレクトリか `package` 自体がリンクの場合を除きます。[更新と削除](#更新と削除)を参照）、導入し直してください。

照合の対象に**含まれない**ものは、`data/` と、ファイルのパーミッションです（ファイルの集合と、各ファイルの内容のハッシュを比べます）。

### 信頼とハッシュとサンドボックスの違い

- **ハッシュは意図しない差分を検出します。** 誤って編集された、切り詰められた、書きかけのパッケージや、記録を更新しなかった何かが改ざんしたパッケージを検出します。
- **ハッシュは署名ではありません。** ハッシュが示すのは、ファイルが導入されたときのファイルであることだけで、プラグインを書いた人については何も示しません。
- **ハッシュはサンドボックスではありません。** 検証済みのプラグインも利用者の権限のすべてで動きます。
- **`install.json` を書き込める相手への防御にはなりません。** 利用者の権限を持つ人は `package/` と `install.json` を一緒に変えられ、そのプラグインは問題なく検証されます。自分のアカウントの悪意ある利用者に対する完全な防御は対象外です。
- コマンドのあとにプロセスグループやジョブを停止するのは、後始末であり、隔離ではありません。グループを抜けたプロセス（Linux では、たとえば `setsid` で抜けたもの）は動き続けます。

### 現行の利用条件

これらは現在の実装の条件です。コードで確かめ、可能なものは Linux で実行しました。管理領域のリンクの規則と Windows の規則は、コードを読んで確かめたもので、Windows では実行していません。

- Git の導入元は作業ツリーのルートでなければなりません。Git の作業ツリーのサブディレクトリとベアリポジトリは `PLUGIN_SOURCE_INVALID` です。入れ子のリポジトリを含む Git の導入元も同じです。Git の導入元の無視されたファイルは導入されません。
- Git の外の導入元は全体がコピーされます。その中の `.git`、リンク、特殊ファイルは拒否されます。
- 導入元のパスはリンクを解決して扱われます。ローカルの導入元のパスはディレクトリへのリンクでもかまいません。ResoLoop は実体のディレクトリへ解決し、その実体が Git のルートかどうかとコピーされるファイルを決めます。記録される `source.location` は実体のパスです。`--project` も同じです。
- ResoLoop が書き込む場所とコピーする場所では、リンクが拒否されます。コピーされるパッケージの中のリンクは、追跡されているかどうかにかかわらず拒否されます（Git の導入元の無視されたリンクはコピーされず、関係ありません）。`.resoloop`、`plugins`、プラグインのディレクトリ、`package`、`data` のどれかがリンクだと拒否され（`PLUGIN_PATH_UNSAFE`。検証は、`package` 自体がリンクの場合も、`package/` の中でリンクが見つかった場合も、`PLUGIN_MODIFIED` として報告します）、導入、削除、コマンドはそのリンクをたどりません。通常のディレクトリを使ってください。
- 導入元とプロジェクトは重なってはいけません。どちらも、もう一方を含んではいけません（`file://` URL も同じです）。
- `--ref` はすべての Git URL で必要です。`file://` も同じです。ディレクトリでは拒否されます。
- スキルの配布が見るのは1つのディレクトリだけです。`init DIRECTORY` と `skills sync DIRECTORY` はそのディレクトリそのものの `.resoloop.json` を使います。導入、一覧、削除、コマンド、doctor、レシピは、代わりにカレントディレクトリから上へ向かって探します。
- 失敗した導入は片付けられます。削除されるのは、その導入の staging ディレクトリだけです。すでにある導入物、その記録、その `data/` は変わりません。すでにファイルかディレクトリがある配置先が上書きされることはなく、ほかの導入の staging ディレクトリには触れません。
- プラグインのコマンドは Linux と Windows でだけ動きます。Windows では `.exe` のプログラムだけが起動されます（`.cmd` と `.bat` は起動されません）。

### 上限

| 項目 | 上限 |
| --- | --- |
| パッケージのファイル数 | 10,000 |
| パッケージの大きさ | 200 MiB |
| manifest の大きさ | 256 KiB |
| manifest の項目数 | commands 50 件、skills 50 件、recipes 50 件、tools 20 件 |
| テキストの欄 | `description`、`purpose`、`hint` は 500 文字。`prototype` は 100 文字。 |
| `install.json`、スキルの記録 | それぞれ 8 MiB（スキルの記録がこれより大きくなる場合は `PLUGIN_SKILL_RECORD_TOO_LARGE`） |
| Git の操作 | それぞれ 120 秒 |
| コマンドの期限 | `timeoutSeconds`（1〜86400）。ない場合は `commandTimeoutSeconds`（既定 900） |
| doctor の tool の検査 | 30 秒、出力 64 KiB、最初の出力行だけを表示（最大 200 文字） |

### 操作は1つずつ

導入、削除、一覧、検証、すべてのプラグインのコマンドは、プロジェクトのプラグインのロック（`.resoloop/plugins/.lock`）を取ります。ほかの操作がそのロックを保持していると、すぐに `PLUGIN_PROJECT_BUSY`（終了コード 7）で失敗します。実行中のプラグインのコマンドがロックを保持するのは、実行の間じゅうです。「失敗する」の例外は `doctor` で、失敗した `plugins` の項目を `PLUGIN_PROJECT_BUSY` とともに出力し、終了コード 0 で終わります。このロックは協調的なものです。ResoLoop 自身の操作を直列にしますが、ほかのプログラムは対象外です。

## エラー

エラーは stderr に `CODE: message` の形で出力され、`next:` の行が続きます。`--json` を付けると、stderr に `{"ok":false,"error":{"code","message","context","suggestions"}}` の形で出力されます。

| コード | 終了コード | 意味と対処 |
| --- | --- | --- |
| `PLUGIN_PROJECT_NOT_FOUND` | 3 | `--project` のディレクトリに `.resoloop.json` がない、またはカレントディレクトリから上へ向かっても見つかりません。`resoloop init DIR` を実行するか、`--project` を渡してください。`--project` がないとき、これを出すのは `plugin install`、`plugin list`、`plugin remove` だけで、`uix recipe` と `doctor` はプラグインなしで続けます。`doctor --project DIR` は、これを失敗した `plugins` の項目として報告し、終了コード 0 で終わります。 |
| `PLUGIN_SOURCE_NOT_FOUND` | 5 | `SOURCE` が、存在するディレクトリでも URL でもありません。パスを確かめてください。 |
| `PLUGIN_SOURCE_INVALID` | 6 | 導入元を導入できません。Git のサブディレクトリ、ベアリポジトリ、入れ子のリポジトリや `.git`、リンクや特殊ファイル、プロジェクトとの重なり、ディレクトリと一緒の `--ref`、不正または曖昧な ref、拒否された URL（資格情報、クエリ、フラグメント、未対応のスキーム）、通常でない Git のエントリ、多すぎるファイルやバイト数のどれかです。メッセージを読み、[現行の利用条件](#現行の利用条件)を参照してください。 |
| `PLUGIN_MANIFEST_NOT_FOUND` | 6 | `resoloop-plugin.json` がパッケージのルートにありません。Git では、リポジトリのルートになければなりません。 |
| `PLUGIN_MANIFEST_INVALID` | 6 | manifest か、それが参照するファイルが不正です。各 `$.path: message` は `context.errors`（`--json`）を見てください。 |
| `PLUGIN_SCHEMA_UNSUPPORTED` | 6 | `schemaVersion` が `1` ではありません。manifest のそれ以外の部分が有効なときに報告されます。 |
| `PLUGIN_INCOMPATIBLE_VERSION` | 6 | 実行中の ResoLoop が、プラグインの `minVersion` と `maxVersionExclusive` の範囲の外です。互換性のある ResoLoop かプラグインのバージョンを使ってください。 |
| `PLUGIN_NAME_CONFLICT` | 6 | プラグイン、コマンド、スキル、レシピの名前が、予約されているか、導入済みの別のプラグインですでに使われています。manifest で名前を変えるか、もう一方のプラグインを削除してください。 |
| `PLUGIN_ALREADY_INSTALLED` | 6 | その名前のプラグインが導入済みです。先に削除してください。 |
| `PLUGIN_GIT_REF_REQUIRED` | 2 | Git URL が `--ref` なしで渡されました。`--ref` を付けてください（コミット SHA の全体が最も確実です）。 |
| `PLUGIN_GIT_REF_NOT_FOUND` | 5 | その ref がリポジトリにありません。 |
| `PLUGIN_GIT_FAILED` | 9 | `git` が失敗したか、時間切れになりました（クローン、ネットワーク、認証、`git` がない場合）。メッセージはあえて一般的な文面にしてあり、URL を含みません。`git` で直接クローンを試してください。 |
| `PLUGIN_MODIFIED` | 6 | パッケージが `install.json` と一致しなくなりました。プラグインを削除して導入し直してください。このプラグインがある間は、プロジェクトのプラグインのコマンド、すべてのプラグインの `plugin install`、スキルの配布、プラグインのレシピの実行が拒否されます。 |
| `PLUGIN_RECORD_INVALID` | 6 | `install.json` が読めない、形式が不正、または manifest と矛盾しています。プラグインを削除して導入し直してください。 |
| `PLUGIN_PATH_UNSAFE` | 6 | ResoLoop が管理するパス（`.resoloop`、`plugins`、プラグインのディレクトリ、`package`、`data`）の中に、リンク、特殊ファイル、ディレクトリでないものがあります。通常のディレクトリに置き換えてください。 |
| `PLUGIN_IGNORE_CONFLICT` | 6 | `.resoloop/plugins/.gitignore` があり、管理対象の `*` ではありません。別の場所へ移してください。 |
| `PLUGIN_REMOVE_REFUSED` | 6 | `plugin remove` がディレクトリを安全に削除できませんでした。パーミッションを確かめて、もう一度試してください。 |
| `CONFIRMATION_REQUIRED` | 6 | `--yes` のない `plugin remove` です。対象を確かめたうえで、`--yes` を付けてください。 |
| `PLUGIN_NOT_INSTALLED` | 5 | その名前のプラグインがありません（またはレシピが、導入されていないプラグインを求めています）。`plugin list` を確かめてください。 |
| `PLUGIN_NAME_INVALID` | 2 | `plugin remove` に渡したプラグイン名が、名前の規則に従っていません。 |
| `PLUGIN_PROJECT_BUSY` | 7 | 別の操作（多くは実行中のプラグインのコマンド）が、プロジェクトのプラグインのロックを保持しています。その終了を待ち、コマンドをもう一度実行してください。`doctor` は終了コード 7 では終わらず、`[fail] plugins: PLUGIN_PROJECT_BUSY: …` を出力して終了コード 0 で終わります。 |
| `PLUGIN_COMMAND_START_FAILED` | 9 | プログラムを起動できませんでした。絶対パスの `PATH` の項目に見つからない、パッケージのファイルがない、通常のファイルでない、または実行できない、Windows の `.exe` の規則、引数に NUL がある、または ResoLoop が自分の呼び戻し方を判断できなかった、のどれかです。プログラムを導入するか、manifest やファイルのモードを直してください。 |
| `PLUGIN_COMMAND_TIMEOUT` | 8 | コマンドが期限を超えて、停止されました。manifest の `timeoutSeconds` か `commandTimeoutSeconds` を大きくしてください。 |
| `PLUGIN_COMMAND_CANCELLED` | 7 | コマンドが中断され（Ctrl+C か終了のシグナル）、そのプロセスツリーが停止されました。 |
| `PLUGIN_COMMAND_STOP_FAILED` | 7 | コマンドのプロセスツリーが停止したことを、ResoLoop が確認できませんでした。もう一度実行する前に、残っているプロセスを探してください。 |
| `UNKNOWN_COMMAND` | 2 | その名前は、本体のコマンドでも、選んだプロジェクトの導入済みのプラグインのコマンドでもありません。 |
| `SKILL_SYNC_REQUIRED` | 6 | `skills sync --check` が、書き込むべきファイルを見つけました。`skills sync --update` を実行してください。プラグインのスキルだけが同期待ちのときも、メッセージは同梱スキルに触れています。`--json` では、同期待ちのファイルは stderr の `error.context.report.plugins` にあります。 |
| `SKILL_SYNC_CONFLICT` | 6 | 配布済みのスキルが編集された、所有者がない、別のプラグインが所有している、またはパスが安全ではありません。何も書き込まれていません。`context.pluginConflicts` を見てください。 |
| `UIX_RECIPE_NOT_FOUND` | 5 | そのレシピはありません。`uix recipe list` を実行してください。プロジェクトが見つからないとき（プロジェクトの外で `--project` がなければ、本体のレシピだけが知られています）は、`hello:hello-card` のようなプラグインのレシピでも、このエラーになります。プロジェクトの中で実行するか、`--project` を渡してください。 |

ほかにも次のプラグインのコードに出会うことがあります。`PLUGIN_SKILL_RECORD_INVALID`（スキルの記録 `.agents/skills/.resoloop-plugins.json` が壊れています。ResoLoop は壊れた記録を空として扱わないため、バージョン管理から復元してください）、`PLUGIN_SKILL_WRITE_FAILED`（スキルのファイルを書き込めませんでした。パーミッションを直し、`skills sync --update` をもう一度実行してください）、`PLUGIN_SKILL_RECORD_TOO_LARGE` です。
