# resoloop 詳細資料

公開向けの概要と導入手順は [README.md](README.md) を参照してください。この文書には、コマンド仕様、宣言的 apply、Flux-SDK、設計、制約などの詳細をまとめています。

resoloopは、CodexやClaude CodeなどのAIエージェントがResoniteを

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

## Installation

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.5
resoloop --version
~~~

Preview版の更新:

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.5
~~~

release自動化とnuget.org Trusted Publishingの設定は[docs/RELEASING.md](docs/RELEASING.md)を参照してください。

ソースから検証・package作成する場合:

~~~powershell
dotnet build ResoLoop.slnx
dotnet test ResoLoop.slnx --no-build
dotnet pack src/RLoop.Cli/RLoop.Cli.csproj -c Release -o artifacts
dotnet tool install --global --add-source .\artifacts ResoLoop --version 0.1.0-preview.5
~~~

開発中は次でも実行できます。

~~~powershell
dotnet run --project src/RLoop.Cli -- help
~~~

## ResoniteLink configuration

最低限、Resoniteに表示された現在のportを設定します。portは起動ごとに変わるためハードコードされていません。

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
resoloop status --json
~~~

設定優先順位:

1. CLI: --url, --timeout, --command-timeout, --library-path
2. 環境変数: RESONITE_LINK_URL, RESOLOOP_TIMEOUT_SECONDS, RESOLOOP_COMMAND_TIMEOUT_SECONDS, RESOLOOP_FLUX_EXECUTABLE, RESONITE_MANAGED_DATA_PATH
3. カレントディレクトリから親方向で最初の .resoloop.json
4. %USERPROFILE%\.resoloop\config.json

URLがなければRESONITE_LINK_URL_MISSINGを返します。設定例は[examples/resoloop.example.json](examples/resoloop.example.json)です。

## Quick start

自分のプロジェクトをゼロから開始する手順は[docs/QUICKSTART.md](docs/QUICKSTART.md)にまとめています。今後の実装順は[docs/ROADMAP.md](docs/ROADMAP.md)を参照してください。

~~~powershell
resoloop init MyResoniteProject
Set-Location MyResoniteProject
resoloop skills sync --check
$env:RESONITE_LINK_URL="ws://localhost:<current-port>"
resoloop doctor
resoloop validate content/main.json --json
resoloop plan content/main.json --json
resoloop apply content/main.json --json
~~~

AIエージェントからは --json を標準にしてください。

~~~powershell
resoloop status --json
resoloop hierarchy --depth 2 --json
resoloop find --name Cube --json
resoloop inspect Root/MyObject --members --json
resoloop find --name Status --under Root/ResoLoop_Test_Game --direct-children --json
resoloop inspect Root/ResoLoop_Test_Game --component Slider --member SnapPositions --depth 4 --json
resoloop type search Grabbable --json
resoloop type describe FrooxEngine.Grabbable --json

$slot = (resoloop slot create --parent Root --name ResoLoop_Test --position 0,1.5,2 --json | ConvertFrom-Json).data.id
$component = (resoloop component add $slot FrooxEngine.Grabbable --set Scalable=true --json | ConvertFrom-Json).data.id
resoloop component set $component Scalable false --json
resoloop inspect $slot --members --json
resoloop slot delete $slot --yes --json
~~~

Reflectionでmemberがlistだと確認できた場合、対応済みのfield/reference要素はJSON arrayで設定できます。たとえばMeshRendererへmaterial providerを割り当てる場合:

~~~powershell
resoloop component set $renderer Materials ('["' + $materialProviderId + '"]') --json
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

全オプションは `resoloop help`、事故に関係する詳細は `resoloop help apply` / `resoloop help diff` で確認できます。hierarchyのdefault depthは2、findは8です。大規模worldでは `find --under` / `--direct-children` と `inspect --component` / `--member` を優先し、depth -1は避けてください。削除は --yes 必須で、Root削除は常に拒否されます。statusの `connectionId` はResoniteLink接続ごとのIDで、安定したworld identityではありません。保存済みIDは接続変更後にpath/keyから再解決されます。

## Declarative apply

[examples/agent-test.json](examples/agent-test.json)を参照してください。

~~~powershell
resoloop validate examples/agent-test.json --json
resoloop validate examples/agent-test.json --strict --json
resoloop plan examples/agent-test.json --json
resoloop apply examples/agent-test.json --json
~~~

Material、家具、照明、ReflectionMaterial、TouchButtonを含むnestedな実例は[examples/house-world.json](examples/house-world.json)です。

~~~powershell
resoloop apply examples/house-world.json --json
~~~

schema v1では、top-levelに `schemaVersion: "1"`、`ownership.key`、root `slot.key`が必要です。ownershipごとのstateは既定でproject内の `.resoloop/state/<ownership>.json` に保存され、途中経過もcheckpointされます。このdirectoryは `resoloop init` が生成するignore設定によりversion controlから除外されます。

`children` でSlot階層を宣言できます。SlotとComponentの明示的 `key` はrename、親変更、セッション変更後の再解決に使われます。stable Slotの親変更はIDを維持する`relocate`、stable Componentの親Slot変更は作成・参照再解決・旧Component削除としてplan/applyされます。同じSlotに同型Componentを複数宣言する場合は、それぞれにkeyが必要です。`identityFields`へ不変な管理memberを指定でき、managed reference topologyもstateへ保存されるため、同型Componentの挿入後も再接続時に誤接続せず再解決できます。`fields`は毎回収束させる値、`initialFields`はComponent新規作成時だけ設定してruntime dataを上書きしない値です。`managedFields`はresoloopが収束させるposition/rotation/scaleを限定し、`preserveWorldTransform`は既存Slotの現在のlocal transform値を保持します。親変更時は`relocationTransform: "local"`が既定で、`"world"`なら旧world transformから新しいlocal transformを計算して以後保持します。装備などでruntime親が変わるitem rootには管理Component証拠と`runtimeRelocatable: true`を宣言できます。保存path消失時は一意な証拠でのみ再解決し、移動中のplan/applyはmutation前に停止します。planのrelocate理由にも選択したpolicyが表示されます。key変更時は`migrateFrom`でworld objectを作り直さずstateを移行できます。fieldから `$slot:key`、`$component:key`、`$member:key.MemberName`、`$asset:key` を参照でき、forward referenceも利用できます。旧 `$ref:key` も互換です。vector、quaternion、colorはJSON array/objectがcanonicalで、従来のcomma stringも互換入力として受理されます。

`inspect`、`slot`、`component`、`item audit`でもstable selectorを使用できます。例: `resoloop component inspect '$component:controller' --state .resoloop/state/item.json --json`。raw IDは接続単位、stable selectorはstateのpath、型、identity、reference topologyから現在のIDへ再解決されます。

include、parameter/variable、prototype/instance、repeat、asset、camera、assertionの仕様は[docs/DECLARATIVE.md](docs/DECLARATIVE.md)にまとめています。house fixtureは3ファイルへ分割し、22個のboxと4本のtable legをprototype化しました。展開結果69 Slot・148 Componentを維持したまま、宣言量は46,178 byteから42,430 byteへ8.1%減っています。

`validate` は接続なしのschema・値形状・key・参照検査、`validate --strict` は接続先のruntime Reflectionを使ったComponent/member検査です。`plan` はworldを変更せずcreate/update/no-opを列挙します。既存rootを初めて管理対象へ取り込む場合、inspectとplanで完全一致対象を確認してから一度だけ `--adopt` を付けます。stateがある通常の再適用では不要です。

~~~powershell
resoloop plan content/main.json --json
resoloop diff content/main.json --changes-only --json
resoloop diff content/main.json --deletes-only --json
resoloop apply content/main.json --profile --json
resoloop diff content/main.json --json
resoloop scene summary content/main.json --output artifacts/scene.json --json
resoloop capture content/main.json --camera main --output artifacts/main.svg --json
resoloop capture content/main.json --camera main --output artifacts/main.jpg --json
resoloop test content/main.json --json
~~~

applyの最終JSONはstdout、進捗はstderrへ分離されます。plan/diffのJSONは常に全変更を `changes` 配列へ分離し、`--changes-only` / `--creates-only` / `--deletes-only` / `--summary` は `operations` の表示量だけを絞ります。`--prune --yes`はstaleな親Slot配下を1回の親削除へ集約します。機械処理できる進捗が必要なら `--ndjson-progress`、表示を抑えるなら `--quiet` を使います。`--timeout` は各ResoniteLink request、`--command-timeout` はcommand全体のdeadlineです。Ctrl+Cやdeadlineで中断した場合はstate fileと完了件数が報告され、同じapplyで再開できます。

## Flux-SDK

~~~powershell
dotnet tool install --global Papaltine.FluxSDK --version 1.9.0
$env:RESONITE_MANAGED_DATA_PATH="D:\Users\star_\AppData\Local\RESO Launcher\profiles\profile1\Game"

resoloop flux check examples/flux/ResoLoopHello.pg --project examples/flux --json
resoloop flux node search DynamicImpulse --json
resoloop flux node describe DynamicImpulseTrigger --json
resoloop flux build examples/flux/ResoLoopHello.pg --project examples/flux --json
resoloop flux deploy --project examples/flux --module ResoLoopHello --parent ResoLoop_Test --json
resoloop flux validate-manifest examples/flux/resoloop.flux.json --json
resoloop flux deploy-manifest examples/flux/resoloop.flux.json --json
resoloop flux watch examples/flux/resoloop.flux.json --json
~~~

`.pg`に対するbuild/check/watchは既存Flux-SDK CLIをラップします。`flux node search/describe`はFlux-SDKの通常metadata出力を、同名nodeをfull identity別に保持したversioned catalogへcacheします（1.9.0の`froox-docs --json`は同名keyで失敗するため使用しません）。`flux validate-manifest`は接続せずにJSON shape、source、dependency、module portとbindingの一対一対応を検査します。JSON module manifestに対するwatchは依存順に成功buildだけを再deployします。`$slot:key` parentはworld stateから現在session向けに検証・再解決されます。moduleの `bindings` ではFlux input名を `mode: "source"`、output名を `mode: "drive"` として `$slot:key` / `$component:key` / `$member:key.MemberName` へ接続できます。driveはmember targetだけを受け付けます。明示したCLI `--state` はcurrent directory基準、manifest内の`worldState`、`source`、`deployState`はmanifest基準です。Fluxの`int`/`int32`/`System.Int32`、`bool`/`System.Boolean`などのscalar aliasは同じ型として照合します。IDはworld stateのcomponent index、管理member、`identityFields`を使って再解決されます。source signatureの未結線port、方向・型不一致、Flux-SDK 1.9.xのinterface global inputはbuild/deploy前に、build成功後の0 nodeはdeploy前に構造化エラーになります。deployはFlux-SDK 1.9のLoader.replaceを利用し、deploy先の`parentSlotId`と、再観測した実module childの`moduleSlotIdBefore` / `moduleSlotIdAfter`、解決済みbinding、checkpoint recoveryを返します。`doctor`は最小check/build probeでmanaged-dataの明示pathまたは自動発見が実際に成功するか確認します。

弾、標的、エフェクトなど多数の同型オブジェクトは、各templateへ完全なFlux graphを複製せず、template側を不可避なDriverやadapterだけに留めます。instance stateは`Projectile/Active`のような型付き・名前空間付きDynamicVariableへ置き、上限付きpoolを1つのcontroller moduleから走査、確保、更新、完全reset、再利用します。これによりpool数を増やしてもProtoFlux module数とdeploy costが比例増加しません。[PooledProjectiles.pg](examples/flux/PooledProjectiles.pg)と[manifest](examples/flux/pooled-projectiles.flux.json)が最小例です。

保存・配布するGrabbableは、保存前に参照閉包を監査できます。

`AudioClipPlayer.playback`などのSyncPlayback memberは型情報を保持するため、`in Rain: SyncPlayback element`へ`$member:player.playback`で接続できます。PlaybackノードがIPlayableを要求する場合は、Reflectionで実装を確認したうえで`ObjectCast<SyncPlayback,IPlayable>`を使います。

~~~powershell
resoloop item audit Root/ResoLoop_Test_Teleporter --strict --json
resoloop tool audit '$slot:tool-root' --state .resoloop/state/tool.json --depth 16 --json
~~~

`item audit`はroot以下のSlot、Component、member IDを閉包として収集し、外部参照をrequired world element、Flux external、runtime context、明示許可へ分類します。保存後に切れる参照は`ITEM_NOT_PORTABLE`、runtime contextはwarningです。意図した依存だけを`--allow-external`で許可し、Flux moduleとbinding targetもGrabbable root内へ置いてください。

engine既定shader/font/materialのようにsession IDが変わる意図的な外部依存は、監査結果を確認したうえで`--allow-external-role TextRenderer:Font`のような正確な`Component:MemberPath`で許可できます。これはengine既定を型だけで推測せず、レビュー済みの参照roleをstableに許可する仕組みです。

`tool audit`はRawDataTool.TipReference、左右GripPoseReference.HandSide、および各poseのlocal Z+とtip方向の内積を検査します。geometryが合格してもPrimary actionのruntime smoke checkは別途必要です。

## Codex Skills

skills/codexには次のworkflow Skillがあります。

- resonite-build: 観測から編集・Reflection・検証・修正までの統合ループ
- resonite-debug: read-first診断
- resonite-inspect: コンテキストを浪費しない観測
- resonite-flux: ProtoGraph check/build/deploy

`resoloop init` はResoniteコンテンツproject限定のskillsとして、対象projectの `.agents/skills/` へこれらを自動的にインストールします。

~~~powershell
resoloop init .
resoloop skills sync --check
# 更新が必要で、利用者編集との競合がないことを確認後
resoloop skills sync --update
~~~

`resoloop init`はskill内容と配布hashを`.agents/skills/.resoloop-bundled.json`へ記録します。`skills sync --check`はread-onlyで差分を検査し、`--update`は現在内容が前回配布hashと一致する未編集skillだけを更新します。利用者編集またはlockのない未知内容は`SKILL_SYNC_CONFLICT`で全更新前に停止します。Codexはcurrent directoryからrepository rootまでの `.agents/skills/` を読み込むため、このskillsは対象project内でだけ利用されます。CLI commandはprimitive、Skillはworkflowです。

## Tests

~~~powershell
dotnet test tests/RLoop.Tests/RLoop.Tests.csproj

$env:RESOLOOP_RUN_INTEGRATION="1"
$env:RESONITE_LINK_URL="ws://localhost:12449"
dotnet test tests/RLoop.IntegrationTests/RLoop.IntegrationTests.csproj --filter Category=Integration
~~~

Integration testは一意なResoLoop_Test_Integration_* Slotだけを作り、finallyでcleanupします。既存ユーザーコンテンツは操作しません。

## Logs and troubleshooting

resoloop logsはResonite内部の非公開APIへ依存せず、設定されたファイルまたはdirectoryの最新.logをtailします。

~~~powershell
$env:RESONITE_LOG_PATH="C:\path\to\Resonite\Logs"
resoloop logs --tail 200 --json
~~~

- CONNECTION_FAILED: ResoniteLinkがworldで有効か、画面上のportとURLが同じか確認
- COMPONENT_TYPE_NOT_FOUND: type searchの完全な結果を使う
- open generic: `resoloop type specialize '[FrooxEngine]FrooxEngine.DynamicValueVariable<>' string` でclosed genericを生成する
- COMPONENT_MEMBER_NOT_FOUND: type describeでflattened memberを確認
- VALUE_CONVERSION_FAILED: vector/quaternion/colorはJSON array/objectを優先し、エラーのtarget typeと受理例を確認
- FLUX_SDK_NOT_FOUND: flux-sdkをglobal toolとして導入、またはRESOLOOP_FLUX_EXECUTABLEを設定
- Flux type error: RESONITE_MANAGED_DATA_PATHまたは --library-path を確認

## In-game screenshots

`capture FILE.json --camera BOOKMARK --output capture.jpg` はブックマークのworld座標・注視点・縦画角と解像度を使って撮影します。manifestを自動applyする操作ではないため、必要なコンテンツを先にapplyしてください。Reflectionで `InteractiveCamera.Capture()` を確認し、専用の `ResoLoop_Test_Capture_*` Slotにカメラを生成します。撮影後は失敗時もそのSlotだけを削除します。既存のカメラは変更しません。

~~~powershell
resoloop capture content/main.json --camera main --output artifacts/main.jpg --url ws://localhost:<current-port> --json
resoloop capture content/main.json --camera main --output artifacts/main.png --screenshots-dir 'C:\Users\YOUR_NAME\Pictures\Resonite' --width 1280 --height 720 --capture-timeout 60 --json
~~~

標準の読み取り先は、Resonite本体と同じWindowsのPictures既知フォルダ配下の `Resonite` です。OneDriveリダイレクトがプロセス間で食い違う場合は、OneDrive直下のローカライズされたPictures候補も調べ、既存写真が最も新しい `Resonite` フォルダを自動選択します。保存先が別PCや共有先にある場合だけ、その写真フォルダをローカルから読み取れる `--screenshots-dir` で指定してください。新規フォルダはResoniteの初回書き出しを待ちます。元の写真は残し、完成した画像だけを `--output` へコピーします。PNGがJPEGに変換される設定では `.jpg` を使うか、ResoniteのKeep Original Screenshot Formatを有効にしてください。形式・解像度が違う場合はエラーになります。

OneDriveなどで保存先が異なる環境は、環境変数 `RESOLOOP_SCREENSHOTS_DIR` または `.resoloop.json` / `%USERPROFILE%\.resoloop\config.json` の `screenshotsDirectory` に保存先を設定できます。優先順位はCLI → 環境変数 → project設定 → user設定です。[examples/capture.json](examples/capture.json)は原点付近を撮る最小例です。

同じ写真フォルダを使うresoloopの同時撮影は拒否します。撮影中は他のカメラや手動の写真撮影を避けてください。公開APIには撮影要求と保存ファイルを対応付けるIDがないため、撮影前後の新規ファイル差分で検出し、複数候補があれば `CAPTURE_AMBIGUOUS` を返します。画像が届かない場合は `CAPTURE_EXPORT_TIMEOUT`（exit 8）となります。対象ワールドが表示中か、レンダラーが動作しているか、保存先が正しいか確認してください。`--capture-timeout` は書き出し待ち時間、`--command-timeout` は接続・準備を含めた全体の上限です。

成功時は `screenshotAvailable: true`、`format: jpeg` または `png` を返します。併記する `.scene.json` はmanifest上の要約です。オフラインの比較には従来の `--output capture.svg` を使えます。

撮影の実機テストは `RESOLOOP_RUN_INTEGRATION=1` と `RESOLOOP_RUN_CAPTURE_INTEGRATION=1` の両方を設定して実行します。`RESONITE_LINK_URL` と必要に応じて `RESOLOOP_SCREENSHOTS_DIR` を指定し、`dotnet test tests/RLoop.IntegrationTests --filter FullyQualifiedName~CaptureIntegrationTests` を実行してください。専用Slotは削除されますが、ゲームが保存した写真は写真フォルダに残ります。

## Known limitations

- ResoniteLink 0.13.1自体がBetaで、breaking changeの可能性があります。
- applyはschema v1のJSONのみです。operationは非atomicでrollbackはできませんが、操作単位のcheckpointと再実行手順を返します。
- List更新は公開API上whole-member replacementです。`diff`は要素added/removedを表示してから一括更新します。SyncObject要素は子memberを含む構造値へ正規化して比較します。
- `capture --output capture.jpg` / `.png` は専用のInteractiveCameraでゲーム内画像を撮影し、ローカルの写真書き出しを読み取ります。Resoniteのレンダラーと写真保存先へのアクセスが必要です。`.svg` は引き続きオフライン投影で、`screenshotAvailable: false` です。
- logsはLink protocolからのstreamではなく、明示されたローカルlog fileのtailです。
- runtime probeは `safe: true` と `--probe --yes` の二重許可が必要です。public Reflectionに公開されたSyncMethodを呼ぶ `method` probeに加え、fieldを一時変更してafter assertionをpollし、`finally`で元の値へ戻して復元確認する `set-member` probeを利用できます。公開されないinteractionはstructural-onlyで、test結果の`verification`は`partial`になります。`partial`を実機interaction確認済みとして扱わないでください。
- ResoniteLink 0.13.1はUIXの`SyncDelegate` memberをComponent definition/update modelへ公開せず、Dynamic Impulse helperと`CallInput.Trigger`も呼び出し可能なSyncMethodとして公開しません。resoloopはraw messageやmember名を推測せず、これらのinteractionをstructural-onlyとして報告します。
- Flux-SDK 1.9.xのinterface型global input（実機で確認した `IButton global` など）はdeploy前に`FLUX_INTERFACE_GLOBAL_UNSUPPORTED`で拒否します。concrete Componentの`element` inputからmodule内でglobal化するか、Dynamic Impulse bridgeを使用してください。`Slot element` inputの配線は正常動作を確認しています。

## License and upstream notes

ResoniteLinkはMIT、Flux-SDK programmatic integrationはAGPL-3.0-or-laterです。この構成はFlux-SDKへリンクするため、resoloopの配布条件もAGPL-3.0-or-laterとしています。公開インターフェースを中心に実装し、非公開Resoniteソースやデコンパイル結果を同梱していません。
