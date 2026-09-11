# 鍛冶屋ワールド test12 — 評価と改善

2026-09-12。test12のtest-report.md、resource-report.md、制作Python、再構築比較記録、コンセプト内観と実機の内外観を確認した。

## 結果と原因

コンセプト3案→清書5枚→ブロックアウト→内外装・設備・小物・環境→実機修正まで実施。最終105 Slot / 128 Component / 41 asset、再applyは作成・更新・importが全て0。材質接続、16 collider、12局所灯、結晶の時間変化を実値で確認したと報告されている。手動歩行・保存後再spawnは未確認。火の粉は静的造形であり、煙・熱揺らぎ・音は未実装。FPSは検証対象外。

約96.8万三角形でも、コンセプトにあった高い吹き抜け、壁面の密度、固有の彫金や素材の細かな変化までは再現されていない。実機画像では天井が低く、反復する石や平滑な武具が目立つ。これはimportの成否とは別の美術上の差である。

床146,640 + 屋根164,124 + 主要石壁148,520 = 459,284三角形（全体の約47.4%）。制作の共通reg関数は多数の部品へ3分割のBevelを一律追加していた。ただしこの集計は部材全体の数であり、全てをベベルの三角形数と断定しない。ユーザー指摘に基づきベベルへ予算を割く指示は既に削除した。品質を一律のポリゴン上限で抑える方針へ戻す必要はない。

## 今回の実装計画

| 優先 | 改善 | 完了条件 |
| --- | --- | --- |
| P1 | スキルの美術レビュー | コンセプトの空間比率・見せ場・素材をブロックアウトと主要アセット段階で実機比較し、差を修正または明示する。ポリゴン総数や部品の列挙で完成としない |
| P1 | Slot fieldの宣言参照 | `$slot-member:slotKey.Rotation`などを追加。Componentの`$member`と区別し、宣言・diff・apply・再接続で観測したfield IDへ解決。未知のキー/フィールドは書込み前に拒否 |
| P1・実機検証で追加 | ID指定の親のstate path | 親IDをパス文字列に埋め込む不具合を修正。親の名前とParent IDをRootまで有限回観測して実パスを保存し、再接続でもSlot fieldを解決する |
| P2 | 初期観測の量 | `hierarchy --summary`でSlot ID/nameと任意のComponent ID/typeだけを表示し、reference-only境界を保持。`--under`で対象を限定。互換性のため既定出力は維持 |
| P2 | Blender制作効率・保存 | 大量のoperator逐次呼出しを避ける判断材料、未使用materialの保持、追加meshごとのUV確認をスキルへ補足 |
| P2 | 再生成差の切り分け | 同じ保存済みblendを別プロセスでexportする回帰を追加。Pythonからの再生成差と同一manifest再applyのno-opを分けて記録 |
| 配布 | ローカルpreview更新 | 検証後にpreview.9へ更新し、このPCのCLIへ同梱スキルを反映。NuGetへの公開は行わない |

再生成差は位置・法線一致、UV最大差約1.91e-6、tangent最大差約8.46e-4、展開頂点37増、17 meshが再import候補。UV自体が異なる入力に対する差を、exporterだけの非決定性と断定できない。任意のUV/接線の丸めや近似hashで変更を無視する処理は今回導入しない。UV seam・法線・細かな意図した編集を失う危険があり、再現可能な同一入力での失敗が先に必要。

完成した鍛冶屋ワールドと制作原本は変更しない。実機テストは独立した一時Slotのみを使用し、finallyでID・名前を再観測して同一Slotを削除する。

## 検証結果

- 通常テスト192件成功。solutionのintegration9項目は通常実行ではopt-in guardにより実機処理を行わない。
- Blender 5.2.1 LTSで15件成功。同じ保存済みfixtureを別々のBlenderプロセスから出力してmanifest/mesh bytes一致、UV seam保持を確認。これは任意の制作Pythonやmodifierの再生成をbyte決定的と保証する結果ではない。
- port34409の専用一時Slotで実機テスト1件成功。公開ReflectionでSpinnerを確認し、別の子SlotのRotationへの明示接続、再接続を想定したpath再解決、再applyの0更新、内部field所有判定を確認。既存のNullable enum/nested回帰も同時に成功。一時Slotはfinallyで名前・IDを確認して削除。
- 親をraw IDで指定した実機テストが最初は再接続パス解決で失敗した。親の実パスを観測する修正後に成功。古いstateの誤ったpathや曖昧なIDを無条件に信頼する移行処理は追加していない。
- 完成鍛冶屋を読み取り専用で`hierarchy --under Root/ResoLoop_Test_BlacksmithWorld_Test12 --depth 1 --include-components --summary`観測。型/IDとreference-onlyの境界を残し、Slot/Component field payloadを出さない。証拠は開発側`artifacts/feedback/preview9/hierarchy-summary.json`。
- 改訂したbuild/blender/inspectの3スキルはquick_validate成功。ベベルへの割当を促す文言は削除済み、FPSは引き続き対象外。
- ローカル`ResoLoop.0.1.0-preview.9.nupkg`を作成し、このPCのglobal toolをpreview.8→preview.9へ更新。`--version`、help、新規initでの改訂スキル同梱を確認した。NuGetには公開していない。既存制作プロジェクトの生成済みスキルは自動上書きしていない。

成果物の検証済み範囲を越えて、鍛冶屋そのものの美術品質改善や手動歩行・保存再spawnまで完了したとは扱わない。今回改善したのはCLIと今後の制作を導くスキルである。
