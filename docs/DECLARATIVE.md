# rloop schema v1 authoring

`rloop`は複数のJSON sourceを展開してから、schema v1として一括検証します。展開は接続なしで行われ、循環include、未解決parameter、stable key衝突、10,000 node／10 MiB／64 source fileの上限違反をmutation前に拒否します。

## Include、parameter、prototype、repeat

~~~json
{
  "include": ["materials.json"],
  "schemaVersion": "1",
  "ownership": { "key": "my-world" },
  "parameters": { "spacing": 1.5 },
  "variables": { "rootName": "RLoop_Test_MyWorld" },
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
    "probe": { "target": "$component:button", "method": "Press", "safe": true },
    "timeoutMs": 2000, "pollMs": 100
  }]
}
~~~

~~~powershell
rloop scene summary content/main.json --output artifacts/scene.json --json
rloop capture content/main.json --camera main --output artifacts/main.svg --json
rloop test content/main.json --json
rloop test content/main.json --probe --yes --json
~~~

ResoniteLink 0.13.1にframebuffer/screenshot APIはないため、`capture`は明示cameraから決定的なSVG投影とscene JSONを生成し、結果の `screenshotAvailable` をfalseにします。CIではこの2成果物を比較できます。`scene summary`は宣言上のworld bounds、配置、material欠落、無効参照を報告します。

通常の`test`はField/Reference構造だけを検証します。probeはmanifestで `safe: true`、CLIで `--probe --yes` の両方が必要です。公開Reflectionにmethodがなければ呼び出さず、`structuralOnly: true`と未評価のafter assertionを明示します。methodが利用可能ならpublic SyncMethod APIで呼び、after assertionをtimeoutまでpollします。

## Diff、rename、prune、recovery

~~~powershell
rloop diff content/main.json --json
rloop diff content/main.json --changes-only --json
rloop diff content/main.json --deletes-only --json
rloop apply content/main.json --prune --yes --json
~~~

`diff`はcreate/update/rename/delete/no-op、理由、list要素のadded/removedを返し、worldを変更しません。JSONの `changes` にはno-op以外が常に入り、`--changes-only` / `--creates-only` / `--deletes-only` / `--summary` は `operations` の表示だけを絞ります。SyncObject listは子memberを構造値へ正規化して比較します。renameはstable keyで同一Slotを追跡してnameを更新します。delete候補はstateに記録されたownership root内の対象だけです。通常applyは削除せず、`--prune --yes`を同時指定した場合だけComponent、深いSlotの順に削除します。

ResoniteLinkのoperationはtransactionではありません。結果は常に `atomic: false` とcheckpoint pathを含む復旧手順を返します。途中失敗後は原因を直し、同じapplyを再実行して収束させます。

## Flux module manifest

[examples/flux/rloop.flux.json](../examples/flux/rloop.flux.json)を参照してください。moduleごとにsource、Flux module path、`dependsOn`を宣言します。依存cycleは事前に拒否され、topological orderで成功buildだけをdeployします。

~~~powershell
rloop flux deploy-manifest examples/flux/rloop.flux.json --json
rloop flux watch examples/flux/rloop.flux.json --json
~~~

parentに `$slot:key` を使う場合は`worldState`または`--state`が必要です。現在sessionなら保存IDを検証し、sessionが変わっていれば保存pathから再解決します。deploy stateはsourceとtransitive dependencyのhash、置換後Slot IDを保存し、no-op/updateと非atomic recoveryを報告します。watchは変更を検出して全moduleをbuildし、成功したmoduleだけを検証済みparentへ再deployします。
