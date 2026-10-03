# 遅延して閉じるドアの設計と評価

2026年10月3日、3番目の作例として `orange World` の `Root/ResoLoop_Example_DelayedDoor` に配置した。初稿はComponentと小さなFluxを組み合わせた。フィールド・時間経過・撮影の確認は済み、実際の押下・通行・複数人の評価を待っている。

## 仕様と状態

- ボタンを押すと右へ開き、最後の押下から3秒で閉じる。開いている間の再操作は、閉じる期限をその時点から3秒後へ上書きする。
- 共有する状態は `ValueField<double>.Value` の閉鎖期限一つ。初期値0は閉じた状態。初回作成時だけ初期化し、通常の再適用では上書きしない。
- 通常操作では押したユーザーのButtonValueSetが期限を書き込む。起動時はホストだけが期限を0へ初期化する。誰でも操作可能とし、複数人の同時操作には独自の順序保証を設けない。ネットワーク競合の結果は未確認。
- 各ユーザーは `WorldTimeDouble < CloseAt` から開閉を導く。閉鎖時の共有状態への書き込みや、残り秒数の毎フレーム同期は行わない。
- 位置は閉 `[0, 1.12, 0]`、開 `[1.3, 1.12, 0]` の2姿勢を即座に切り替える。滑らかなアニメーションや挟み込み検知はこの初稿に含めない。
- 戸と枠にBoxColliderを置き、CharacterColliderを有効にした。通行を阻むこと・開くと通れることは実際の歩行で確認する。

期限は現在のワールド時刻に属する。初稿では、保存期限が残ったままTだけが0へ戻るため、再開後に長時間開く欠陥があった。ユーザーの指摘を受け、ホスト限定のOnStartで期限を0へ戻す修正を加えた。途中参加したゲストは共有期限をリセットせず、現在値から表示を導く。実際のワールド保存再読込と途中参加は未検証。

## ワールド開始時の初期化

ユーザーは、起動時の初期化が通常処理と別のFluxに分かれている点も良いと評価した。初期化の発火条件と書き込み対象を独立して追える構成として維持する。

`DelayedDoorReset.pg` の `OnStart(OnlyHost=true)` から、mutable入力で既存の `close-at.Value` へ0を書き込む。追加は5ノード。通常処理の7ノードと合わせて12ノードで、LocalUpdate・Sequence・Delayは引き続き0。初期化モジュールは同じlogic Slotの子に追加し、ユーザーが展開・移動した既存のDelayedDoorモジュールは置き換えていない。

OnStartは厳密な「ワールド開始専用」ではなく、そのノードが各ユーザー側で開始したときのイベント。OnlyHost=trueでゲストの途中参加によるリセットを防ぐ。ホスト側でこの初期化グラフを新規生成・置き換えた場合にもリセットする仕様とした。別ワールドへゲストが持ち込む場合や、途中のホスト交代は別途検証が必要。

既存Component候補にはDynamicValueVariableResetがあるが、今回は直接参照するValueFieldを使っている。初期化だけのためにDynamicVariableSpaceや変数検索を追加するより、OnStartとWriteの小さなFluxで意図を示す構成を選んだ。

実機では追加前の保存期限6646.254398999951が、初期化グラフ配置後に0へ変わり、CLOSEDになった。OnStartのOnlyHostがtrue、TriggerがValueWrite、書き込み値がdoubleの0、書き込み先が既存の期限フィールドであることを再観測した。これは配置時の初期化確認であり、ワールド全体の保存・閉鎖・再起動を実行した証拠ではない。

## 実装手段の選択

| 役割 | 実装 | 判断理由 |
| --- | --- | --- |
| 押下時に期限を書き込む | UIX.ButtonとButtonValueSet<double> | 既存Componentがイベントからの値書き込みを直接表す |
| 現在時刻に3秒を足す・期限と比較する | DelayedDoor.pg | 数式2つを短く読める。Componentだけの計算連鎖を増やす必要がない |
| 共有期限を保存する | ValueField<double> | 非Driveの共有フィールドを、通常操作では押下時だけ更新する |
| 起動時に期限を初期化する | OnStart(OnlyHost=true)とWrite | 保存値とワールド時刻の寿命を揃え、ゲストの途中参加でリセットしない |
| 開閉boolを2か所へ配る | ValueMultiDriver<bool> | WriteBack不要なのでValueCopyを複数使わない |
| 戸の位置・OPEN/CLOSEDの文字 | BooleanValueDriver<float3/string> | 2状態の値を既存Componentで直接指定できる |

ネイティブのTimer/Delay/Playback系ComponentとFluxの遅延ノードも候補として調べた。この仕様では遅延後の副作用を要求していないため、最新の期限を保持し現在時刻と比較する方式を採用した。閉鎖コールバックを登録しないので、古い処理の取消や世代番号を管理する必要がない。これは「期限に応じた表示」の作例であり、遅延後に一度だけ処理を実行する用途にはそのまま一般化しない。

`ButtonValueSet.SetValue` を `WorldTimeDouble + 3.0d` でDriveし、押下時にその値を保存期限へコピーする。ButtonとButtonValueSetは同じSlot、SendSlotEvents=true。単なる保持やホバーでは期限を書き換えない設計。

期限・ValueMultiDriver・位置Driver・文字Driverは `Deadline and door drivers` の1 Slot、4 Componentにまとめた。生成Fluxはその子の `DelayedDoor`。ビルド結果は7ノード、LocalUpdate・Sequence・Delayは0。実機の生成モジュールは12 Slot・27 Componentで、SDKの入力・出力・補助構造を含む。ノード数とComponent総数を同一視しない。

## 検証

Resonite 2026.9.18.82、ResoniteLink 0.13.1.0、Flux-SDK 1.9.0でReflection、check、build、deployを実施した。

- 宣言の2ケース・6項目が成功。過去／未来の期限に対して開閉bool、文字、位置DriverのStateを確認し、元の期限へ復元した。
- 時間経過の10項目が成功。ボタンに渡す3秒後の値を期限へ書いて開閉すること、旧期限前の上書き、旧期限後の開状態、新期限後の閉状態、実際のSlot位置を確認した。
- 延長テストはCLIの接続往復に余裕を持たせるため10秒後の期限を直接注入した。製品側の3秒設定は変更していない。実際の押下イベントを発火した検証ではない。
- 期限を0へ復元し、CLOSEDを確認。宣言差分は作成・更新・削除すべて0。
- 正面の閉状態と開状態、背面の戸とパネルカバーを画像で確認。撮影用の一時Slotは削除済み。
- 期限入力、ButtonValueSetのTargetValue/SetValue、ValueMultiDriverの分配先、戸のPositionへの参照を実機で確認した。

初回の宣言テストでは、CLIのmember assertionがSlot memberを扱えずPosition項目が失敗した。宣言テストでは位置DriverのStateを検証し、実際のPositionは時間経過スクリプト内のSlot inspectionで別途検証した。CLI本体は変更していない。

時間経過スクリプトの初回結果だけでは、上書きが旧期限より前に完了したことを保証できなかった。そのため明示的な旧期限前の検査を加え、余裕を広げて再実行した。採用する証跡は `timing-02.json`。

## 評価で確認すること

- [ ] 一押しで開き、約3秒後に閉じるか。
- [ ] 2秒ほど待って再度押すと、最初の押下から3秒の時点では閉じず、最後の押下から3秒で閉じるか。
- [ ] 保持・ホバーでは延長せず、押し直したときだけ延長するか。
- [ ] 開閉時の通行と衝突が期待どおりか。
- [ ] 別ユーザー、途中参加、同時操作で期限と見た目がどう再現されるか。
- [ ] 期限が0以外の状態でワールドを保存し、閉じて再び開くと、期限0・CLOSEDになるか。
- [ ] 開いている間にゲストが途中参加しても、期限が0へ戻らないか。
- [ ] ComponentとFluxの境界、期限一つの状態、1 Slotへまとめたドライバーが読みやすいか。

## ファイルと再現

- [ワールド宣言](../examples/delayed-door.json)
- [Fluxソース](../examples/flux/DelayedDoor.pg)
- [ホスト限定の初期化Flux](../examples/flux/DelayedDoorReset.pg)
- [Flux接続宣言](../examples/flux/delayed-door.flux.json)
- [明示実行する時間経過プローブ](../examples/verify-delayed-door.ps1)

リポジトリルートから実行する。discoverで選択した接続先を `$studyUrl` に設定し、既存stateを保持する。プローブ中はこの作例を他のユーザーが操作しないようにする。

```powershell
resoloop diff examples/delayed-door.json --state .resoloop/state/delayed-door.json --brief --url $studyUrl --json
resoloop apply examples/delayed-door.json --state .resoloop/state/delayed-door.json --brief --url $studyUrl --json
resoloop flux check examples/flux/DelayedDoor.pg --json
resoloop flux deploy-manifest examples/flux/delayed-door.flux.json --url $studyUrl --json
resoloop test examples/delayed-door.json --state .resoloop/state/delayed-door.json --probe --yes --brief --url $studyUrl --json
./examples/verify-delayed-door.ps1 -Url $studyUrl -Probe -Report artifacts/delayed-door-study/new-timing.json
```

プローブは `-Probe` 必須、対象Root名を確認し、finallyで元の期限を戻す。既存レポートは上書きしない。期限そのものを復元するため、元の期限が実行中に経過した場合は閉じる。

証跡は `artifacts/delayed-door-study/` の `test-02.json`、`timing-02.json`、`logic-01.json`、`convergence.json`、`front-01.jpg`、`front-open.jpg`、`rear.jpg`。stateは `.resoloop/state/delayed-door.json`。これらの実機記録はGit管理外。

初期化追加の証跡は `reset-structure.json`、`test-reset.json`、`timing-reset.json`。配置時の初期化と通常の開閉・期限延長を分けて記録する。

参考：[ButtonValueSet](https://wiki.resonite.com/Component%3AButtonValueSet)、[WorldTimeDouble](https://wiki.resonite.com/ProtoFlux%3AWorld_Time_Double)、[Delay](https://wiki.resonite.com/ProtoFlux%3ADelay)、[OnStart](https://wiki.resonite.com/ProtoFlux%3AOnStart)。型・member・接続は実機Reflectionを優先した。
