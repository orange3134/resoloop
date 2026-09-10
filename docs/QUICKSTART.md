# resoloop クイックスタート

このガイドでは、独立したプロジェクトを作り、Resoniteへ最初のSlotとComponentを適用し、必要ならProtoFluxをデプロイするところまで進めます。PowerShellを前提にしています。

## 1. resoloopをインストールする

必要なものはWindows 10/11、.NET SDK 10、Resonite、対象worldで有効なResoniteLinkです。Preview版はNuGet global toolとして導入します。

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.5
resoloop help
~~~

更新時は `dotnet tool update --global ResoLoop --version 0.1.0-preview.5` を実行します。

## 2. 自分のプロジェクトを作る

~~~powershell
resoloop init MyResoniteProject
Set-Location MyResoniteProject
~~~

生成される構成は次のとおりです。

~~~text
MyResoniteProject/
├─ AGENTS.md              # AIエージェント向けの基本workflowと安全境界
├─ .resoloop.json          # project単位の非機密設定
├─ .resoloop/
│  └─ .gitignore        # apply checkpoint/stateをversion controlから除外
├─ .agents/
│  └─ skills/           # project限定のCodex workflow skills
├─ content/
│  └─ main.json         # SlotとComponentの宣言
└─ flux/
   ├─ Main.pg           # ProtoGraph source
   ├─ protograph.toml   # Flux-SDK project manifest
   ├─ resoloop.flux.json   # dependency orderとstable parent
   └─ .gitignore        # Flux生成物を除外
~~~

`resoloop init` は既存ファイルを上書きしません。同じ内容ならスキップし、内容が違うファイルがあれば `INIT_FILE_EXISTS` で、ほかのファイルを書き始める前に停止します。同梱skillの配布hashもlockへ記録されます。

## 3. ResoniteLinkへ接続する

対象worldでResoniteLinkを有効にし、画面に表示された現在のWebSocket portを使います。portはセッションごとに変わり得るため、値を推測したりリポジトリへ固定したりしません。

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:<current-port>"
resoloop doctor
~~~

`resonite-link-url` と `resonite-connection` が `pass` で、末尾が `ready` ならSlot/Component開発を開始できます。Flux SDK、managed data、log pathは任意機能なので、未設定でもcore開発は可能です。Flux-SDKが利用可能な場合、`resonite-managed-data`は最小check/build probeを実行し、明示pathの解決成功、未設定時の自動発見成功、解決失敗を区別します。

設定の優先順位は、CLI option、環境変数、カレントから親方向にある `.resoloop.json`、ユーザー設定の順です。現在portのようなセッション依存値には環境変数か `--url` を推奨します。

## 4. 最初のコンテンツを適用する

初期ファイルはschema v1、ownership、stable root keyを含み、`Root` 直下にプロジェクト名を含む `ResoLoop_Test_*` Slotを作って `FrooxEngine.Grabbable` を追加します。変更前にoffline validation、runtime validation、planの順で確認します。

~~~powershell
resoloop validate content/main.json --json
resoloop validate content/main.json --strict --json
resoloop plan content/main.json --json
$apply = resoloop apply content/main.json --json | ConvertFrom-Json
$slotId = $apply.data.slotId

resoloop inspect $slotId --members --json
~~~

`content/main.json` のposition、scale、Component fieldsなどを編集して、validateとplanを通してから同じapplyを再実行します。stateは `.resoloop/state/` にcheckpointされ、変更なしの対象にはworld書き込みを行いません。

適用時には作業ルートへ `AI_GeneratedContent` が自動で付き、`Source` に実行中の resoloop のバージョンが記録されます。`runtimeRelocatable` の子ルートや、`Grabbable`、`RawDataTool`、`AvatarRoot`、`ObjectRoot` を持つ子ルートも個別にタグ付けされます。

既存の手動配置を保つSlotには`preserveWorldTransform: true`、一部のtransformだけをresoloopに収束させる場合は`managedFields: ["scale"]`のように指定できます。親変更でworld位置を保つ場合は`relocationTransform: "world"`、local値を維持する場合は既定の`"local"`を使います。装備中にruntime親が変わるitem rootには、同じSlot上の管理Component証拠とともに`runtimeRelocatable: true`を指定すると再接続後も一意に再発見でき、移動中のapplyはmutation前に停止します。stable keyを変更するときは新keyへ`migrateFrom: "old-key"`を一時的に追加すると、world objectを作り直さずstateを移行できます。

~~~powershell
resoloop validate content/main.json --json
resoloop plan content/main.json --json
resoloop apply content/main.json --profile --json
resoloop diff content/main.json --changes-only --json
resoloop inspect $slotId --members --json
~~~

既存の同名rootを初めて管理対象へ取り込む場合、resoloopは `APPLY_OWNERSHIP_UNVERIFIED` で停止します。対象をinspectし、完全一致するrootだと確認した場合だけ、最初のplan/applyへ `--adopt` を付けてください。通常の再適用ではstateから再解決されるため不要です。

apply中の進捗はstderrへ出ます。stdoutの最終JSONと同じく機械処理する場合は `--ndjson-progress`、非表示にする場合は `--quiet` を使います。`--timeout` は個々のLink request、`--command-timeout` はcommand全体に適用されます。中断後は報告されたstate fileを保持して同じapplyを再実行すると、完了済み対象を再利用します。

新しいComponentを使う前には、実行中のResoniteから正確な型とmemberを取得します。

~~~powershell
resoloop type search Grabbable --json
resoloop type describe FrooxEngine.Grabbable --json
~~~

型名やmember名を推測せず、`type describe` の結果を `content/main.json` に反映してください。

宣言が大きくなったらinclude、parameter、prototype、repeatで分割します。参照は `$slot:key`、`$component:key`、`$member:key.Member`、`$asset:key` を使います。完全な構文と上限は[DECLARATIVE.md](DECLARATIVE.md)を参照してください。

cameraとtestsを宣言した場合は、apply後にCIで比較可能な成果物と構造assertionを生成できます。

~~~powershell
resoloop scene summary content/main.json --output artifacts/scene.json --json
resoloop capture content/main.json --camera main --output artifacts/main.svg --json
resoloop capture content/main.json --camera main --output artifacts/main.jpg --json
resoloop test content/main.json --json
~~~

`.jpg` / `.png` は専用InteractiveCameraで撮影したゲーム内画像です。標準の読み取り先はResonite本体と同じPictures/Resoniteで、OneDriveへリダイレクトされた既存の書き出し先も自動検出します。異なる場合は `--screenshots-dir DIR` を指定します。写真の書き出し中は他のカメラで撮影せず、PNGがゲーム側でJPEGに変換される場合は `.jpg` を使ってください。SVGは従来どおりオフラインの空間投影です。interaction probeはmanifestの `safe: true` と `--probe --yes` の両方があるときだけ呼ばれ、利用不能ならstructural-onlyと報告されます。

## 5. ProtoFluxを使う（任意）

resoloopのdeployerはFlux-SDK 1.9.0に固定されています。`doctor` が別versionを報告した場合は更新します。

~~~powershell
dotnet tool install --global Papaltine.FluxSDK --version 1.9.0
# 導入済みなら:
dotnet tool update --global Papaltine.FluxSDK --version 1.9.0

$env:RESONITE_MANAGED_DATA_PATH="C:\path\to\your\Resonite\managed-data"
resoloop doctor
~~~

次に、生成されたsourceを検査、ビルド、デプロイします。

~~~powershell
resoloop flux check flux/Main.pg --project flux --json
resoloop flux build flux/Main.pg --project flux --json
resoloop flux deploy --project flux --module Main --parent $slotId --json
resoloop flux validate-manifest flux/resoloop.flux.json --json
resoloop flux deploy-manifest flux/resoloop.flux.json --json
resoloop flux watch flux/resoloop.flux.json --json
resoloop inspect $slotId --depth 2 --members --json
~~~

`flux deploy` は指定parent配下の同名moduleだけを置換します。`flux validate-manifest`はResoniteへ接続せず、source、dependency、port、bindingを検証します。module manifestでは複数moduleと依存順を宣言でき、world apply stateの `$slot:key` をparentにできます。CLIの`--state`はcurrent directory基準、manifest内の`worldState`、`source`、`deployState`はmanifest基準です。manifest結果の`parentSlotId`はdeploy先、`moduleSlotIdBefore` / `moduleSlotIdAfter`は再観測した実module childです。watchは成功buildだけを再deployします。

同型の弾や標的を多数使う場合、各instanceへ完全なFluxを複製しません。template内は不可避なDriverだけにし、状態を名前空間付きDynamicVariableへ置き、上限付きpool全体を単一controller moduleから走査・reset・再利用します。

宣言から対象を取り除いた場合、まず`resoloop diff content/main.json --deletes-only --json`でownership内のdelete候補だけを確認します。通常applyは削除しません。意図した候補だけだと確認した場合に限り、`resoloop apply content/main.json --prune --yes --json`で収束させます。staleな親Slotは配下の管理対象を含む1回のSlot削除へ集約されます。処理は非atomicなので、失敗時は結果のcheckpoint pathを保持して同じapplyを再実行します。

## 6. AIエージェントと反復する

機械処理では `--json` を標準にすると、成功時は `data`、失敗時は `error.code`、`context`、`suggestions` を安定して利用できます。

`resoloop init` は同梱skillsをpersonal skillsではなく、Resoniteコンテンツprojectの `.agents/skills/` へ自動的にインストールします。

~~~powershell
resoloop init .
resoloop skills sync --check
~~~

Codexはcurrent directoryからrepository rootまでの `.agents/skills/` を読み込むため、これらのskillはこのproject内でだけ利用されます。`skills sync --check`で配布hashとの差分を読み取り専用確認でき、`skills sync --update`は前回hashと一致する未編集skillだけを更新します。利用者編集は`SKILL_SYNC_CONFLICT`として保護されます。`.agents/skills/` とlockをversion controlに含めれば、チームで同じworkflowを共有できます。Codexが変更を検出しない場合は再起動してください。

エージェントには、対象project directory、実現したい内容、変更してよい範囲を伝えます。安全な基本ループは次のとおりです。

1. `doctor` とboundedな `hierarchy` / `find` で現在状態を観測する。
2. `type search` / `type describe` でruntime APIを確認する。
3. checked-in JSONまたは `.pg` sourceを編集する。
4. JSONは `validate`、`validate --strict`、`plan` の順に検証する。
5. applyまたはFlux deployを実行する。
6. `inspect --members` で結果を再観測し、差があれば修正する。

## 7. 安全な後片付け

IDはResoniteLinkセッション内だけで有効です。セッションが変わったら、保存していたIDを再利用せず、名前とpathから再取得します。

~~~powershell
$matches = resoloop find --name ResoLoop_Test_MyResoniteProject --exact --json | ConvertFrom-Json
$matches.data
~~~

結果が1件で、削除対象が意図したテストSlotだと確認した場合だけ、そのIDを削除します。

~~~powershell
$exactId = $matches.data[0].id
resoloop inspect $exactId --json
resoloop slot delete $exactId --yes --json
~~~

`Root`、未確認ID、曖昧なpathに対して削除を実行しないでください。resoloopは`Root`の削除を拒否し、delete/removeには明示的な `--yes` が必要です。

## よくある問題

- `RESONITE_LINK_URL_MISSING`: 現在portを `RESONITE_LINK_URL` または `--url` に設定する。
- `CONNECTION_FAILED`: 対象worldでResoniteLinkが有効か、画面上のportが一致するか確認する。
- `COMPONENT_TYPE_NOT_FOUND`: `type search` の完全な型名を使う。
- `COMPONENT_MEMBER_NOT_FOUND`: `type describe` で継承memberを含む定義を再取得する。
- `APPLY_OWNERSHIP_UNVERIFIED`: 既存rootをinspectし、管理対象へ取り込む意図がある場合だけ `--adopt` を使う。
- `REQUEST_TIMEOUT` / `COMMAND_TIMEOUT`: stderrの進捗とstate fileを確認し、Resonite応答性を直してから同じapplyを再実行する。
- `FLUX_SDK_NOT_FOUND`: Flux-SDK 1.9.0を導入するか `RESOLOOP_FLUX_EXECUTABLE` を設定する。
- Fluxの型解決エラー: `RESONITE_MANAGED_DATA_PATH` または `--library-path` を実際のResonite managed DLL directoryへ向ける。
- 詳細が必要: `--verbose` を付け、必要に応じて `RESONITE_LOG_PATH` を設定して `resoloop logs --tail 200 --json` を使う。
