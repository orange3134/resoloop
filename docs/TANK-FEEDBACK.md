# 戦車制作テスト10の評価と改善計画

2026-09-11。対象はglobal CLI `0.1.0-preview.7` による独立制作。根拠は `rloop-test10/test-report.md`、最終apply、階層補正スクリプト、実機検証JSON、no-opログ、失敗時ログ、および実機全景・近景画像。開発側では該当する値変換、strict検証、state照合、scene summaryの実装も確認した。

## 結果と評価

RIVET-10 Badgerは制作・取込を完了し、砲塔／主砲／ハッチの独立した姿勢変更と復元を確認。最終再applyは33 noOps、0 mutations、0 asset imports、diff空。26,032三角形、48,093展開頂点、6mesh／6描画セクション、1材質、3枚の1024画像。画像のRGBA8+mip概算16.0MiB、geometryを含む概算18.50MiBであり、実測GPUメモリやFPSではない。

これは経路の成功であり、仕様の全面達成ではない。色空間指定不能のためPBRに制約があり、保存後の再spawnは未検証。左右足回りは独立assemblyで、履板や車輪個別の走行animationには追加分離が必要。ハッチのピボットは動くが、開口・内部空間はない。

ユーザーは海賊船・戦車とも最適化を強く意識しすぎた見た目と評価し、よりハイポリで造形を作り込むことを許容した。写真からも主形状は読みやすい一方、砲塔・防盾・足回りの接合、奥行きある機構、部位固有の表面表現には追加の余地がある。初稿47,664→26,032三角形の削減は記録されているが、実測負荷に基づく削減ではない。旧版画像との統一条件比較はないので、その削減だけが見た目の原因とは断定しない。共有atlas、少ない材質、造形設計、暗めの既存照明も分けて評価する。単純な細分割だけでは情報量やデザインは改善しない。

## 制作品質の指示変更（実施済み）

`resonite-blender` と `resonite-build` の制作指示を変更した。

- 旧「小物2–10k三角形、1–3材質、512–1024画像」の数値目安を外した。大型の船、車両、ランドマークを小物予算に寄せない。
- 要求する造形・素材品質を先に作り、実際の距離・個数・負荷に応じて最適化する。
- 近景の視差、影、隙間、継手、縁、表面の段差に効く形状はgeometryで作り込める。遠景シルエットに影響しないことだけを理由にmapへ追い出さない。
- 単一材質／小さな共通atlasを目標にせず、固有の汚れ・素材差・texel密度に必要な画像と材質を使う。
- 50k等の既存export警告は確認のきっかけであり、削減命令・上限ではない。数字を丸めたり警告を消したりするための自動的な削減を避ける。
- 造形と見た目を比較してから不可視・重複などの無駄を削る。高精細な原本を残す。VRへの配慮、不要な高解像度レンダリングの省略、実機確認は維持する。

計画作成時は進行中の時計塔へ最新の品質方針だけを通知し、global preview.7とtest10の証拠は維持した。その後、ユーザーの実装依頼により時計塔の結果を統合し、以下のCLI項目を実装した。[統合計画・実装結果](CLOCKTOWER-FEEDBACK.md)が現在の状態を示す。各テスト制作の原本と完成モデルは変更していない。

## CLI改善の優先順位

| 優先 | 問題と根拠 | 実装方針 | 完了条件 |
| --- | --- | --- | --- |
| P1 | `PreferredProfile: Linear` がstrict成功後に `VALUE_TYPE_UNSUPPORTED`。9asset取込・7Slot・3Component作成後に停止。単独setも同じ失敗 | adapterでNullableの実際の型表現を正しく分解し、内部enumのReflectionと公開nullable enum wrapperを使って値／nullを送る。strictとapplyが同じ書込み可否判定を利用するようIResoniteClient経由で接続する | enum名・数値・null・不正値、Nullable scalarの回帰。未対応値はimport／mutation前に検出。専用SlotでLinearを書込み・readbackし、中間値を持つdata mapが意図した色空間になることを確認 |
| P1 | 途中checkpointで同型StaticTexture2D 2件が `STABLE_COMPONENT_AMBIGUOUS`。manifestへidentity追加しても旧stateは復旧せず、新stateへのadoptが必要 | exporterのprovider識別を作成直後から成立させる。名前付きprovider Slotへの分離を第一候補として互換性・Slot増加を評価し、旧bundle/stateを無断移行しない。checkpointのidentity成立時点と再解決を見直し、根拠のないID／順序fallbackを追加しない。既存stateへidentityを足す場合の診断・明示復旧手順を整備 | 同型provider作成後・参照結線前・field更新途中に障害を注入し、同じstateで再接続・再実行して重複なしに完了。不変identity未設定、途中identity、真の曖昧性、session再起動も試験。曖昧な対象は安全に停止 |
| P2 | 静的exportがworld頂点へ焼込み、階層／pivotを平坦化。戦車側で独自Pythonによる頂点rebaseと親子再構成が必要 | 既定flattenの互換性を保ち、明示的な階層保持モードを設計。local geometry、親子transform、object originを保持する。任意shear等TRSで表せないものは診断し、黙って見た目を変えない | 多段親子、回転、非一様scale、鏡映、軸変換、UV／normal／tangentを回帰。砲塔yaw・砲身pitch・ハッチhingeが正しい中心で動き、元姿勢へ復元可能 |
| P2 | 直結Roughness／Metallic画像にもexport用blendと手動packingが必要。二値metallicの回避は完全なPBR移植ではない | P1の色空間書込み修正後、対応する直結画像の明示的な自動packingを追加。PBS_MetallicのR=metallic／A=smoothnessを生成し、data mapを線形で接続。異なるUV・寸法・sampling、非対応nodeは明示診断 | 0/1以外のmetallicとroughnessを含む既知pixelで検証。入力色空間、寸法、normal保持、mipと実機素材差、再export／applyの安定性を確認 |
| P2 | scene summaryのboundsが外形でなくpivot範囲。実測meshのmin/maxと大きく異なった | まずboundsの根拠と精度を出力する（pivots／known geometry／partial等）。次にローカルmesh sourceまたはhash整合したexport metadataからboundsを取得し、親transformを適用。不明な外部URLを実外形と表示しない | 砲身・アンテナの張出し、多段transform、既知／未知mesh混在、source変更を試験。カメラ配置や占有範囲に誤使用されない表示 |
| P3 | 未使用のpacked画像がblend再open時に消えた。CLIの画像取込失敗ではなく自作datablock保持の問題 | Blenderスキルに「packは未使用datablockの保持を保証しない。必要な未接続原本はfake user等で保持し再open確認」の短い手順を追加 | 使用画像と未使用原本が保存／再読込後に存在しpixel内容が保持される |

### 実装の根拠と順序

`ResoniteLinkClientAdapter.ParseEnumOrReflection` は現在Nullableをunwrapせずenum定義を要求している。`TryParseReflectedField` のgeneric切出しも `Nullable<><T>` を専用に扱っていない。一方、読取りには `Field_Nullable_Enum` の分岐が存在する。これらを統一する。ただしwire modelはpinned公開APIで確認し、CoreへLink固有型を入れない。

`ApplyDocumentValidator` のstrict処理はメンバーの存在・参照・tuple等を確認するが、全fieldのadapter変換を通していない。型メタデータが読めることと、その値を書き込めることを区別してpreflightを整える。

state照合は保存されたidentity／member／参照構造を使い、manifestの追記だけでは古いcheckpointの根拠が増えない。現在の「identityFieldsを追加」のsuggestionだけでは不足する。元stateを保持して明示復旧した戦車の手順を再現ケースとし、最初からadoptに頼る設計にはしない。

順序は **値変換とpreflight → 中断復旧 → 階層保持 → PBR packing → bounds**。品質指示の変更は先行実施済み。exportの数値警告をasset単位の設定可能な予算へ変更する案は、その後の制作テスト結果で判断する。新しい一律の高い数値上限を導入するだけでは同じ誘導が残る。

## 次回の評価

ユーザーの方針により、FPS計測はモデル単体の評価項目から除外する。ResoniteのセッションFPSは人数・他アイテム・世界内の編集や実行処理に左右され、制作したモデルだけに帰属できない。過去の「FPS未検証」は未完了事項ではなく対象外として扱う。モデル自身の三角形数・展開頂点・mesh／材質／描画セクション・画像寸法／概算容量・灯や駆動Componentを記録し、見た目・構造・要求された動作を確認する。構造上の資源数を実測フレーム負荷と呼ばない。

同じbriefと閲覧距離で、高精細な造形を先に作った版を評価する。形状、接合、材質差、固有の摩耗、コンセプトへの一致を画像で確認し、統計は別欄に記録する。最適化前後を比較できる原本を残す。低い三角形数や1材質化を成功指標にしない。時計塔の新方針での結果も参考にするが、進行中の担当へ開発コードや既知不具合の解法を流し込まない。

完成戦車や海賊船の再制作、世界への修正適用、NuGet公開は今回の計画整理には含めない。外部shader依存を自動許可してauditを成功にする変更も行わない。
