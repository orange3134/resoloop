# rloop

rloopは、CodexやClaude CodeなどのAIエージェントがResoniteを

観測 → 実装 → 適用 → 検証 → 解析 → 修正

のループで操作するための非対話CLIです。ResoniteLinkを単純に露出せず、Slot、Component、member、runtime typeという安定した概念へ写像します。v0.1は接続、Hierarchy/検索/inspection、Slot・Component CRUD、Reflection、JSON apply、Flux-SDK build/deploy、ログtailを実装しています。

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

1. CLI: --url, --timeout, --library-path
2. 環境変数: RESONITE_LINK_URL, RLOOP_TIMEOUT_SECONDS, RESONITE_MANAGED_DATA_PATH
3. カレントディレクトリから親方向で最初の .rloop.json
4. %USERPROFILE%\.rloop\config.json

URLがなければRESONITE_LINK_URL_MISSINGを返します。設定例は[examples/rloop.example.json](examples/rloop.example.json)です。

## Quick start

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
| Connection | status, ping |
| Observe | hierarchy, find, inspect |
| Slot | slot create, slot set, slot delete --yes |
| Component | component list/inspect/add/set/remove |
| Reflection | type search, type describe |
| Declarative | apply FILE.json |
| Flux | flux status/check/build/watch/deploy |
| Diagnostics | logs |

全オプションは rloop help で確認できます。hierarchyのdefault depthは2、findは8です。depth -1は全階層なので大規模worldでは避けてください。削除は --yes 必須で、Root削除は常に拒否されます。IDはResoniteLink session内だけで安定し、world再読込後には再取得が必要です。

## Declarative apply

[examples/agent-test.json](examples/agent-test.json)を参照してください。

~~~powershell
rloop apply examples/agent-test.json --json
~~~

v0.1 applyは同じparent直下の同名Slotを再利用し、transformを更新し、同じComponent型があればfieldを更新します。複数の同名Slotがある場合は安全のため停止します。

## Flux-SDK

~~~powershell
dotnet tool install --global Papaltine.FluxSDK --version 1.9.0
$env:RESONITE_MANAGED_DATA_PATH="D:\Users\star_\AppData\Local\RESO Launcher\profiles\profile1\Game"

rloop flux check examples/flux/RLoopHello.pg --project examples/flux --json
rloop flux build examples/flux/RLoopHello.pg --project examples/flux --json
rloop flux deploy --project examples/flux --module RLoopHello --parent RLoop_Test --json
~~~

build/check/watchは既存Flux-SDK CLIをラップします。flux deployはFlux-SDK 1.9のLoader.replaceを利用し、指定parent配下で同じmodule名のchildだけを置換します。Flux-SDK programmatic APIもBetaです。RLoop.Flux.Deployerに隔離しているため追従箇所は限定されています。

## Codex Skills

skills/codexには次のworkflow Skillがあります。

- resonite-build: 観測から編集・Reflection・検証・修正までの統合ループ
- resonite-debug: read-first診断
- resonite-inspect: コンテキストを浪費しない観測
- resonite-flux: ProtoGraph check/build/deploy

Codexのpersonal skillsへコピーする例:

~~~powershell
Copy-Item -Recurse skills\codex\* "$env:USERPROFILE\.codex\skills\"
~~~

CLI commandはprimitive、Skillはworkflowです。SkillはComponent/member名を推測せず、先にruntime Reflectionするよう指示します。

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
- applyはv0.1ではJSONのみです。完全なdiff、削除収束、state fileは未実装です。
- member conversionはbool/numeric/string/URI/type/float vectors/quaternion/color/enum/referenceを対象とし、list/dictionary/sync objectの書き換えは未対応です。
- screenshotは未実装です。安定したwindow selection/capture方式をCLI本体へ持ち込まず、将来optional Windows helperとして実装予定です。
- logsはLink protocolからのstreamではなく、明示されたローカルlog fileのtailです。
- flux watchはcompiler watchであり、Resoniteへのhot deploy loopは今後の拡張です。

## License and upstream notes

ResoniteLinkはMIT、Flux-SDK programmatic integrationはAGPL-3.0-or-laterです。この構成はFlux-SDKへリンクするため、rloopの配布条件もAGPL-3.0-or-laterとしています。公開インターフェースを中心に実装し、非公開Resoniteソースやデコンパイル結果を同梱していません。
