# 順序が必要な試行カウンター

2026年10月3日。5候補の最後となる、Sequenceに用途上の理由がある対照例。`Root/ResoLoop_Example_OrderedAttempt`、位置 `[12, 1.5, 2]` に配置した。共有慣性マーカーの隣で比較できる。

## 仕様

モードを選び、TRYを押して条件付きWriteを試す。その同期処理が戻った後、成否によらず試行回数を1増やし、今回の結果を表示する。

| モード | 書き込み | 試行回数 | 結果表示 |
| --- | --- | --- | --- |
| SKIP | 条件が偽なので実行しない | +1 | SKIPPED |
| SUCCESS | ValueIncrementで共有値を+1 | +1 | WRITTEN |
| FAIL | Variable未接続のWriteを実行し、実際にOnFailを発火させる | +1 | WRITE FAILED |

RESET COUNTSは共有値と試行回数を0、結果をREADYへ戻す。モードは維持し、リセット操作自体は試行回数に含めない。初期モードはSUCCESS。

誰でも操作でき、ButtonEventsは押した人のコンテキストで動く。モード・値・回数・結果は同期フィールド。今回の結果の中間値だけをLocal<string>に置く。非同期処理、Update、LocalUpdateは使わない。

回数と値は保存して残す累計なので、ワールド起動時にはリセットしない。初期値はinitialFieldsに置き、再applyでも実行中の値を消さない。時刻原点に依存する遅延ドアとは初期化の目的が違う。

1回の同期実行に対して記録を1回行う例であり、複数端末の同時押下を厳密に合算する分散カウンターではない。競合する読み取り・加算・書き込みはホスト裁定していない。逐次のボタン操作で制御フローを評価する。

## ComponentとFluxの分担

モード選択は既存のchoiceレシピ、選択色はBooleanValueDriverと単一宛先のValueCopy、数値表示はValueTextFormatDriverを使う。関連する値と表示ドライバーはlogic Slotの4 Componentにまとめた。選択状態は選択欄のSlotに配置し、同型Componentの識別も明確にした。

ButtonValueSet/Shift/ActionTriggerなどの候補を調査したが、条件分岐、Writeの成功・失敗、それに依存しない共通の記録処理を読む目的にはFluxが直接的。Componentだけの連鎖には置き換えなかった。

宣言は25 Slot・65 Component。ValueIncrementへの変更でFluxは46から42ノードになり、生成補助構造を含む実機モジュールは47 Slot・78 Component。UIの前後素材、各コントロール、表示のレイアウトには別Slotの意味がある。構成量は性能測定ではない。

## Sequenceを残す理由

```text
_run = impulse {
    _attempt;
    _record;
};
```

この2つのまとまった処理の間だけにSequenceを使う。`_attempt`はSKIP・OnWritten・OnFailの各経路でLocalの結果を決める。同期処理なのでSequenceの次の出力が呼ばれる時点で結果が確定している。`_record`はその結果を読み取るため、順序が必要。

```text
_record = impulse {
    bind _counted = ValueIncrement<FrooxEngine.ProtoFlux.FrooxEngineContext,int>(Variable=Attempts).OnWritten;
    Summary <- _result;
};
```

記録内部は、回数のValueIncrementが成功してから結果を保存する。リセット内部もOnWrittenでつなぐ。回数の更新自体が失敗した場合まで必ず記録できるという意味ではなく、失敗時は後続が止まる。巻き戻しはしない。

成功するWriteのOnWrittenだけに`_record`をつなぐと、SKIPとFAILを数えなくなり別の仕様になる。すべての分岐末尾へ共通の記録処理をつなぐ実装も可能だが、ここでは「試す」「必ず記録する」の2つの役割をSequenceでまとめた。Sequenceが唯一の解法という主張ではない。

これは同期処理に限定した例。非同期処理の完了待ちをSequenceで代用しない。[Flux-SDKの制御フロー](https://flux-sdk.samsmucny.com/ProtoGraph/Impulse-Control-Flow.html)を参照。

## 検証と残る評価

Resonite 2026.9.18.82 / ResoniteLink 0.13.1.0 / Flux-SDK 1.9.0でReflection、check、build、deployを実施した。

- 実機グラフはSequenceが1個、Callsは2本。最初が条件分岐、次が回数ValueIncrement。記録とリセットの内部はOnWritten接続、更新ノードは0。
- ValueIncrementへの変更後も同じ6ケースが成功。float版とint版の2ノード、それぞれのVariableとOnWritten/OnFailの接続を確認した。配置済みパネルの設定・累計値を保持し、ユーザーが移動したモジュールSlotの位置も再配置後に復元した。
- 隔離したResoLoop_Test_OrderedAttempt_* Slotで、製品ソースの処理本体をそのまま使い、ボタン入口だけをホスト限定OnStartへ置換して6ケースを検証した。成功→スキップ→失敗→成功→失敗の後、回数5・値2。リセット後は回数0・値0・READY。全ケース成功。
- 書き込み先未接続の失敗でも値は変わらず、OnFailの結果が記録された。Sequenceの後半が先に実行されると今回のLocal結果を記録できないため、結果表示も検証した。
- ソースハッシュが同じならdeploy-manifestは再配置を省略する。テストではケース名コメントを変え、各ケースでOnStartが新規インスタンス上で実行されるようにした。
- 前面の文字、選択色、数値表示、背面カバーを実機撮影で確認。試験・撮影用Slotは名前と親を確認して削除済み。宣言差分0、警告0。

2026年10月3日、ユーザーはこの作例を「これは問題ない」と評価した。改善候補として、+1は加算結果のWriteよりValueIncrementを使えるとの指摘を受けた。成功経路の共有値と、記録経路の試行回数をValueIncrementへ変更した。意図的な失敗を起こす未接続Writeは対照例として残す。複数人の同時操作や保存再読込など、個別の実施条件が明示されていない項目まで確認済みとは扱わない。

初稿はSUCCESS・READY・回数0・値0で配置した。更新時には操作設定と累計値を保持する。評価時はRESET COUNTS後、SUCCESS→TRY、SKIP→TRY、FAIL→TRYの順で、回数3・値1・WRITE FAILEDになることを確認できる。

## SDK上の注意

ValueIncrementもVariableのコンテキストを合わせる。今回のmutable入力には `ValueIncrement<FrooxEngine.ProtoFlux.FrooxEngineContext,float>` と同じint版を使い、OnWritten/OnFail/Variableを実機Reflectionで確認した。通常のLocal/Storeに使うExecutionContext版とは区別する。

`ValueWrite<float>(Variable=Target, ...)`はコンパイルできたが、mutable入力のValueSourceが要求されたExecutionContextのIVariableに適合せず、実機への参照接続で失敗した。`switch (Target <- Target + 1.0)`にすることでSDKがFrooxEngineContext版のWriteを選択し、配置と動作確認に成功した。意図的な未接続Writeだけは`ValueWrite<float>`を使う。失敗した配置の内容を確認してから、同じ管理対象モジュールを再配置した。

## 再現と証跡

```powershell
resoloop diff examples/ordered-attempt.json --state .resoloop/state/ordered-attempt.json --brief --url $studyUrl --json
resoloop apply examples/ordered-attempt.json --state .resoloop/state/ordered-attempt.json --brief --url $studyUrl --json
resoloop flux validate-manifest examples/flux/ordered-attempt.flux.json --json
resoloop flux deploy-manifest examples/flux/ordered-attempt.flux.json --url $studyUrl --json
./examples/verify-ordered-attempt.ps1 -Url $studyUrl -Probe -Report artifacts/ordered-attempt-study/new-verification.json
```

検証は隔離したコピーを使い、配置済みパネルの状態を変更しない。保存先は新しいレポート名を選ぶ。Git管理外の証跡は `artifacts/ordered-attempt-study/verification-03.json`、`structure.json`、`deploy-02.json`、`convergence-02.json`、`front.jpg`、`rear.jpg`。

ValueIncrement版の証跡は同じディレクトリの `increment-verification.json`、`increment-structure.json`、`increment-deploy.json`、`before-increment-values.json`、`after-increment-values.json`。元の展開済みグラフは `before-increment.json` に記録した。

ソースは [OrderedAttempt.pg](../examples/flux/OrderedAttempt.pg)、[周辺宣言](../examples/ordered-attempt.json)、[Flux manifest](../examples/flux/ordered-attempt.flux.json)、[検証スクリプト](../examples/verify-ordered-attempt.ps1)。
