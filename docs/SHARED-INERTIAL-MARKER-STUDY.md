# 使用者が計算する共有慣性マーカー

2026年10月3日、`Root/ResoLoop_Example_SharedInertialMarker`、位置 `[10, 1.5, 2]` に配置した。既存のローカル版 `[8, 1.5, 2]` は変更していない。元の5候補のうち、4番目の作例の比較版。

ユーザーは「ローカルで動かすべきものなら前作の実装は正解」と評価し、使用者が計算して結果を全員に同期する版を提案した。計算式・移動範囲・操作を揃え、実行者と状態の置き場所を比較する。

## 目的による違い

| 観点 | ローカル版 | 使用者が計算する共有版 |
| --- | --- | --- |
| 目的 | 各ユーザーの表示用シミュレーション | 1人が計算した結果を皆で観察する |
| 計算の起点 | 各ユーザーのLocalUpdate | Operatorを指定したUpdate、SkipIfNull=true |
| 位置・速度 | ユーザーごとのStore | 非Driveの同期フィールドへ使用者がWrite |
| 表示 | 自分の計算結果からDrive | 受信した共有値からComponentでDrive |
| 操作 | 誰でも共有設定を操作 | 誰でも操作・停止。計算担当はSTARTを押した人 |
| 途中参加 | 原点・速度0から自分の運動を開始 | 現在の共有位置・速度を受信して表示する設計 |
| 通信 | 操作設定を同期 | 操作設定と計算結果を同期 |

共有版でも、受信した状態を表示する部分はDriveでよい。違いは、積分した結果を全員分別々に生成するか、指定した使用者のWriteで共有状態として確定するかにある。

## 実装

未使用時のSTARTで、押したクライアントのLocalUserをReferenceField<User>.Referenceへ保存し、成功後にRunningをtrueにする。ButtonEventsが処理する。誰がSTOP / RESETを押してもRunning=false、位置・速度0、使用者参照を空にする。加速度は保持する。ZERO FORCEは加速度だけを0にし、惰性と減衰を観察する。

ユーザーの指摘を受け、ボタン側のIsLocalUserによる拒否と、ボタン・スライダーの使用者限定Enabled制御を削除した。計算担当者を1人にすることと、操作できる人を限定することは別の仕様。今回は誰でも触れるアイテムとし、加速度変更・ZERO FORCE・STOPを誰でも使える。null判定にはIsNull<User>を使う。

UpdateのUpdatingUserをOperatorからDriveし、SkipIfNull=trueに固定した。空の場合にホストへ暗黙に計算が移ることを防ぐ。更新内でもRunningとIsLocalUser(Operator)を確認する。[Updateの仕様](https://wiki.resonite.com/ProtoFlux%3AUpdate)に基づく選択であり、運動計算をホストに固定したものではない。

位置はValueMultiDriver<float3>.Value、速度はValueField<float>.ValueにWriteする。位置のMultiDriverがmarker.PositionをDriveし、速度はValueTextFormatDriver<float>で表示する。Runningをラベルと発光へ配るMultiDriverも維持した。関連する状態・表示制御は1 Slot・8 Component。周辺宣言は26 Slot・48 Componentで、実行時の自動追加分は含めない。

運動はローカル版と同じ式、dt上限0.05秒、速度上限±1.5、範囲±0.7、反射係数0.65。計算途中のdt・次速度・次位置はLocalで確定し、最後に共有フィールドへ書く。通常Fluxは83ノード、初期化・退出処理は別Fluxの24ノード。生成補助構造を含む実機の数は、それぞれ88 Slot・125 Componentと29 Slot・54 Component。

## Writeの成功を条件にした接続

単独のWriteをimpulse内に並べるとSequenceになるため、依存する後続WriteをOnWrittenへ接続した。Flux-SDKでは次のように書ける。[公式のbind説明](https://flux-sdk.samsmucny.com/ProtoGraph/Impulse-Control-Flow.html)にあるcontinuationの明示を、代入式へ適用する。

```text
impulse {
    bind _claimed = (Operator <- LocalUser).OnWritten;
    Running <- true;
}
```

停止・初期化・積分・反射・結果確定の依存するWriteも同様に接続した。実機で両モジュールのSequenceが0、後続のあるWriteはOnWrittenへ接続、OnFailは未接続であることを確認した。失敗時はその後続を実行しない。ただし、既に成功したWriteを巻き戻すトランザクションではない。

Sequence自体を禁止する方針ではない。前の処理の成否にかかわらず次のまとまった処理を実行する仕様なら有用。条件付き処理の後に試行回数を増やす候補などは、成功時だけつなぐと意味が変わる。単なるノード数削減として両者を置換しない。

## 初期化と使用者の交代

SharedInertialMarkerResetのOnStart(OnlyHost=true)で、使用者を空、Running=false、加速度・位置・速度を0に戻す。ゲストの途中参加ではリセットしない設計。ホスト側で初期化グラフを新規配置・置き換えた場合もリセットする。

UserLeftもOnlyHost=true。Running中で退出者がOperatorに一致するか、使用者参照が既に空に解決されている場合に同じ初期化を行う。観客の退出では継続し、使用者の退出では引き継がず停止する意図。ホスト自身の退出・ホスト移譲を含む実際の退出動作は未確認。

交代は停止してから次の人がSTARTを押す方式。強制取得や速度を維持した交代は実装していない。同時STARTは同期参照の競合解決に依存し、収束前の一時的な複数書き込みを保証付きで排除するものではない。厳密な排他取得が目的なら、ホストによる取得要求の裁定などを追加する。

## 同期の範囲

使用者の更新ごとに位置・速度へ書く。実際のネットワーク送信頻度は測定しておらず、「毎フレーム必ず1パケット」とは扱わない。2フィールドは原子的なスナップショットではない。観客用の補間・予測・送信頻度制限は含めず、観客には通信遅延がある。人数や個数が増える用途では更新頻度と補間を別途設計する。

## 検証

Resonite 2026.9.18.82、ResoniteLink 0.13.1.0、Flux-SDK 1.9.0でReflection、check、build、deployを実施。

- 単一使用者プローブ12項目が成功。使用者なしで計算しないこと、指定後の共有位置・速度の変化、使用者名、惰性、減衰、反転、4回の範囲確認を観測。
- 今回の修正後も12項目が成功（`artifacts/shared-inertial-marker-study/review-motion-02.json`）。反転確認は弱い逆向き加速度で行い、その後の境界確認では強い加速度に戻す。CLIの観測待ち中に境界で再反射し、反転の判定と混ざることを避けた。
- 計算停止後に位置0.25・速度0.12を書き、2回の観測で保持された。marker Slotもx=0.25になり、ローカル積分のDriveに上書きされないことを確認。
- 検証では一時的なホスト限定OnStartからホストをOperatorへ設定した。実際のSTART押下ではない。一時Slotは名前・親を再確認してfinallyで削除し、共有状態を復元した。
- 位置0.3、速度0.2、加速度1を設定し、初期化モジュールの再配置で全て0になることを確認。保存再読込の確認ではない。
- UpdatingUserの参照経路、SkipIfNull=true、OnStartとUserLeftのOnlyHost=trueを再観測。
- 両版の前面比較と共有版の背面を撮影して確認。撮影用Slotを削除。宣言差分は0。

2026年10月3日、ユーザーから「複数人で試しましたが、問題ありませんでした」と評価を受けた。OnWrittenでつなぐ書き方にも納得できるとの評価があり、依存する処理の基本方針として採用した。個別の参加・退出、同時START、保存再読込など、実施手順が明示されていないケースまで検証済みとは扱わない。エージェントの単一ユーザープローブと、人間による複数人評価を区別して記録する。

OnWrittenの動作は独立した一時グラフでも検証した。書き込み先のないWriteでOnFailが発火し、OnWrittenに接続した後続は実行されなかった。一方、有効なLocalへ書いた成功側は後続が実行された。検証用Slotはfinallyで名前と親を再確認して削除した。証跡は `artifacts/shared-inertial-marker-study/continuation-probe/result.json`。実機構造は `review-structure.json`、更新前の配置と配線は `before-review.json` に保存した。

## 現バージョンの互換性

`null<User>`の配置がReferenceをFieldへ変換できず失敗し、4個のValue Null Slotが空になった。参照をクリアするWriteの値に限り、一度も書かない既定値nullのStore<User>を使う。null判定ではこれと比較せずIsNullを使う。運動状態をローカルStoreへ戻したものではない。

UIX.Buttonはelement入力からObjectCast<Button,IButton>を経てasDrivenGlobalでButtonEventsへ渡す。入出力型と実機参照を確認した。ResoniteLinkから取得したUserの生IDを直接Referenceへ設定するプローブも失敗したため、検証では限定した一時FluxからLocalUserを書き込んだ。CLI本体は変更していない。

## 再現

接続先をdiscoverで選び、既存stateを保持する。プローブは未使用・停止状態で、他のユーザーが操作しない間に実行する。再配置前にユーザーが展開したグラフを確認する。

```powershell
resoloop diff examples/shared-inertial-marker.json --state .resoloop/state/shared-inertial-marker.json --brief --url $studyUrl --json
resoloop apply examples/shared-inertial-marker.json --state .resoloop/state/shared-inertial-marker.json --brief --url $studyUrl --json
resoloop flux validate-manifest examples/flux/shared-inertial-marker.flux.json --json
resoloop flux deploy-manifest examples/flux/shared-inertial-marker.flux.json --url $studyUrl --json
./examples/verify-shared-inertial-marker.ps1 -Url $studyUrl -Probe -Report artifacts/shared-inertial-marker-study/new-motion.json
```

成功証跡はGit管理外の `artifacts/shared-inertial-marker-study/motion-02.json`、`deploy-02.json`、`deploy-03.json`、`startup-reset.json`、`structure-final.json`、`convergence.json`、`comparison.jpg`、`rear.jpg`。元の作例は [ローカル慣性マーカー](INERTIAL-MARKER-STUDY.md)。
