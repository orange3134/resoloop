# 時計塔テスト11の評価・統合改善計画

2026-09-11。test11のレポート、採用コンセプト、最終全景・近景、動作と回避処理の記録を戦車の計画と照合した。

最終時計塔は126,522三角形、123,196展開頂点、5材質・10描画セクション。回転4部位、ピストンの往復、局所光のパルスを実値で確認し、再applyは56 noOps／変更0／import0。初稿の低い三角形数を目標にせず造形を増やし、smooth normal調整で形状を維持して頂点を減らした点は新方針に合う。一方、コンセプトの彫刻・密集機構・固有の摩耗は十分再現されておらず、126kという数字だけで美術品質の達成を判断しない。FPSは評価対象外。

## 戦車計画からの変更

| 優先 | 項目 | 対応と検証 |
| --- | --- | --- |
| P1・継続 | Nullable enum／書込みpreflight | strictと実書込みの変換を揃え、失敗をasset import前に検出。Linear/null/readbackを確認 |
| P1・追加 | nested SyncObjectの偽差分 | 指定した子メンバーのみを再帰比較し、referenceと値を正規化。省略した既定値は無視、指定値のdriftは検知。initialFieldsへの逃避を不要にする |
| P1・追加 | Slot fieldの所有判定 | 公開APIからSlot member IDを保持・inspectへ表示し、監査の所有集合へ含める。内部の回転／位置FieldDriveを内部参照と判定し、本当の外部参照は残す |
| P1・継続 | 中断checkpoint復旧 | 作成時点から識別可能なprovider配置と、途中失敗／再接続の回帰。旧stateを根拠なくadoptしない |
| P2・両テスト共通 | 階層／pivot保持 | 互換性を保つopt-in export。親子transformと可動中心を保持し、独自頂点rebaseを不要にする |
| P2・継続 | PBR packing | 直結metallic／roughness画像を明示オプションでpackingし線形profileへ接続。任意node graph対応には広げない |
| P2・両テスト共通 | bounds | ローカルmeshを参照して外形を計算。不明な場合はpivot由来／部分的であることを明示 |
| P2・追加 | Slot vector入力 | JSON配列／objectと従来comma形式を共通化し、有限値・要素数を検査 |
| P2・追加 | capture所有境界 | 実際の一時Slot親・IDとcleanup結果を結果に含める。親指定を増やす場合の座標変換は別途設計し、まず現状のRoot配下exact cleanupを可視化 |
| P3・文書 | 動作例／画像保持 | Panner3DのtargetとRelativePositioner基準空間をReflectionで確認する手順、未使用packed画像の保持をスキルへ補足 |

実装順は書込みと再適用・監査の正しさを先行し、export改善とbounds、文書・配布を続ける。戦車計画の数値警告を設定可能にする案は引き続き将来候補で、今回は品質優先のスキル指示を維持する。完成船・戦車・時計塔の変更やFPS計測は行わない。実機mutationは専用の一時Slotで行い、再観測後に同一Slotだけを清掃する。NuGet公開は行わない。

## 実装結果 — 0.1.0-preview.8

上表の項目を実装した。Nullable enumは公開`Field_Nullable_Enum`を使用し、strictの事前変換をadapter内の実書込みと共有。Slotの公開field IDをCoreの汎用member情報へ保持し、監査の内部所有集合へ含めた。nested SyncObjectは指定した子のみを再帰比較し、list内の入れ子も同じ規則で照合する。数値型表現の差を正規化し、dictionaryは余分な要素も差分として扱う。

providerの再識別は別の名前付きSlotを既定とし、旧出力は`--legacy-root-providers`で維持する。旧checkpointの曖昧性は勝手に解消せず、候補確認と旧stateの識別根拠が不足している旨を診断する。`--preserve-hierarchy`、`--pack-pbr`を追加し、任意shader graph／animation／shearの黙示変換は行わない。boundsは現在のローカルmeshファイルから計算し、取得不可時は精度を明示する。captureは親指定を追加せず、現行の一時Slotとcleanup結果を可視化した。

### 検証

- ビルド警告0／エラー0、通常単体テスト185件成功。solutionのintegration9項目は通常実行ではopt-in guardにより実機処理を行わない。
- Blender 5.2.1 LTSで14件成功。n-gon／画像回帰に加え、階層pivot・非一様scale・鏡映の法線／表裏、provider配置とlegacy配置、PBR中間値0.25とsmoothness 0.3、画像寸法不一致の明示失敗を確認。
- port34409の専用一時Slotで実機テスト2件成功。Nullable enumのLinear／null／数値0の書込みreadback、不正enumのmutation前失敗、nested再applyの0更新、内部FieldDrive監査を確認。Blender最小propのimportと再apply0変更・0再import、640×480 captureとcleanup結果も検証し、画像を視認した。一時SlotはfinallyでID・名前を再観測して削除。
- 完成時計塔を変更せず監査。以前externalだった内部FieldDrive5件が内部へ移り、internal31／external5。残る5件は共有shaderであり自動許可していない。Grabbableなし警告と保存再spawnの未検証は維持。
- 完成時計塔の宣言＋ローカルmeshからboundsを再計算し、min[30.40,0,39.92]／max[40.24,16.18,49.05]、kind=geometry。test11がmeshから求めたrest外形と一致。
- 戦車の編集用`artifacts/tank.blend`を`--preserve-hierarchy --pack-pbr`で直接export成功。26,032三角形・48,093頂点・6mesh・1材質・3画像を保ち、Linear packed providerと親子／pivotを生成。旧adapt_bundle.pyを使わない出力を開発側`artifacts/feedback/tank-native`に保存。
- 時計塔の編集用blendも`--preserve-hierarchy`で直接exportし、126,522三角形・123,196頂点・5材質・10sectionsを維持。開発側`artifacts/feedback/clocktower-native`へ保存。両原本と完成世界コンテンツは変更していない。既存のnative動作追加まで自動変換したわけではない。
- 名前付きproviderの作成途中3地点で中断と再接続を模擬し、同じstateで重複なしに収束。nested参照の同値no-opと実drift検知、真の外部Slot fieldの拒否、boundsが取得できない場合、JSON／commaのvectorを回帰検証。
- 改訂した4スキルのquick_validate成功。品質優先・FPS対象外の方針を保持。

公開API根拠: [pinned enum wrappers](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/DataModel/Fields/Field_Enum.cs)、[pinned Slot model](https://github.com/Yellow-Dog-Man/ResoniteLink/blob/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/DataModel/Slot.cs)。Link固有モデルはadapter内に留めている。
