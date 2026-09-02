# resoloop schema v1 authoring

`resoloop`は複数のJSON sourceを展開してから、schema v1として一括検証します。展開は接続なしで行われ、循環include、未解決parameter、stable key衝突、10,000 node／10 MiB／64 source fileの上限違反をmutation前に拒否します。

## Include、parameter、prototype、repeat

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
- `$asset:key`: import済みasset URLまたは宣言したURI

forward referenceを利用できます。全参照が宣言され、strict modeではComponent/memberがruntime Reflectionに存在すると確認されてからmutationを始めます。closed generic Component typeも文字列を変形せずReflectionへ渡します。

vector、quaternion、color/colorXなどの構造値はJSON arrayまたは`x/y/z/w`・`r/g/b/a` objectをcanonical入力とします。従来のcomma stringも互換入力として受理し、runtimeのobject表現と同じ値なら2回目applyで差分を出しません。不正な要素数や数値はmutation前にtarget typeと受理例付きで拒否されます。

Componentの`fields`はapplyごとに収束させます。runtimeが更新するcounter、history、選択状態などは`initialFields`へ置くと、新規作成時だけ初期化され、adopt・再applyでは上書きされません。同型Componentが複数あり再接続時の識別が必要なら、不変な管理値のmember名を`identityFields`へ指定してください。identity fieldは`fields`または`initialFields`にも存在する必要があります。stateはtype ordinalだけでなく、Slot内index、管理member集合、identity値を保存し、複数候補が残る場合は`STABLE_COMPONENT_AMBIGUOUS`として停止します。

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
resoloop test content/main.json --json
resoloop test content/main.json --probe --yes --json
~~~

ResoniteLink 0.13.1にframebuffer/screenshot APIはないため、`capture`は明示cameraから決定的なSVG投影とscene JSONを生成し、結果の `screenshotAvailable` をfalseにします。CIではこの2成果物を比較できます。`scene summary`は宣言上のworld bounds、配置、material欠落、無効参照を報告します。

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

assertionはmember値に加え、`$component:key`の存在と`kind: "child-count"`を扱えます。child-countには`name`、`componentType`、固定`count`、またはprobe前からの`delta`を指定できます。`assertions`欠落は`APPLY_TEST_ASSERTIONS_MISSING`です。未知propertyは黙って無視せず、たとえば`argumnts`には`arguments`をsuggestします。

## Diff、rename、prune、recovery

~~~powershell
resoloop diff content/main.json --json
resoloop diff content/main.json --changes-only --json
resoloop diff content/main.json --deletes-only --json
resoloop apply content/main.json --prune --yes --json
~~~

`diff`はcreate/update/rename/delete/no-op、理由、list要素のadded/removedを返し、worldを変更しません。JSONの `changes` にはno-op以外が常に入り、`--changes-only` / `--creates-only` / `--deletes-only` / `--summary` は `operations` の表示だけを絞ります。SyncObject listは子memberを構造値へ正規化して比較します。renameはstable keyで同一Slotを追跡してnameを更新します。delete候補はstateに記録されたownership root内の対象だけです。通常applyは削除しません。`--prune --yes`では、staleな親Slotがある場合は配下のSlot / Componentを個別削除せず、最上位のstale親Slotを1回削除して対応するstateをまとめてcheckpointします。親に含まれないstale Componentだけは個別に削除します。

ResoniteLinkのoperationはtransactionではありません。結果は常に `atomic: false` とcheckpoint pathを含む復旧手順を返します。途中失敗後は原因を直し、同じapplyを再実行して収束させます。

## Flux module manifest

[examples/flux/resoloop.flux.json](../examples/flux/resoloop.flux.json)を参照してください。moduleごとにsource、Flux module path、`dependsOn`を宣言します。依存cycleは事前に拒否され、topological orderで成功buildだけをdeployします。

~~~powershell
resoloop flux deploy-manifest examples/flux/resoloop.flux.json --json
resoloop flux watch examples/flux/resoloop.flux.json --json
~~~

parentに `$slot:key` を使う場合は`worldState`または`--state`が必要です。現在sessionなら保存IDを検証し、sessionが変わっていれば保存pathから再解決します。module input/outputは次のようにstable bindingとして宣言できます。

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

manifest deployはsource headerの`in`/`out` signatureとbindingを一対一で照合してからbuild/deployへ進み、方向、world target型、driveのmember可否を検査します。build結果の`Packing 0 ProtoFlux nodes`は`FLUX_EMPTY_MODULE`、未結線portは`FLUX_MODULE_PORT_UNBOUND`です。Flux-SDK 1.9.xの`IButton global`のようなinterface globalは既知の非原子的失敗を避けるためdeploy前に拒否されます。concrete Componentの`element` inputからmodule内で`asDrivenGlobal`するか、eventだけならDynamic Impulse bridgeを選びます。

## Portable item audit

~~~powershell
resoloop item audit Root/MyItem --strict --json
~~~

Grabbableを保存する前に、root以下のSlot、Component、nested memberを参照閉包として検査します。root外の通常参照とFlux参照は保存後に切れるerror、Userなど再取得前提の参照はruntime-context warning、`--allow-external`で指定したIDまたはstable selectorは明示許可として分類されます。strict modeではwarningも不合格です。Grabbable、Flux module、runtime targetを同じ保存rootへ収めてから監査してください。
