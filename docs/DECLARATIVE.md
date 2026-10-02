# resoloop schema v1 authoring

組込UIXレシピは `children` 内で `{"$recipe":"button","$with":{"key":"accept","rect":{}}}` と直接使えます。引数契約は既存レシピと共通、生成キーは `uix-<recipe>--<key>` を接頭辞にします。引数の不足・余分をまとめて拒否し、visual指定は追加しません。子ノードは各instance/repeatのscope確定後に一度だけ展開します。既存 `$prototype`/includeは従来のキーを保持します。組込構文への変更はキー移行になるため、適用済みmanifestを機械的に書き換えないでください。[接続例](../skills/codex/resonite-uix/references/recipes.md)を参照。

`observe '$member:KEY.FIELD' [...] --state STATE --json` は1〜64個の明示フィールドをread-onlyで取得します。重複selectorを除き、同じComponentは呼出し内で一度だけ読みます。戻り値は `count`, `components`, selectorをキーとする`values`。各値にComponent ID/typeと既存のMemberValue（kind/id/type/value/targetId等）を保持します。欠落memberは部分成功にせず失敗。通常のstable参照解決による再接続時の曖昧性拒否を維持します。Component間の読取はatomicではなく、64指定の上限はlist/nested member内のサイズを制限しません。

新規宣言は `resoloop manifest scaffold --output NEW_FILE.json --key KEY` で生成できます。`schema list` / `schema describe TOPIC --json` はCLIの型定義に連動した部分契約と最小例を返します。原型/include等のソース展開とruntime ComponentのReflectionは別です。cameraのposition/targetは3数値、schemaVersionは文字列`"1"`、camerasはbookmark名をキーにしたobjectです。[生成・識別診断・自動撮影](AUTHORING-ASSISTANCE.md)も参照してください。

`resoloop`は複数のJSON sourceを展開してから、schema v1として一括検証します。展開は接続なしで行われ、循環include、未解決parameter、stable key衝突、既定10,000 JSON node／10 MiB／64 source fileの上限違反をmutation前に拒否します。JSON node数にはfieldやvalueも含まれ、Slot数ではありません。include先も同じ予算へ合算します。大きな単一ownershipにはrootで `"limits": { "expandedNodes": 20000 }` を指定できます（整数1〜250000）。byte数・file数の上限は変わりません。compilation結果には展開node数・設定上限・byte数を返します。上限を増やしてもruntime側の処理時間や快適な規模を保証しません。

## Include、parameter、prototype、repeat

UIXの構造prototypeは `resoloop uix recipe list` / `describe NAME` / `export NAME --output NEW_FILE.json` で取得できます。通常のincludeと同じ予算・key衝突検査・所有stateを使い、新しいruntime言語は追加しません。button、boolean-state、scroll-content、value-state、text-input、toggle、choice、sliderの接続口とcaller側の見た目の構成は[レシピreference](../skills/codex/resonite-uix/references/recipes.md)を参照してください。init/skills syncでもJSON資産を配布し、利用者編集を保護します。

~~~json
{
  "include": ["materials.json"],
  "schemaVersion": "1",
  "ownership": { "key": "my-world" },
  "parameters": { "spacing": 1.5 },
  "variables": { "rootName": "ResoLoop_Test_MyWorld" },
  "prototypes": {
    "box": {
      "slot": { "key": "box-${i}", "name": "Box ${i}", "position": [0, 0, 0] },
      "components": []
    }
  },
  "slot": { "key": "root", "name": "${rootName}", "parent": "Root" },
  "children": [
    {
      "$prototype": "box",
      "$with": {},
      "$repeat": { "count": 4, "as": "i", "offset": ["${spacing}", 0, 0] }
    }
  ]
}
~~~

`${name}` が値全体ならnumber、array、objectの型を保ったまま置換し、文字列内なら文字列へ埋め込みます。`$instance` は `$prototype` の別名です。`$with` はinstance固有値、repeatの `as` は0始まりindex、`offset` は各instanceのpositionへ加算する3要素vectorです。展開後のSlot/Component keyは全体で一意でなければなりません。

includeは記述順に読み込まれ、`children`、`components`、`tests`を連結し、objectをmergeします。後段のroot documentはscalarを上書きします。include pathは宣言元JSONからの相対pathです。

## Stable reference

- `$slot:key`: Slot ID
- `$component:key`: Component ID（旧 `$ref:key` も互換）
- `$member:key.Member`: Component member ID
- `$slot-member:key.Rotation`: 宣言したSlotの公開field ID（Position / Rotation / Scale / Name / Tag / Parent / IsActive / IsPersistent / OrderOffset）
- `$asset:key`: import済みasset URLまたは宣言したURI

forward referenceを利用できます。全参照が宣言され、strict modeではComponent/memberがruntime Reflectionに存在すると確認されてからmutationを始めます。closed generic Component typeも文字列を変形せずReflectionへ渡します。

Slot fieldはComponent fieldと区別して`$slot-member`を使います。例: Reflectionで確認したSpinnerの`"_target": "$slot-member:crystal.Rotation"`。実field IDは現在のSlotから取得し、再接続時にはstateのSlot pathを再解決します。未知のSlot key/公開field名はmutation前に拒否します。駆動されるrotationは`managedFields`から外すか固定配置Slotの子に分け、再applyで動作を巻き戻さないようにします。これは参照値としての構文であり、Component用set-member probeには使いません。Flux manifestのbindingは現時点では従来のComponent member構文のみ対応します。

vector、quaternion、color/colorXなどの構造値はJSON arrayまたは`x/y/z/w`・`r/g/b/a` objectをcanonical入力とします。従来のcomma stringも互換入力として受理し、runtimeのobject表現と同じ値なら2回目applyで差分を出しません。不正な要素数や数値はmutation前にtarget typeと受理例付きで拒否されます。

Componentの`fields`はapplyごとに収束させます。runtimeが更新するcounter、history、選択状態などは`initialFields`へ置くと、新規作成時だけ初期化され、adopt・再applyでは上書きされません。ただし、list memberの宣言要素数が実機より少ないためにComponentを作り直すapply（planの`recreate`）では、新しいComponentへ`initialFields`を設定し直すため値は初期値に戻り、宣言していないmemberも型の既定値に戻ります。同型Componentが複数あり再接続時の識別が必要なら、不変な管理値のmember名を`identityFields`へ指定してください。identity fieldは`fields`または`initialFields`にも存在する必要があります。stateはtype ordinalだけでなく、Slot内index、管理member集合、identity値を保存し、複数候補が残る場合は`STABLE_COMPONENT_AMBIGUOUS`として停止します。applyがComponentを削除・移動した後は、同じSlotに残るkeyのindexも実機の並びに合わせて保存し直します。

CLI の `validate --strict`、`plan`、`apply`、`test` は、ownership の作業ルートへ `FrooxEngine.AI_GeneratedContent` を自動追加し、`Source` を実行中の resoloop の名前とバージョン（例: `[resoloop 0.1.0-preview.5]`）へ収束させます。子 Slot のうち `runtimeRelocatable: true` のルート、または `Grabbable`、`RawDataTool`、`AvatarRoot`、`ObjectRoot` を持つルートにも同じ Component を追加します。この自動 Component は plan と state に含まれ、2回目の apply では書き込みません。入力 JSON 自体は書き換えません。低水準の `slot create` では、そのコマンドで作った Slot 自体を生成オブジェクトのルートとして同様にタグ付けします。

## Transform管理とstable key migration

既存Slotの配置を宣言へ取り込むときは、resoloopが管理するtransformを明示的に狭められます。

~~~json
{
  "slot": {
    "key": "panel-v2",
    "migrateFrom": "panel",
    "name": "Panel",
    "position": [0, 1, 2],
    "scale": [1, 1, 1],
    "managedFields": ["scale"]
  },
  "components": [{
    "key": "grabbable-v2",
    "migrateFrom": "grabbable",
    "type": "FrooxEngine.Grabbable"
  }]
}
~~~

`managedFields`に指定できるのは`position`、`rotation`、`scale`です。省略時は、宣言されたtransformをすべて管理します。既存Slotで`preserveWorldTransform: true`を指定すると、この3つのlocal値を更新しません。`preserveWorldTransform`と`managedFields`を併記した場合は保持を優先します。Slot名はどちらの設定にも関係なく管理されます。stable keyを保った親変更はplanで`relocate`となり、ResoniteLinkのParent更新でSlot IDを維持します。`relocationTransform`は`local`（既定）または`world`です。`local`は現在のlocal値／通常のmanaged fieldを新親でも使います。`world`は旧Slotと新親のRootからのtransform chainを観測し、world matrixを維持するlocal position/rotation/scaleへ再計算します。`world`指定の既存transformは以後管理対象外になり、2回目applyで宣言localへ戻りません。新規Slotにはどちらのpolicyでも宣言値を初期値として適用します。

装備などによりruntime中だけ宣言親の外へ移動するitem rootには`runtimeRelocatable: true`を指定できます。同じSlot上に、再接続後の照合証拠となる管理Componentを最低1つ宣言してください。保存pathが見つからない場合、resoloopはRoot以下を最大depth 64で探索し、Slot名と管理Componentの型・member集合・`identityFields`が一意に一致した場合だけstable selectorを再解決します。候補0件または複数件では推測しません。itemが宣言親の外にある間の`plan`/`apply`は`APPLY_RUNTIME_RELOCATABLE_ACTIVE`でmutation前に停止するため、装備中のitemを複製したり強制的に元へ戻したりしません。dropして宣言親へ戻してからapplyしてください。

Slot / Componentの明示keyを変更する場合は、新key側へ`migrateFrom`で旧keyを1つ指定できます。stateだけを移行するため、対応するworld objectを削除・再作成しません。旧keyと新keyの両方がstateにある場合、旧keyを同じ宣言内に残した場合、移行元を複数箇所で使った場合は曖昧な移行としてvalidationまたはplanで拒否します。移行を適用してcheckpointされた後は`migrateFrom`を削除できます。

## Asset

~~~json
{
  "assets": {
    "albedo": { "kind": "texture", "source": "assets/albedo.png" },
    "mesh": { "kind": "mesh", "source": "assets/mesh.resonitelink.json" },
    "material": { "kind": "material", "source": "resdb:///..." }
  }
}
~~~

local `texture`、`audio`、ResoniteLink `ImportMeshJSON`は公開import APIを使います。source hashと返されたURLをownership stateへcheckpointし、内容が変わった場合だけ再importします。`resdb:`などのabsolute URIはそのまま参照できます。materialはworld内Componentとして宣言するか、既存asset URIを使います。

Blenderからは `resoloop blender export FILE.blend --output NEW_DIRECTORY --name NAME --parent VERIFIED_PARENT` でmesh・画像・material接続を含むapply bundleを生成できます。均一な静的mesh JSONはアダプターで公式binary APIへ変換して送信し、UV channelを保持します。画像パスはResoniteホストから読める必要があります。[Blender制作](BLENDER.md)を参照してください。

## Camera、scene artifact、test

~~~json
{
  "cameras": {
    "main": {
      "position": [0, 3, -8], "target": [0, 1, 0], "fieldOfView": 60,
      "width": 1280, "height": 720, "representative": true
    }
  },
  "tests": [{
    "name": "toggle wiring",
    "assertions": [
      { "target": "$component:toggle.TargetValue", "expected": "$member:renderer.Enabled" },
      { "target": "$component:renderer.Enabled", "expected": false, "phase": "after" }
    ],
    "probe": { "target": "$component:worker", "method": "Run", "arguments": { "count": 1 }, "safe": true },
    "timeoutMs": 2000, "pollMs": 100
  }]
}
~~~

~~~powershell
resoloop scene summary content/main.json --output artifacts/scene.json --json
resoloop capture content/main.json --camera main --output artifacts/main.svg --json
resoloop capture content/main.json --camera main --output artifacts/main.jpg --json
resoloop test content/main.json --json
resoloop test content/main.json --probe --yes --json
~~~

`capture` の `.jpg` / `.png` 出力は明示cameraのworld座標・注視点・縦画角から専用InteractiveCameraを作り、公開Captureメソッドで撮影します。Resonite本体と同じPictures/Resonite（既存のOneDriveリダイレクト先も自動検出）から完成した新規画像を読み取り、`screenshotAvailable: true` を返します。別の保存先は `--screenshots-dir DIR`、待ち時間は `--capture-timeout 60` で指定できます。元の写真は残し、専用Slotはfinallyで削除します。manifestは自動applyしません。撮影中は他の写真撮影を避けてください。PNGがゲーム側でJPEGに変換される場合は `.jpg` を指定するか、Keep Original Screenshot Formatを有効にします。詳細はREADMEのIn-game screenshotsを参照してください。

`.svg` 出力は従来の決定的なオフライン投影で、`screenshotAvailable: false` です。CIではSVGとscene JSONを比較できます。`scene summary`と撮影のscene JSONは宣言上のworld bounds、配置、material欠落、無効参照を報告します。

通常の`test`はField/Reference構造だけを検証します。probeはmanifestで `safe: true`、CLIで `--probe --yes` の両方が必要です。公開Reflectionにmethodがなければ呼び出さず、`structuralOnly: true`と未評価のafter assertionを明示します。methodが利用可能ならpublic SyncMethod APIで呼び、after assertionをtimeoutまでpollします。

公開methodを使わず、fieldの一時状態だけを検証する場合はtransactional probeを使えます。

~~~json
{
  "probe": {
    "kind": "set-member",
    "target": "$component:button.Enabled",
    "value": false,
    "restore": true,
    "safe": true
  }
}
~~~

`set-member`はfieldだけを対象とし、変更中にafter assertionをpollした後、成功・失敗・cancelのいずれでも元の値を復元して再読取確認します。`restore: false` は拒否されます。従来のmethod probeは `kind` 省略時の既定値です。

幅と長文など複数条件を一緒に試す場合は `set-members` を使います。

```json
{
  "probe": {
    "kind": "set-members", "safe": true, "restore": true,
    "values": {
      "$member:canvas.Size": [320, 240],
      "$member:label.Content": "A longer paragraph for the narrow layout."
    }
  },
  "assertions": [{ "target": "$member:canvas.Size", "expected": [320, 240], "phase": "after" }]
}
```

`values`は1〜32個のComponent fieldを指定し、全対象の存在・型変換・重複を事前検証します。値の退避後に順番に書き込み、失敗・cancel時も書き込みを試みたfieldを逆順に復元し再読取します。一つの復元失敗で残りを中断せず、復元不能なselectorを `PROBE_RESTORE_FAILED` の `targets` に返します。atomicなtransactionではなく、他ユーザーが同時編集するfieldで実行しないでください。通常と同じ `test --probe --yes` が必要です。これはfield検証であり、文字折返し・スクロール末尾・実クリックは別途captureや実操作で確認します。[UIXサンプル](../examples/uix-responsive.json)を参照。

assertionはmember値に加え、`$component:key`の存在と`kind: "child-count"`を扱えます。child-countには`name`、`componentType`、固定`count`、またはprobe前からの`delta`を指定できます。`assertions`欠落は`APPLY_TEST_ASSERTIONS_MISSING`です。未知propertyは黙って無視せず、たとえば`argumnts`には`arguments`をsuggestします。

## Diff、rename、prune、recovery

~~~powershell
resoloop diff content/main.json --json
resoloop diff content/main.json --changes-only --json
resoloop diff content/main.json --deletes-only --json
resoloop apply content/main.json --prune --yes --json
~~~

`diff`はcreate/update/rename/recreate/delete/no-op、理由、list要素のadded/removedを返し、worldを変更しません。JSONの `changes` にはno-op以外が常に入り、`--changes-only` / `--creates-only` / `--deletes-only` / `--summary` は `operations` の表示だけを絞ります。SyncObject listは子memberを構造値へ正規化して比較します。renameはstable keyで同一Slotを追跡してnameを更新します。delete候補はstateに記録されたownership root内の対象だけです。ワールドの再読込後にapplyが照合したstaleなSlot / ComponentのIDは、そのapplyのcheckpointでstateへ保存します（plan・diffはstateを書きません）。そのため、同じセッションで続けて実行する`--deletes-only`や`--prune`でも対象から外れません。通常applyは削除しません。`--prune --yes`では、staleな親Slotがある場合は配下のSlot / Componentを個別削除せず、最上位のstale親Slotを1回削除して対応するstateをまとめてcheckpointします。親に含まれないstale Componentだけは個別に削除します。

ResoniteLinkのoperationはtransactionではありません。失敗時のcontextは `atomic: false` とcheckpoint pathを含みます。途中失敗後はエラー固有の `recovery` とsuggestionsを確認してください。`APPLY_LIST_SHRINK_NOT_CONVERGED` では、原因を変えない再実行で再び失敗する場合と、中断した置換を取り消したあと同じ宣言で再実行すると収束する場合があります。

checkpoint schema 2はSlotのexact name segmentsを保存します。`resoloop slot inspect 'path:["Root","A/B"," Label "]' --json`のように指定すると名前内の区切り文字や前後空白を保持できます。通常のslash pathは階層の区切りとして解釈します。schema 1のstateは読込可能で次の保存時に移行しますが、失われた曖昧な名前情報は復元しません。宣言のschemaVersionは`"1"`のままです。同名兄弟はkeyが異なってもvalidationで拒否します。

同じ表示名でも新keyは新roleとして作成し、旧roleのprune対象とは別IDにします。live/stale keyの同一Slot所有は`APPLY_SLOT_OWNERSHIP_CONFLICT`で変更前に停止します。Component/Slotのライフサイクル変更後はsymbolic referenceを読み戻して1回修復し、なお保持されない場合は`APPLY_REFERENCE_NOT_RETAINED`を返します。旧driverがtargetを占有する場合は明示pruneの計画を確認してください。自動でpruneを有効化することはありません。

ResoniteLinkはlistの要素を削除できません（短いlistを送っても先頭の要素が置き換わるだけです）。`fields`で宣言したlist memberの要素数が実機より少ない既存Componentは、planで`recreate`となります。applyは同じSlotに新しいComponentを作って宣言したfieldを書き、listを読み直して確かめてから、管理Componentの参照を張り替え、旧Componentを削除します。Componentの移動と同じく、この削除に`--prune`は不要です。新しいComponentでは、`initialFields`は宣言の初期値に、宣言していないmemberは型の既定値に戻ります。stateに記録の無いComponent（adoptの初回のapplyで取り込むもの）は作り直さず、その場で更新します。保存したIDで確かめられないComponent（作成が中断したkeyに型と位置で照合したもの）も作り直さず、`STABLE_COMPONENT_AMBIGUOUS`で停止します。次の場合は停止します。

- `APPLY_LIST_SHRINK_REFERENCED`（変更前）: 旧Componentやそのmemberを、管理Componentが同じ位置に参照を宣言していないもの（syncObjectの一部だけを宣言した場合の、宣言していない子を含む）が参照しています。調べるのはapplyが読んだ階層（`slot.parent`以下、applyが読み取る深さまで）だけで、その外からの参照は旧Componentの削除で切れます。参照元を`$component:`などで管理対象として宣言するか、参照を外してください。
- `APPLY_LIST_SHRINK_NOT_CONVERGED`（検証時）: 新しいComponentでもlistが宣言より長いまま残りました。相互参照などで書き込みを保留したlistも、旧Componentを消す前に読み戻します。旧Componentが残り、参照を切らずに削除できる置換だけを取り消してstateを戻します。このapplyで作った置換だけでなく、前のapplyで作った再開中の置換も、安全に取り消せる場合は対象です。既に参照されている置換や、旧Componentが消えていて戻せない置換は削除せず、contextの`inProgress` とcheckpointに残します。runtimeがlistを補充した場合は宣言する要素数や補充原因を直してから再実行してください。参照のない中断中の置換をさらに短く宣言した場合は、取り消し後、同じ宣言で再実行すると旧Componentから作り直せます。エラー固有の `recovery` とsuggestionsに従ってください。
- `APPLY_RECREATE_INTERRUPTED`（変更前）: 作り直しが途中で止まり、自動では再開できません。`reason`が`session-changed`なら、ワールドの再読込などでIDが変わっています。エラーのsuggestionsと`stateFile`に従ってください（stateの編集が要ります）。同じSlotに同じ型のkeyがほかにある場合は自動では区別できないので、stateをそのまま残し、各候補の参照先を確かめてください。`key-not-declared`なら、同じセッションのうちにkeyを元のSlotへ戻して再実行すると完了します。keyが不要なら、その後keyを外して`--prune`で削除してください。`declaration-shrank`なら、新しいComponentが持つ要素数以上を宣言して同じセッションで再実行し、作り直しを終えてから宣言を短くしてください。`replacement-unverified`なら、`candidates`のうちlistが宣言と一致するものは、中断したapplyが作った新しいComponentである可能性が高いものです。宣言が同じapplyで後から作るComponentを参照している場合は、applyが初期値なしで作るので、listがすべて空のものもその可能性があります。ただし、listが空の、利用者が置いたComponentとは見分けられません。`resoloop component inspect ID`で確かめ、中断したapplyが作ったと確かめられたものだけを`resoloop component remove ID --yes`で削除し、再実行してください。当てはまる候補が無い場合は、エラーのsuggestionsに従ってください（stateの編集が要ります）。

同じcheckpointへのapplyはwriter lockで直列化し、競合は`APPLY_STATE_BUSY`としてworld変更前に停止します。readerは置換可能な完全snapshotを読みます。ロックファイルの存在だけでは実行中と判断せず、使用中のロックを削除して迂回しないでください。別stateが同じworld objectへ書く競合や、観測中のworld全体の一貫性を保証する仕組みではありません。

## Flux module manifest

[examples/flux/resoloop.flux.json](../examples/flux/resoloop.flux.json)を参照してください。moduleごとにsource、Flux module path、`dependsOn`を宣言します。依存cycleは事前に拒否され、topological orderで成功buildだけをdeployします。

~~~powershell
resoloop flux validate-manifest examples/flux/resoloop.flux.json --json
resoloop flux deploy-manifest examples/flux/resoloop.flux.json --json
resoloop flux watch examples/flux/resoloop.flux.json --json
~~~

`validate-manifest`はlive接続前にmanifest JSON、source、dependency、module signatureとbinding coverageを検証します。parentに `$slot:key` を使う場合は`worldState`または`--state`が必要です。明示したCLI `--state` はcurrent directory基準、manifest内の`worldState`、`source`、`deployState`はmanifest基準です。現在sessionなら保存IDを検証し、sessionが変わっていれば保存pathから再解決します。module input/outputは次のようにstable bindingとして宣言できます。

~~~json
{
  "name": "controller",
  "source": "Controller.pg",
  "module": "Controller",
  "bindings": {
    "TargetSlot": { "target": "$slot:root", "mode": "source" },
    "Enabled": { "target": "$member:renderer.Enabled", "mode": "drive" }
  }
}
~~~

`source`はFlux moduleの `in` 名、`drive`は `out` 名をkeyにします。source targetはslot/component/member、drive targetはmemberだけです。resoloopはworld stateから現在のIDを再解決し、Flux-SDKのInputMap/OutputMapへ渡します。binding宣言と解決結果が一致しない場合はdeployしません。deploy stateはsource、binding、transitive dependencyのhash、置換後module child IDを保存し、no-op/updateと非atomic recoveryを報告します。結果の`parentSlotId`はdeploy先、各moduleの`moduleSlotIdBefore` / `moduleSlotIdAfter`はparent直下を再観測した実module childです。接続が変わっても再観測したchildとhashが一致すればno-opになります。watchは変更を検出して成功buildだけを検証済みparentへ再deployします。

manifest deployはsource headerの`in`/`out` signatureとbindingを一対一で照合してからbuild/deployへ進み、方向、world target型、driveのmember可否を検査します。`int`/`int32`/`System.Int32`や`bool`/`System.Boolean`などのscalar aliasは同値です。build結果の`Packing 0 ProtoFlux nodes`は`FLUX_EMPTY_MODULE`、未結線portは`FLUX_MODULE_PORT_UNBOUND`です。Flux-SDK 1.9.xの`IButton global`のようなinterface globalは既知の非原子的失敗を避けるためdeploy前に拒否されます。concrete Componentの`element` inputからmodule内で`asDrivenGlobal`するか、eventだけならDynamic Impulse bridgeを選びます。

弾や標的のような同型instanceを多数使う場合は、各templateに完全なcontroller graphを複製せず、template-local FluxをDriverなどの不可避な処理へ限定します。instance stateは型を統一した名前空間付きDynamicVariableへ保存し、bounded poolの確保、更新、衝突、reset、再利用を単一controller moduleへ集約します。pool枯渇時の挙動を定義し、再利用前に全stateをresetしてください。

## Portable item audit

~~~powershell
resoloop item audit Root/MyItem --strict --json
~~~

RawDataToolとして使うitemはportable auditに加えて、装備前に次を実行します。

~~~powershell
resoloop tool audit '$slot:tool-root' --state .resoloop/state/tool.json --depth 16 --json
~~~

`tool audit`はRawDataTool.TipReferenceがitem root内を指すこと、左右のGripPoseReference.HandSideが存在すること、各GripPoseのlocal Z+がtip方向を向くこと（既定でdot 0.8以上）を検査します。これはgeometryのstructural checkなので、成功してもPrimary actionや実際の装備感は`structuralOnly: true`のままです。

Grabbableを保存する前に、root以下のSlot、Component、nested memberを参照閉包として検査します。root外の通常参照とFlux参照は保存後に切れるerror、Userなど再取得前提の参照はruntime-context warning、`--allow-external`で指定したIDまたはstable selectorは明示許可として分類されます。strict modeではwarningも不合格です。Grabbable、Flux module、runtime targetを同じ保存rootへ収めてから監査してください。

engine既定shader/font/materialのようにsessionごとにtarget IDが変わる外部参照を意図して許可する場合は、まず通常のaudit結果でComponent型とmember pathを確認し、`--allow-external-role PBS_Metallic:_shader`、`--allow-external-role TextRenderer:Font`、`--allow-external-role 'TextRenderer:Materials[0]'`のように正確なroleを指定します。role許可はtargetがengine既定であることを自動推測しません。そのmemberが外部依存でよいとレビューした、安定した宣言です。広い型単位では許可せず、監査結果に現れたmember pathだけを使ってください。
