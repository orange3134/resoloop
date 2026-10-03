# 明るさスライダーの設計と評価

共有する入力から表示を導く作例。2026年10月3日、オレンジが最初の題材として「明るさスライダー（共有入力）」を選択した。Flux初稿の配置と単独ユーザー環境でのフィールド応答を確認後、オレンジから「この場合のDriveは合理的。ただし目的に合う既存Componentも検討する」という評価と、ComponentSample0の参考実装を受け取った。ComponentとFluxを選ぶ判断基準を生成スキルに反映した。実機操作・複数人での検証がすべて完了したという意味ではない。

## 仕様と設計理由

ネイティブ UIX Slider の `Value` を保存状態とする。範囲は 0〜1、初期値は 0.5。操作ユーザーがこの非 Drive の同期フィールドを更新し、ProtoFlux は読み取りだけを行う。初期値は `initialFields` に置き、再適用による入力のリセットを避ける。

初稿は複数の操作ユーザーを許容し、ホスト限定・操作ロック・独自の優先順位は設けない。競合の処理はネイティブ制御と同期に委ねるため、同時操作時の最終値や手元への追従は未検証。途中参加者にも現在値から表示が再現されることを期待仕様とする。

入力を `Clamp01_Float` に通し、ライト強度、発光色、表示文字列を導く。三つの出力は専用のフィールドを各ユーザー側で Drive する。これらは独立した保存状態にせず、同じフィールドを他のドライバーと共有しない。

| 入力 | ライト強度 | 発光色の RGB | 表示 |
| --- | --- | --- | --- |
| 0 | 0 | 0, 0, 0 | 0 % |
| 0.5 | 2 | 0.5, 0.5, 0.5 | 50 % |
| 1 | 4 | 1, 1, 1 | 100 % |

発光色は Linear、Alpha は 1。これは入力と出力の関係を読み取りやすくするための線形の初案であり、知覚的に均等な明るさの変化を保証するものではない。整数の百分率表示だけが丸められ、強度と色は連続値を使う。

ProtoFlux のイベント入口、状態書き込み、非同期処理はない。出力 Drive がデータフローの消費者になるため、LocalUpdate は不要。三つの出力間に順序要件がないため、Sequence も不要。継続したローカル評価は存在するが、毎フレーム共有フィールドに書き戻すグラフではない。[Flux-SDK のデータフローと impulse の説明](https://flux-sdk.samsmucny.com/ProtoGraph/Impulse-Control-Flow.html)を設計の根拠とした。

再入力は現在値の置き換えで、古い遅延処理の無効化は該当しない。接続先が消失したときの復旧、NaN／Infinity、保存・再読込は未検証。再デプロイ時は参照を再確認する。

## ファイルと配置

- [ProtoGraph ソース](../examples/flux/BrightnessSlider.pg)
- [ワールド宣言とフィールド応答テスト](../examples/brightness-slider.json)
- [入出力バインディング](../examples/flux/brightness-slider.flux.json)

評価用の成果物は `orange World` の `Root/ResoLoop_Example_BrightnessSlider` に配置した。グラフはその子 `ProtoFlux - BrightnessSlider/BrightnessSlider`。既存ユーザーコンテンツは変更していない。レビュー対象として残しており、撮影用の一時 Slot は撮影処理の finally で削除済み。セッション ID や Component ID は再利用せず、保存した state と再観測で対象を解決する。

## 生成グラフ

Flux-SDK 1.9.0（e674229f）、Resonite 2026.9.18.82、ResoniteLink 0.13.1.0、リポジトリの基点 b3a84ae で確認。

| ノードの役割 | 数 |
| --- | --- |
| ValueSource float | 1 |
| Clamp01 Float | 1 |
| float 定数 | 3 |
| ValueMul float | 2 |
| Pack ColorX | 1 |
| 文字列定数 | 1 |
| ToString Float | 1 |
| 出力 Drive | 3 |
| 合計 | 13 |

コンパイル結果と配置後の Component 列挙の両方で13ノードを確認した。SDK の補助 Slot、DynamicVariable、Drive Proxy はこのノード数に含めない。Source の参照先と、三つの Drive Proxy の対象が宣言したフィールドに一致することを再観測した。LocalUpdate、Sequence は各0。グラフを実際に展開した際の配線の見やすさは人間の評価待ち。

Flux初稿を比較の基点とする。以下のComponent参考実装との構造比較は行ったが、設定に差があるため、厳密な意味的同等性や性能改善率は主張しない。一般的な LocalUpdate／Sequence の禁止ルールにはしない。

## ComponentSample0との比較

2026年10月3日、ユーザーが作成した `Root/ComponentSample0` を深さ5まで読み取りで観測した。型とmemberは実機Reflectionでも確認した。参照実装と共有スライダーへの書き込みは行っていない。

`Drivers` Slotの三つのComponentは、すべてFlux作例と同じスライダーのValueを参照していた。

| Component | 設定と接続 |
| --- | --- |
| LinearMapper1D | SourceMin 0、SourceMax 1、TargetMin 0、TargetMax 4。Targetは参考実装のLight.Intensity |
| LinearColorMapper | SourceMin 0、SourceMax 1、TargetMin黒、TargetMax白。Targetは参考実装のPBS_Metallic.EmissiveColor |
| ValueTextFormatDriver float | Formatは `{0:P0}`。Textは参考実装のTextRenderer.Text |

二つのMapperはClampとAllowReverseMappingがtrue。ReflectionではMapperのTargetとTextFormatDriverのTextがFieldDriveであることも確認した。したがって、出力をDriveする判断と、処理をComponent／Fluxのどちらで表現するかは別の判断である。

| 集計範囲 | Slot数 | Component数 |
| --- | --- | --- |
| 参考実装の機能部分 Drivers | 1 | 3 |
| Flux初稿のモジュール本体とSDKの補助構造 | 18 | 37（うちProtoFluxノード13） |

両行とも入力UIと出力先のランプ・表示を除外した。Component側は作業中のGizmoLinkを除外し、Flux側はノードのビジュアル展開前の完全な観測 `graph-01.json` を使った。現在のグラフは展開用のUI等が増えており、深さ制限のある観測 `graph-02.json` を総数には使っていない。この比較は構造の差で、CPU負荷や通信量の測定ではない。

参考実装全体はルートを含む5 Slot、11 Component（GizmoLinkを除くと10）。入力は外部の既存スライダーを共用しているため、単体で完結したUIの数とは比較しない。

厳密に同じ出力を比較する場合には、色の設定と表示書式をそろえる必要がある。参考実装の色端点はsRGBで、Flux初稿はLinear。参考実装は百分率書式を使い、観測時は `100%`、Flux初稿は明示的に空白を含む `100 %` を生成する。中間色の補間、書式のカルチャー、範囲外入力、逆方向のマッピングの挙動はこの読み取りでは検証していない。これらは参考実装の不具合という指摘ではなく、同等性を評価する際に確認する差分である。

## 採用した判断基準

オレンジの評価では、今回のDriveの選択は合理的。一方、目的に合うComponentがあれば、少ないSlotとComponentで直接実現できる利点がある。Componentの参照関係は後から読みにくくなることがあり、Componentだけに固執して間接的な連鎖を作るとFluxより複雑になる。

生成時には、要求する入出力・状態・イベントを整理し、目的に合う既存ComponentをReflectionで確認する。直接的で読みやすく実現できるならComponentを優先する。Fluxでしか実現できない処理、またはComponentでは回りくどくなる部分にはFluxを使い、必要なら併用する。同じ仕様と同期範囲を保ったうえで、構成量と読みやすさを比較し、選択理由を短く残す。

この方針を `resonite-build`、`resonite-flux`、`resonite-uix` の既存SKILL.mdへ反映した。具体的なComponent候補と比較時の注意はUIXの既存 `interaction-and-migration.md` に追加した。Fluxのガイダンスでは、出力Driveをデータフローの消費者として選べることも明記し、コンパイルに残すためだけのLocalUpdate追加を避けるようにした。

## 検証結果

- `flux check`、`flux build`、`flux validate-manifest`、デプロイ成功。
- 0／0.5／1 の3ケース、各4項目（ライト強度、発光色、表示、ハンドル AnchorMin）が実機で成功。各プローブは元の入力値を復元する。
- プローブ後、入力 0.5、ライト強度 2、Linear 発光色 RGB 0.5、表示 50 % を確認。
- 正面の文字・スライダーと、背面カバーを実機の画像で確認。
- デプロイ後の `diff` は作成・更新・削除すべて0。入力保持を変化させた再適用は未実施。

証跡はローカルの `artifacts/brightness-study/` に保存した。`graph-01.json` が配置後の構造・参照、`test-02.json` が発光色も含む12項目の結果、`front.jpg` と `rear.jpg` が撮影結果。artifacts と state は Git 管理対象外。

## 人間による評価

- [ ] ドラッグして、端点と中間でランプ・表示・ハンドルが追従するか。
- [ ] 入力を保持した場合と、何度も往復させた場合に不自然な動作がないか。
- [ ] 共有入力から三つの出力への分岐と、各フィールドを専有する Drive の意図が読めるか。
- [ ] 強度の上限4、線形の変化、整数の百分率表示は作例として適切か。
- [ ] 二人で操作し、値の一致、同時操作時の競合、途中参加時の再現を確認する。
- [ ] 入力変更後の再適用、保存・再読込、参照消失時の扱いを確認する。

Driveと実装手段の選択に関する人間の所見は上記に記録した。チェック項目の実施結果はまだ明示的に報告されていない。今回は判断ガイダンスを更新し、参考実装の同等性や操作・同期の合否を固定する回帰テストは追加していない。

## 再現手順

リポジトリのルートで実行する。接続先は `resoloop discover --json` でその都度確認し、複数候補なら対象を選択する。以下の `$studyUrl` は選択済み URL に設定する。既存成果物の state が失われている場合は自動採用せず、対象を観測してから復旧する。

```powershell
resoloop flux check examples/flux/BrightnessSlider.pg --project examples/flux --json
resoloop flux build examples/flux/BrightnessSlider.pg --project examples/flux --json
resoloop flux validate-manifest examples/flux/brightness-slider.flux.json --json
resoloop diff examples/brightness-slider.json --state .resoloop/state/brightness-slider.json --brief --url $studyUrl --json
# 差分の対象を確認してから適用する。
resoloop apply examples/brightness-slider.json --state .resoloop/state/brightness-slider.json --brief --url $studyUrl --json
resoloop flux deploy-manifest examples/flux/brightness-slider.flux.json --url $studyUrl --json
# 明示的な実機検証。操作中のユーザーがいない状態で行い、元の値の復元を確認する。
resoloop test examples/brightness-slider.json --state .resoloop/state/brightness-slider.json --probe --yes --brief --url $studyUrl --json
```
