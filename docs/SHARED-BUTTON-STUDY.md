# 共有ボタンとランプの設計と評価

2026年10月3日、明るさスライダーに続く2番目の作例として作成した。共有するON／OFF状態、そこから導く表示、押下時だけの音を分け、ProtoFluxを使わずに構成した。ユーザーからComponentの選択は良いと評価され、Slotの統合とValueMultiDriverへの置き換えを実施した。改訂後のフィールド応答は検証済み。実際の押下・聴感・複数人での挙動の実施結果はまだ明示されていない。

## 現在の仕様

- 保存状態は `ValueMultiDriver<bool>.Value` の一つ。初期値はfalseで、初回作成時だけ初期化する。ButtonToggleはこのValueを直接反転する。
- 誰でもボタンを押せる。押下したユーザーの操作で `ButtonToggle` が共有状態を反転する。ホスト限定や独自の操作キューは設けない。同時操作で全押下が順番どおり反映される保証はない。
- OFFはライト強度0、発光色は黒、表示はOFF。ONは強度4、発光色は白、表示はON。表示は専用フィールドをDriveする。
- 押下音は約80 msの短い音。押下に対するフィードバックであり、状態変更の成功通知ではない。状態の反転と音の間に処理順序の保証を要求しない。
- 押し続ける・離す・ホバーするだけでは追加の音を鳴らさない。再度押せば新しい音が鳴る設計で、短時間の連打による音の重なりは許容する。
- 表示は共有状態から再現する。途中参加や状態の読み直しを押下として扱わず、過去の操作音を再生しないことを期待する。
- 音は操作位置の空間音とし、近くの参加者に聞こえることを期待仕様にする。MinDistance 0.5、MaxDistance 4、SpatialBlend 1。実際に聞こえるユーザー範囲と途中参加時の挙動は二人以上で検証する。

クリップが未ロード／音が聞こえない場合も、音を状態変更の前提にしない。対象フィールドが消失した場合の復旧は未検証。同期競合を含む厳密な操作順や成功後だけの音が必要になれば、権限とイベント処理を含めて設計を見直す。

## 実装手段の判断

| 役割 | 採用したComponent | 理由 |
| --- | --- | --- |
| 入力 | UIX.ButtonとButtonToggle | 共有boolの反転を直接表せる |
| 保存状態と値の分配 | ValueMultiDriver bool | Valueを共有状態とし、Drivesで三つの表示ドライバーのStateを制御する |
| 強度・色・文字 | BooleanValueDriverを3つ | 同じboolから型の異なる専用フィールドへ導く。ValueCopyは不要 |
| 押下音 | ButtonAudioClipPlayerとStaticAudioClip | PressedClipsだけに音を設定し、状態変化からの再生を避ける |

コンポーネントもFieldDriveを使う。Driveの適否と、処理をComponent／Fluxのどちらで表すかは別に判断した。今回は特別な分岐・遅延・処理順序がなく、既存コンポーネントで役割が対応するため、FluxやLocalUpdate、Sequenceを追加する理由はなかった。

`ButtonToggle` と `ButtonAudioClipPlayer` は同じボタンSlotに置き、`SendSlotEvents=true`。Press以外のクリップ一覧は空。音の一時Slotは所有ルート内の `Transient press sounds` 以下に生成する設定とした。音の寿命・後片付けも実際の押下で確認する。

共有状態と三つの表示ドライバーは、同じ目的と寿命を持つため、一つの `Shared state and display drivers` Slotへまとめた。UIレイアウトや音の一時生成先など、別の境界が必要な部分は分離している。

## Slot統合とValueMultiDriverへの改訂

ユーザーの所見：Componentの使い方は良いが、状態・light・emission・labelを別Slotに分ける必要はない。同じ値を複数のValueCopyでDriveするより、2か所以上でWriteBackが不要ならValueMultiDriverでまとめる。1か所へのコピーやWriteBackが必要な場合はValueCopyが適する。

実機Reflectionで `ValueMultiDriver<bool>` のValueとDrivesを確認した。独立したValueFieldと3つのValueCopyを1つのValueMultiDriverに置き換え、Valueを保存状態として兼用した。ValueMultiDriverにはSource入力がないため、今回はボタンをValueへ直接接続した。外部の既存値を読み取る設計なら、その値の所有権を保ってValueへ一度だけ渡す構成にする。

| 対象部分 | 改訂前 | 改訂後 |
| --- | --- | --- |
| Slot | 4 | 1 |
| Component | 7（ValueField 1、ValueCopy 3、表示Driver 3） | 4（ValueMultiDriver 1、表示Driver 3） |

既存の状態SlotをIDを保って改名し、3つの表示Componentを移した。移動ではComponentが再作成されるため参照を再接続した。現在値falseを直前に再観測し、改訂後にもfalseを確認した。旧3 Slotと旧ValueFieldだけを、削除対象を確認したうえで明示的なpruneで除去した。Slot名の整理のために安定キーを変更していない。

改訂後もOFF／ONの計8項目が成功。ValueMultiDriver.Drivesの3参照、各表示Driverの出力先、ボタンのTargetValueを再観測し、差分0を確認した。実装と判断基準をresonite-build・resonite-uix、およびUIXの既存referenceへ反映した。

## 配置とファイル

`orange World` の `Root/ResoLoop_Example_SharedButton` に配置。前のスライダー作例とユーザーのComponentSample0は変更していない。

- [宣言と検証ケース](../examples/shared-button.json)
- [押下音](../examples/assets/button-click.wav)

音はこの作例用に生成した880 Hzの正弦波、24 kHz・16 bit・モノラル・1920サンプル。振幅0.35にsin²の窓をかけた80 msの素材で、再生音量は0.2。外部の録音素材は使っていない。

初稿の実機観測は19 Slot・35 Component（エンジン生成物を含む）、ProtoFluxノード0。初稿の管理Componentは34。今回の変更は3 Slot・3管理Componentの削減で、宣言全体は16 Slot・31管理Componentになる。作業中のGizmo等は増減するため、現在の生の総数とは区別する。

## 確認できたこと

Resonite 2026.9.18.82／ResoniteLink 0.13.1.0でReflectionを使って型・member・参照を確認し、diff／applyを実行した。

- OFF／ONの2ケースで各4項目、計8項目が成功。ライト強度、発光色、文字表示、音用Slotの観測時の子数0を確認した。
- 各プローブ後に元の共有状態を復元した。検証後はOFF。
- 初稿ではボタンのTargetValue、三つのValueCopyとDriver、クリップ参照を再観測した。改訂後はValueMultiDriverと三つのDriverの参照を再確認した。
- デプロイ後のdiffは作成・更新・削除すべて0。音アセットは1件をimportした。
- 正面のOFF表示とボタン、背面カバーを撮影で確認した。背面の撮影は手前の既存インスペクターで一部が隠れており、その範囲の見た目は未確認。撮影用の一時Slotは削除済み。

フィールドプローブは押下イベントを発火していない。音用Slotの子数確認も一時点の観測であり、音が一切鳴らないことや実際の押下音を聴いた証明にはならない。ResoniteLinkの公開Reflectionでは今回のButtonの押下を発火できるSyncMethodは得られておらず、操作の確認は人間が行う。

ローカル証跡は `artifacts/shared-button-study/` の `structure-01.json`、`test-01.json`、`convergence-01.json`、`front.jpg`、`rear.jpg`。統合後の証跡は `consolidated-logic.json`、`test-consolidated.json`、`consolidated-convergence.json`。stateは `.resoloop/state/shared-button.json`。いずれもGit管理対象外。

## 人間による評価

- [ ] 一度押すとON、次に押すとOFFになるか。
- [ ] 押し続けて反転や音が連発しないか。離す・ホバーだけで鳴らないか。
- [ ] 一押しに対して音が一度鳴り、連打後に不要な音のSlotが残らないか。
- [ ] 保存状態、表示用ドライバー、押下音の役割を後から読めるか。
- [ ] 別ユーザーに同じ状態が見え、近くで押下音が聞こえるか。同時操作の挙動をどう評価するか。
- [ ] 途中参加では表示だけが再現され、過去の音を再生しないか。
- [ ] 入力変更後の再適用や保存・再読込で状態を保持できるか。

Component選択とSlot構成に関する所見は上記に記録した。操作・聴感・同期に関するチェックは、構成への評価だけでは完了扱いにしない。

## 再現手順

接続先はdiscoverで選択し、`$studyUrl`に設定する。リポジトリルートから実行し、diffの対象を確認してからapplyする。既存のstateを保持し、テストは他のユーザーが操作していないときに明示的に実行する。

```powershell
resoloop diff examples/shared-button.json --state .resoloop/state/shared-button.json --brief --url $studyUrl --json
resoloop apply examples/shared-button.json --state .resoloop/state/shared-button.json --brief --url $studyUrl --json
resoloop test examples/shared-button.json --state .resoloop/state/shared-button.json --probe --yes --brief --url $studyUrl --json
```

公開の参考資料：[ButtonToggle](https://wiki.resonite.com/Component%3AButtonToggle)、[ButtonAudioClipPlayer](https://wiki.resonite.com/Component%3AButtonAudioClipPlayer)。実際に設定する型・フィールド・リスト要素は実機Reflectionの結果を優先した。
