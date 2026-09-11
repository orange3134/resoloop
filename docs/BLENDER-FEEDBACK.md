# rloop-test9 制作テストからの改善計画

根拠は `rloop-test9/test-report.md`、制作／準備スクリプト、監査JSON、Resoniteの全景・砲近景。preview.6の独立制作で118,542三角形、8材質・描画セクション、10画像の海賊船が完成し、再適用0変更・0再importを確認した。大規模な静的モデルの経路は成立したが、以下の回避作業が必要だった。

| 優先 | 観測と原因 | 改善 | 検証条件 |
| --- | --- | --- | --- |
| P1 | n-gonでcalc_tangents失敗。calc_loop_trianglesはポリゴンそのものを三角形にしない | 評価済みメッシュから一時的な三角形メッシュを作り、UV・corner normal・色・materialを保持して接線計算 | UV付きn-gon、custom normals、複数UV・材質、元データ不変をBlenderで確認 |
| P1 | Image.copyはpixel bufferを複製せず、生成／編集画像が空になる場合がある | 元bufferのsave_copyを使用。欠損時は保存済みファイルから限定的に回復し、画像・パスを含む対処可能なエラー | 未pack生成画像、dirty file画像、保存／再起動、Non-Color、欠損画像 |
| P2 | auditのroleが単純型名だけに一致し、完全修飾型名やIDが黙って不一致 | 単純名・完全修飾名・正確なcomponent IDを明示的に扱い、候補と未使用指定をreportへ追加 | 誤指定を通知、異なる型への過剰許可を防止、既存strict判定を維持 |
| P2 | 色空間enumのassembly指定が欠けて検索失敗 | type describe --memberからReflectionのvalueTypeへ接続しNullableを解除。型エラーとhelpを改善 | StaticTexture2D.PreferredProfileの実機enum値、既存component describeの互換性 |
| P2 | 制作途中の画像準備、近景修正、最終report更新に手探りがあった | スキルに保存後の再読込確認、3段階の視覚確認、スムーズ法線と意図的なhard edge、最終apply変更後の資源再集計を追加 | skill検証、最小例＋保存済み制作.blendのオフライン再export |

完成船の再モデリング、無条件の標準shader許可、FPS保証、GLB/FBXやアニメーション対応は今回の対象外。近景の手続き模様・装飾の簡略化は美術品質の課題であり、import成功や三角形数でAAA相当と認定しない。完成した世界コンテンツは変更せず、実機mutationが必要な場合だけ専用の一時Slotで確認する。NuGet公開は行わない。

## 実装と検証結果（2026-09-11）

上表の改善を実装し、このPCのglobal toolを `0.1.0-preview.7` に更新した。パッケージはローカルでpackしてローカルfeedから導入し、NuGetには公開していない。新規initの同梱スキルにも改訂を反映した。既存制作プロジェクトのスキルは自動変更せず、必要なプロジェクトで `resoloop skills sync --check` → `resoloop skills sync --update` を使用する。

- Debug build: 警告0、エラー0。通常の単体テスト168件成功。solution内のintegration項目は通常実行ではopt-in guardにより実機処理をしない。
- Blender 5.2.1 LTSで11件成功。n-gon、custom normal、複数UV、corner color、鏡映／非一様scale、material順、Non-Color PNG、未pack生成pixel、dirty fileの最新pixel、保存後のblend再open、欠損画像の診断を検証した。
- 回避処理前の `rloop-test9/artifacts/Crowned_Reaver.blend` を新しい出力先 `artifacts/feedback/pirate-unprepared` へ直接export成功。118,542三角形、329,476展開頂点、8mesh・8材質・8描画セクション、9画像。元blendと完成船は変更していない。頂点数と画像数が最終船と異なるのは、法線調整前であり、最終applyへ追加したpacked PBR画像を含まないため。元データの自動最適化や最終納品物との同一性は主張しない。
- port34409で専用 `ResoLoop_Test_Blender_*` SlotのBlender制作→export→import→再applyを1件実行し成功。再applyでasset再import、Component更新／追加、Slot作成が0。finallyで同じSlot名とIDを再確認して削除した。
- `StaticTexture2D --member PreferredProfile` から `[Renderite.Shared]Renderite.Shared.ColorProfile` と `Linear=0, sRGB=1, sRGBAlpha=2` を取得。インストール済みpreview.7でも確認した。値はこの実行環境の観測であり、他バージョンへ固定値を推測して適用しない。
- 完成船を読み取り専用で監査し、完全修飾 `PBS_Metallic:_shader` roleが8参照に一致。未使用roleなし。Grabbableなしのwarningは残り、非strictのportable=trueを保存・再spawn確認の代用にしていない。誤namespace／assembly／ID／member、IDの大小文字差、未使用指定でのstrict失敗は単体テストで検証した。
- 変更した4スキルのquick_validate成功。インストール済みpreview.7の新規initで5スキル配置を確認。Release pack成功。

今後の制作テストでは、保存後の再spawn、近景の木目・摩耗表現、モデル自身の資源量を別の評価項目として記録する。ユーザーの方針により、人数や他のアイテムの影響を分離できないセッションFPSはモデル単体の評価対象外とし、検証待ちにしない。今回のCLI修正で未実施のマルチプレイヤー・航行操作やAAA相当の美術品質を保証しない。
