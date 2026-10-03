# 継続更新する慣性マーカーの設計と評価

2026年10月3日、5候補中4番目の初稿を `orange World` の `Root/ResoLoop_Example_InertialMarker` に配置した。位置は `[8, 1.5, 2]`。継続更新の必要性を単体で読めるよう、当初のPooledProjectiles候補から生成・破棄を外し、加速度・減衰・端での反射がある1個のマーカーに絞った。人間によるグラフと操作感の評価はこれから。

## 操作と状態

ユーザーは、ローカルで動かす目的ならこの実装は正解と評価した。目的による違いを示すため、同じ運動を使用者が計算して全員へ同期する [共有版](SHARED-INERTIAL-MARKER-STUDY.md) を隣に追加した。

STARTで開始し、スライダーで左右の加速度を変える。ZERO FORCEは加速度だけを0にするので、その後も惰性で動き、減速する。STOP / RESETは位置と速度を0に戻す。停止してもスライダー値は保持する。

共有状態はRunningとAccelerationの2つ。位置・速度・前回のRunningはユーザーごとのStoreに保持する。出力をDriveして表示するため、毎フレームの共有フィールド書き込みは行わない。途中参加者は原点・速度0から現在の操作設定を適用する。全員の軌跡が一致する物理シミュレーションや、共有ゲームの判定には使わない。

[LocalUpdate](https://wiki.resonite.com/ProtoFlux%3ALocalUpdate)は各ユーザーの更新で発火する。この例は各ユーザーが自分の表示用状態を更新する設計なので、ホスト限定にはしない。[Store](https://wiki.resonite.com/ProtoFlux%3AStore)の更新を出力側で再評価させるため、ContinuouslyChangingValueRelayを挟んだ。

## ComponentとFluxの選択

操作はButtonToggle、ButtonValueSet<float>、Slider、数値表示はValueTextFormatDriver<float>。Runningをラベルと発光色へ配るValueMultiDriver<bool>と2個のBooleanValueDriverを同じlogic Slotに置いた。関連する制御・表示用のComponentは1 Slot・7 Componentにまとめている。

一定速度や往復ならPanner1D、目標値への補間ならSmoothValueを候補にできる。今回は途中で変わる加速度と、それ以前から持ち越す速度、減衰、境界反射を扱うため、計算と分岐をFluxにまとめた。これは「動くものには必ずLocalUpdate」を意味しない。

Position出力はValueMultiDriver<float3>のValueを経由してmarker.PositionをDriveする。ここだけは単一対象。現在のFlux manifestのbinding検証がSlotメンバーへの直接出力を受け付けないため、出力の受け口と対象参照を1 Componentで持つアダプターにした。CLI自体は変更していない。

## 更新と初期化

InertialMarkerは67ノード。LocalUpdateは1個で、Running中だけ運動計算する。停止への遷移でStoreをクリアし、その後は条件判定だけを行う。ローカルの更新順を表すSequenceは5個あり、速度の計算、位置の計算、反射、状態の確定を順に実行する。

- dtを0〜0.05秒へ制限する。長い停止時間は追いつかず捨てる。
- `v = clamp((v + a * dt) * (1 - 0.6 * dt), -1.5, 1.5)`、`x = x + v * dt`。
- xが±0.7を越えると端へ戻し、速度を反転して0.65倍にする。
- 境界へ加速し続けると小さな反射を繰り返す簡易モデル。静止摩擦・接触解法は持たず、厳密な物理演算ではない。
- 1個の固定マーカーのみで、更新中にSlotの生成・破棄はしない。モジュールを無効にすると停止し、再有効化は同じローカル状態から続く設計。停止ボタンは明示的なリセット。

別のInertialMarkerResetは9ノード。OnStart(OnlyHost=true)で共有Running=false、Acceleration=0を初期化する。初回配置・ホスト側のこの初期化グラフの置き換えもリセット境界になる。ゲストの途中参加では共有操作をリセットしない設計。前作の初期化を別Fluxに分けた構成がユーザーから評価され、今回も通常処理と分けた。

生成された通常モジュールは72 Slot・91 Component、初期化は14 Slot・26 Component。周辺UI・形状・native制御の宣言は26 Slot・48 Component。生成モジュールの数は補助構造を含み、ノード数とは区別する。周辺宣言の数には実行時の自動追加Componentを含めない。

## 検証と限界

Resonite 2026.9.18.82、ResoniteLink 0.13.1.0、Flux-SDK 1.9.0でReflection、check、build、deployを実施。

- 時間経過プローブ16項目が成功。正方向の加速、加速度0での惰性、速度低下、負方向への切り替え、10回の範囲・速度上限確認、停止時の位置・速度・表示、実際のmarker Slotの原点復帰を確認した。
- 慣性の初回プローブは観測前に端で反射して正の速度という条件を満たさなかった。加速度を0.04へ弱め、境界と減衰を分離した再検証が成功した。実装は変更していない。
- 負の加速度で左端へ到達後、x=-0.7と正の速度0.019前後を観測。境界での速度反転を確認した。反射係数の数値精度や全フレームの範囲を保証する測定ではない。
- manifestの停止ケース2項目が成功。宣言との差分は作成・更新・削除すべて0。
- 前面・背面を撮影して可読性と背面カバーを確認。撮影用の一時Slotは削除済み。
- 終了時はRunning=false、Acceleration=0、位置・速度0へ復元した。

入力はCLIから既存操作フィールドへ書き込んだ。実際のクリック・ドラッグ、複数人での操作と途中参加、ワールド保存再読込、無効化と再有効化は未検証。単一ユーザーの観測を同期動作の証明としない。

## 再現

discoverで接続先を選び、既存stateを保持する。プローブ中は他の人がこの作例を操作しないこと。再デプロイ前は、展開・配置された既存グラフを確認する。

```powershell
resoloop diff examples/inertial-marker.json --state .resoloop/state/inertial-marker.json --brief --url $studyUrl --json
resoloop apply examples/inertial-marker.json --state .resoloop/state/inertial-marker.json --brief --url $studyUrl --json
resoloop flux validate-manifest examples/flux/inertial-marker.flux.json --json
resoloop flux deploy-manifest examples/flux/inertial-marker.flux.json --url $studyUrl --json
resoloop test examples/inertial-marker.json --state .resoloop/state/inertial-marker.json --probe --yes --brief --url $studyUrl --json
./examples/verify-inertial-marker.ps1 -Url $studyUrl -Probe -Report artifacts/inertial-marker-study/new-motion.json
```

証跡はGit管理外の `artifacts/inertial-marker-study/`、成功した時間経過プローブは `motion-02.json`。次の候補は条件付き書き込みの成否にかかわらず試行回数を増やす「順序が必要な対照例」。
