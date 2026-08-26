# rloop

rloopは、CodexやClaude CodeなどのAIエージェントがResoniteを

観測 → 実装 → 適用 → 検証 → 解析 → 修正

のループで操作するための非対話CLIです。ResoniteLinkを単純に露出せず、Slot、Component、member、runtime typeという安定した概念へ写像します。v0.1はproject初期化・診断、接続、Hierarchy/検索/inspection、Slot・Component CRUD、Reflection、再利用可能なJSON apply、scene/test artifact、安全な差分収束、Flux-SDK build/hot deploy、ログtailを実装しています。

## Architecture

~~~text
Agent / Codex Skills
        │
        ▼
   RLoop.Cli ───────► RLoop.Flux ─────► flux-sdk / FluxSDK.Core
        │
        ▼
   RLoop.Core (models, workflows, IResoniteClient)
        │
        ▼
   RLoop.ResoniteLink (0.13.1 adapter)
        │
        ▼
   ResoniteLink WebSocket ─────────────► Resonite
~~~

ResoniteLink依存はAdapterに隔離されています。上位層は公式ライブラリの生JSONや型へ依存しません。Flux-SDKのコンパイラは再実装せず、CLIと公式programmatic APIを利用します。

## Requirements

- Windows 10/11
- .NET SDK 10
- Resoniteの対象worldでResoniteLinkを有効化
- ProtoFlux利用時はFlux-SDK 1.9推奨
- FluxのReflection用にResonite managed DLL directory

Resonite本体のデコンパイルはビルド・実行の必須依存ではありません。

## Build and installation

~~~powershell
dotnet build RLoop.slnx
dotnet test RLoop.slnx --no-build
dotnet pack src/RLoop.Cli/RLoop.Cli.csproj -c Release -o artifacts
dotnet tool install --global --add-source .\artifacts RLoop.Cli --version 0.1.0
~~~

開発中は次でも実行できます。

~~~powershell
dotnet run --project src/RLoop.Cli -- help
~~~

## ResoniteLink configuration

最低限、Resoniteに表示された現在のportを設定します。portは起動ごとに変わるためハードコードされていません。

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
rloop status --json
~~~

設定優先順位:

1. CLI: --url, --timeout, --command-timeout, --library-path
2. 環境変数: RESONITE_LINK_URL, RLOOP_TIMEOUT_SECONDS, RLOOP_COMMAND_TIMEOUT_SECONDS, RESONITE_MANAGED_DATA_PATH
3. カレントディレクトリから親方向で最初の .rloop.json
4. %USERPROFILE%\.rloop\config.json

URLがなければRESONITE_LINK_URL_MISSINGを返します。設定例は[examples/rloop.example.json](examples/rloop.example.json)です。

## Quick start

自分のプロジェクトをゼロから開始する手順は[docs/QUICKSTART.md](docs/QUICKSTART.md)にまとめています。今後の実装順は[docs/ROADMAP.md](docs/ROADMAP.md)を参照してください。

~~~powershell
New-Item -ItemType Directory MyResoniteProject
Set-Location MyResoniteProject
rloop init .
$env:RESONITE_LINK_URL="ws://localhost:<current-port>"
rloop doctor
rloop validate content/main.json --json
rloop plan content/main.json --json
rloop apply content/main.json --json
~~~

AIエージェントからは --json を標準にしてください。

~~~powershell
rloop status --json
rloop hierarchy --depth 2 --json
rloop find --name Cube --json
rloop inspect Root/MyObject --members --json
rloop type search Grabbable --json
rloop type describe FrooxEngine.Grabbable --json

$slot = (rloop slot create --parent Root --name RLoop_Test --position 0,1.5,2 --json | ConvertFrom-Json).data.id
$component = (rloop component add $slot FrooxEngine.Grabbable --set Scalable=true --json | ConvertFrom-Json).data.id
rloop component set $component Scalable false --json
rloop inspect $slot --members --json
rloop slot delete $slot --yes --json
~~~

Reflectionでmemberがlistだと確認できた場合、対応済みのfield/reference要素はJSON arrayで設定できます。たとえばMeshRendererへmaterial providerを割り当てる場合:

~~~powershell
rloop component set $renderer Materials ('["' + $materialProviderId + '"]') --json
~~~

成功出力:

~~~json
{"ok":true,"data":{"id":"Reso_...","updated":true}}
~~~

自己修正可能な失敗出力（stderr、non-zero exit code）:

~~~json
{"ok":false,"error":{"code":"COMPONENT_TYPE_NOT_FOUND","message":"Component type 'Grabbabl' was not found.","context":{"query":"Grabbabl"},"suggestions":["[FrooxEngine]FrooxEngine.Grabbable"]}}
~~~

Exit codeは、2=引数、3=設定、4=接続、5=not found、6=validation、7=操作、8=timeout、9=外部ツール失敗です。

## Command reference

| Area | Commands |
|---|---|
| Project | init, doctor |
| Connection | status, ping |
| Observe | hierarchy, find, inspect |
| Slot | slot create, slot set, slot delete --yes |
| Component | component list/inspect/add/set/remove |
| Reflection | type search, type describe |
| Declarative | validate, plan/diff, apply [--prune --yes], scene summary, capture, test |
| Flux | flux status/check/build/watch/deploy/deploy-manifest |
| Diagnostics | doctor, logs |

全オプションは rloop help で確認できます。hierarchyのdefault depthは2、findは8です。depth -1は全階層なので大規模worldでは避けてください。削除は --yes 必須で、Root削除は常に拒否されます。IDはResoniteLink session内だけで安定し、world再読込後には再取得が必要です。

## Declarative apply

[examples/agent-test.json](examples/agent-test.json)を参照してください。

~~~powershell
rloop validate examples/agent-test.json --json
rloop validate examples/agent-test.json --strict --json
rloop plan examples/agent-test.json --json
rloop apply examples/agent-test.json --json
~~~

Material、家具、照明、ReflectionMaterial、TouchButtonを含むnestedな実例は[examples/house-world.json](examples/house-world.json)です。

~~~powershell
rloop apply examples/house-world.json --json
~~~

schema v1では、top-levelに `schemaVersion: "1"`、`ownership.key`、root `slot.key`が必要です。ownershipごとのstateは既定でproject内の `.rloop/state/<ownership>.json` に保存され、途中経過もcheckpointされます。このdirectoryは `rloop init` が生成するignore設定によりversion controlから除外されます。

`children` でSlot階層を宣言できます。SlotとComponentの明示的 `key` はrenameやセッション変更後の再解決に使われます。同じSlotに同型Componentを複数宣言する場合は、それぞれにkeyが必要です。fieldから `$slot:key`、`$component:key`、`$member:key.MemberName`、`$asset:key` を参照でき、forward referenceも利用できます。旧 `$ref:key` も互換です。

include、parameter/variable、prototype/instance、repeat、asset、camera、assertionの仕様は[docs/DECLARATIVE.md](docs/DECLARATIVE.md)にまとめています。house fixtureは3ファイルへ分割し、22個のboxと4本のtable legをprototype化しました。展開結果69 Slot・148 Componentを維持したまま、宣言量は46,178 byteから42,430 byteへ8.1%減っています。

`validate` は接続なしのschema・値形状・key・参照検査、`validate --strict` は接続先のruntime Reflectionを使ったComponent/member検査です。`plan` はworldを変更せずcreate/update/no-opを列挙します。既存rootを初めて管理対象へ取り込む場合、inspectとplanで完全一致対象を確認してから一度だけ `--adopt` を付けます。stateがある通常の再適用では不要です。

~~~powershell
rloop plan content/main.json --json
rloop apply content/main.json --profile --json
rloop diff content/main.json --json
rloop scene summary content/main.json --output artifacts/scene.json --json
rloop capture content/main.json --camera main --output artifacts/main.svg --json
rloop test content/main.json --json
~~~

applyの最終JSONはstdout、進捗はstderrへ分離されます。機械処理できる進捗が必要なら `--ndjson-progress`、表示を抑えるなら `--quiet` を使います。`--timeout` は各ResoniteLink request、`--command-timeout` はcommand全体のdeadlineです。Ctrl+Cやdeadlineで中断した場合はstate fileと完了件数が報告され、同じapplyで再開できます。

## Flux-SDK

~~~powershell
dotnet tool install --global Papaltine.FluxSDK --version 1.9.0
$env:RESONITE_MANAGED_DATA_PATH="D:\Users\star_\AppData\Local\RESO Launcher\profiles\profile1\Game"

rloop flux check examples/flux/RLoopHello.pg --project examples/flux --json
rloop flux build examples/flux/RLoopHello.pg --project examples/flux --json
rloop flux deploy --project examples/flux --module RLoopHello --parent RLoop_Test --json
rloop flux deploy-manifest examples/flux/rloop.flux.json --json
rloop flux watch examples/flux/rloop.flux.json --json
~~~

`.pg`に対するbuild/check/watchは既存Flux-SDK CLIをラップします。JSON module manifestに対するwatchは依存順に成功buildだけを再deployします。`$slot:key` parentはworld stateから現在session向けに検証・再解決されます。deployはFlux-SDK 1.9のLoader.replaceを利用し、moduleごとのbefore/after ID、構造差分、checkpoint recoveryを返します。

## Codex Skills

skills/codexには次のworkflow Skillがあります。

- resonite-build: 観測から編集・Reflection・検証・修正までの統合ループ
- resonite-debug: read-first診断
- resonite-inspect: コンテキストを浪費しない観測
- resonite-flux: ProtoGraph check/build/deploy

Resoniteコンテンツproject限定のskillsとして導入する例です。対象projectのrootで実行します。

~~~powershell
$rloopRepository = "D:\path\to\resonite-link-cli-loop"
New-Item -ItemType Directory -Force .agents\skills | Out-Null
Copy-Item -Recurse -Force "$rloopRepository\skills\codex\*" .agents\skills\
~~~

Codexはcurrent directoryからrepository rootまでの `.agents/skills/` を読み込むため、このskillsは対象project内でだけ利用されます。詳細は[OpenAI公式のskill discovery仕様](https://developers.openai.com/codex/skills#where-codex-loads-local-skills)を参照してください。CLI commandはprimitive、Skillはworkflowです。SkillはComponent/member名を推測せず、先にruntime Reflectionするよう指示します。

## Tests

~~~powershell
dotnet test tests/RLoop.Tests/RLoop.Tests.csproj

$env:RLOOP_RUN_INTEGRATION="1"
$env:RESONITE_LINK_URL="ws://localhost:12449"
dotnet test tests/RLoop.IntegrationTests/RLoop.IntegrationTests.csproj --filter Category=Integration
~~~

Integration testは一意なRLoop_Test_Integration_* Slotだけを作り、finallyでcleanupします。既存ユーザーコンテンツは操作しません。

## Logs and troubleshooting

rloop logsはResonite内部の非公開APIへ依存せず、設定されたファイルまたはdirectoryの最新.logをtailします。

~~~powershell
$env:RESONITE_LOG_PATH="C:\path\to\Resonite\Logs"
rloop logs --tail 200 --json
~~~

- CONNECTION_FAILED: ResoniteLinkがworldで有効か、画面上のportとURLが同じか確認
- COMPONENT_TYPE_NOT_FOUND: type searchの完全な結果を使う
- COMPONENT_MEMBER_NOT_FOUND: type describeでflattened memberを確認
- VALUE_CONVERSION_FAILED: vectorsはcomma区切り、quaternion/colorは4要素
- FLUX_SDK_NOT_FOUND: flux-sdkをglobal toolとして導入、またはRLOOP_FLUX_EXECUTABLEを設定
- Flux type error: RESONITE_MANAGED_DATA_PATHまたは --library-path を確認

## Known limitations

- ResoniteLink 0.13.1自体がBetaで、breaking changeの可能性があります。
- applyはschema v1のJSONのみです。operationは非atomicでrollbackはできませんが、操作単位のcheckpointと再実行手順を返します。
- List更新は公開API上whole-member replacementです。`diff`は要素added/removedを表示してから一括更新します。
- ResoniteLink 0.13.1にscreenshot APIがないため、captureは決定的なcamera-space SVGで、最終レンダリング画像ではありません。結果は `screenshotAvailable: false` を明示します。
- logsはLink protocolからのstreamではなく、明示されたローカルlog fileのtailです。
- runtime probeはpublic Reflectionに公開されたSyncMethodだけを明示許可付きで呼びます。公開されないinteractionはstructural-onlyです。

## License and upstream notes

ResoniteLinkはMIT、Flux-SDK programmatic integrationはAGPL-3.0-or-laterです。この構成はFlux-SDKへリンクするため、rloopの配布条件もAGPL-3.0-or-laterとしています。公開インターフェースを中心に実装し、非公開Resoniteソースやデコンパイル結果を同梱していません。
