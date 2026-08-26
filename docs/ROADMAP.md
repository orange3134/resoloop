# rloop development roadmap

このロードマップは、小さな家ワールドを `rloop apply` で実際に構築した結果を基準にしています。優先順位は、短い反復時間、安全に再実行できること、結果を自動検証できること、表現力の順です。

## 実践で確認できたこと

- nested Slot、Component、List、`colorX`、Component/Member参照を含む宣言から、69 Slot・148 Component規模のワールドを構築できた。
- 同じ宣言の再適用に使える基本的な収束動作と、同名Slotの曖昧性を拒否する安全策は機能した。
- 鏡のButtonToggleについて、対象ComponentとMember参照が正しく接続されたことは構造上確認できた。
- 旧実装の初回適用には十数分を要した。P0実装後の同じfixtureは9.45秒、変更なし再適用は1.83秒・Link request 10件・world mutation 0件になった（2026-08-26、同一のローカル環境）。
- applyは進捗、事前plan、request/command timeout、checkpoint再開を備え、長い処理の現在位置と停止後の状態を報告できる。
- stable keyとstate fileにより、複数の同型Component、明示keyを持つSlotのrename、別接続セッションからの再解決が可能になった。
- 大量の家具や建材を素のJSONで列挙する必要があり、再利用、反復、パラメータ化、ファイル分割が足りない。

## P0: 実用的な反復ループ

### 1. applyの高速化と計測

- [x] Link request数、処理時間、cache hitを計測できる `--profile` を追加する。
- [x] Component definition、Slot snapshot、参照解決結果をapply単位でcacheする。
- [x] 公開APIで可能な範囲でField更新と読み戻しを集約し、不要な再取得を除く。
- [x] house-world fixtureを性能回帰テストにし、同一環境で初回applyを現状比5倍以上高速化する。
- [x] 2回目の変更なしapplyでは書き込みを行わず、request数と所要時間を記録する。

完了条件: どの処理に時間を使ったかJSONで説明でき、house-worldの反復が分単位ではなく実用的な待ち時間になること。

### 2. timeout、cancel、進捗表示

- [x] 接続時だけでなく各Link requestとcommand全体にtimeoutを適用する。
- [x] Ctrl+Cを安全に伝播し、完了済みの対象と未処理の対象を報告する。
- [x] `apply` の検証・作成・更新・参照解決を、件数と対象path付きでstderrへ逐次表示する。
- [x] `--json` 利用時はstdoutの最終結果を壊さず、必要に応じてNDJSON progressを選択できるようにする。

完了条件: hangが無期限に続かず、利用者が現在位置、残量、停止後の状態を判断できること。

### 3. mutation前のvalidateとplan

- [x] `rloop validate <file>` で、接続なしにschema、値形状、key重複、未解決参照を検出する。
- [x] 接続先のReflection結果も使うstrict validation modeを追加する。
- [x] `rloop plan <file>` でcreate/update/no-op/errorをmutation前に列挙する。
- [x] forward referenceを許可し、全参照を解決できてからmutationを開始する。
- [x] schema versionを必須化し、互換性のない入力を明示的に拒否する。

完了条件: 型違い、参照ミス、曖昧な対象による失敗は、原則としてワールドを変更する前に判明すること。

### 4. 永続identity、再開、所有境界

- [x] Slot/Componentのstable keyをstate fileでセッションを越えて保持する。
- [x] 複数の同型Componentをkeyとtype ordinalで一意に更新できるようにする。
- [x] セッション変更を検出し、古いIDを拒否してkey/path/type ordinalから再解決する。
- [x] 中断後に完了済み操作を再利用して再開できるcheckpointを追加する。
- [x] rloopが所有するroot境界を宣言し、未確認の既存rootは明示的 `--adopt` なしに更新しない。

完了条件: 同じ宣言を別セッションから安全に再適用でき、途中失敗後も重複生成せずに再開できること。

## P1: 検証可能なコンテンツ開発

### 5. 見た目と空間の検証

- [ ] 明示的なcamera、解像度、保存先を指定する `rloop capture` を、利用可能な公開APIだけで実装または連携する。
- [ ] Slot群のworld bounds、配置、欠落material、無効参照を検査するscene summaryを追加する。
- [ ] camera bookmarkと代表viewをmanifestで宣言できるようにする。
- [ ] screenshotとscene summaryを成果物としてCIから比較できる形式にする。

### 6. ギミックの動作検証

- [ ] Field/Referenceを読むassertionと、変更前後を監視する `rloop test` の仕様を作る。
- [ ] 公開APIで安全に可能な場合だけ、Button pressなどのinteraction probeを追加する。
- [ ] 鏡の例をfixture化し、ButtonToggleのtarget/member wiringとon/off結果を検証する。
- [ ] runtime eventを呼び出せない環境では、構造検証までであることを明確に報告する。

### 7. 安全な差分収束

- [ ] `rloop diff` でcreate/update/rename/delete候補と理由をJSON化する。
- [ ] stable keyによるrenameを実装し、削除して作り直す挙動を避ける。
- [ ] 削除は所有境界内だけを対象にし、previewと明示的 `--yes` を必須にする。
- [ ] 失敗時のrollback可否を操作単位で示し、非atomicな場合は復旧手順を出す。

## P1: 宣言形式の表現力

### 8. 再利用とファイル分割

- [ ] include、parameter、variableを追加し、共通materialや寸法を一元化する。
- [ ] prefab/prototypeとinstance、配列・格子向けのrepeatを追加する。
- [ ] `$slot:key`、Component、Member、assetを統一的に扱う参照構文を設計する。
- [ ] 循環include、key衝突、展開後サイズに上限を設ける。
- [ ] house-world.jsonを複数ファイルと再利用部品で書き直し、宣言量の削減を測る。

### 9. 型とassetの対応範囲

- [x] 対応済みscalar/reference要素を持つListの一括書き込み
- [x] `colorX` とnamed floating-point値のJSON処理
- [ ] Listの差分更新、Dictionary、SyncObjectの読み書き
- [ ] generic Componentの型指定とReflectionによる検証
- [ ] asset URI、enum flags、nullable、nested valueの境界テスト
- [ ] material、texture、meshなどのasset import/参照ライフサイクル

## P1: ProtoFlux反復開発

- [ ] `flux watch` の成功buildだけを、検証済みparentへ安全に再deployする。
- [ ] module manifest、複数module、依存順序を扱う。
- [ ] deploy前後の構造差分と、失敗時の復旧方針を表示する。
- [ ] Flux-SDK version互換性テストと代表的なgraph fixtureを追加する。
- [ ] world宣言のstable keyとProtoFlux参照を統合する。

## P2: 導入、配布、運用

### 10. versioned release

- [ ] immutableなversion付きdotnet tool packageを発行する。
- [ ] Windows CIでbuild、offline test、package install smoke testを行う。
- [ ] opt-in live test jobを用意し、必ず専用 `RLoop_Test*` 配下だけを扱う。
- [ ] `rloop init` templateとCLI/schemaのversion互換性を診断する。
- [ ] changelog、upgrade guide、release automation、署名方針を整備する。

### 11. 診断と観測性

- [ ] `doctor --json` にautomation向けのstrict exit codeを追加する。
- [ ] Resonite log pathを安全に検出し、設定・version・直近エラーをdiagnostic bundleへまとめる。
- [ ] Link event/log streamingを利用可能な公開APIだけで評価する。
- [ ] request ID、対象key/path、elapsed timeを相関できるstructured logを追加する。
- [ ] ResoniteLink Beta / Flux-SDK更新時のadapter契約テストを追加する。

## 実装順

1. 計測を先に入れ、request cacheと不要な読み戻し削減でapply時間を短縮する。
2. timeout、cancel、progressを入れ、長い処理を制御可能にする。
3. validate/planを実装し、mutation前に入力全体を確定する。
4. stable identity、checkpoint、所有境界を導入して安全な再適用を完成させる。
5. screenshot/scene assertion/interaction probeで結果の検証ループを閉じる。
6. 再利用構文、型・asset、ProtoFlux、配布基盤を順次広げる。

P0は完了しました。削除収束とrollbackはP1のため、引き続きrloopが管理する専用の `RLoop_Test*` Slotから導入し、`validate` と `plan` を先に実行する運用を推奨します。
