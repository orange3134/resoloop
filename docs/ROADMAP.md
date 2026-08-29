# rloop development roadmap

このロードマップは、小さな家ワールドとブロック崩しを `rloop apply` / Flux deployで実際に構築した結果を基準にしています。優先順位は、差分の収束、安全に再実行できること、結果を自動検証できること、反復時の観測量、表現力の順です。

## 2026-08-28 ブロック崩しフィードバックの改善計画

### 今回実装

- [x] SyncObjectを要素に持つListを子member込みの構造値へ正規化し、宣言値と一致する場合に差分を収束させる。実worldの `Slider.SnapPositions` で偽差分が消えることを確認した。
- [x] plan/diffのJSONに `changes` を常設し、`--changes-only`、`--creates-only`、`--deletes-only`、`--summary` を追加する。
- [x] `find --under` / `--direct-children` / `--exclude-reference-only` と、`inspect --component` / `--member` / `--components-only` を追加する。
- [x] Flux-SDKの位置付き診断を配列化し、stdout/stderr channel、parse/type/cascade分類、`primaryDiagnostics` を返す。
- [x] statusのIDを `connectionId` として返し、安定world identityではないことを明示する。
- [x] assembly付きopen genericからclosed generic名を作る `type specialize` と、事故に関係するサブコマンドhelpを追加する。

### 次期P0の進捗

Flux manifest binding:

- [x] pinned Flux-SDK 1.9.0の公開 `DeployTarget.InputMap` / `OutputMap` と生成Slot・Component構造を確認する。
- [x] moduleの `in` / `out` 名を `source` / `drive` として宣言し、`$slot:key`、`$component:key`、`$member:key.MemberName`へ接続するmanifest schemaを追加する。global/elementの生成方式はProtoGraph宣言に従う。
- [x] world stateのkey・path・type ordinalから接続先IDを再解決し、Flux-SDKのInputMap/OutputMapへ渡す。解決先IDが変わった場合もhashを更新して再deployする。
- [x] 未解決binding、manifestと解決結果の不一致、member以外を対象にしたdriveをdeploy前の構造化エラーとして拒否する。
- [x] オフライン契約テストに加え、専用 `RLoop_Test*` live fixtureで `Slot element` の正常deployとglobal referenceのtarget配線を確認する。
- [ ] Flux-SDK 1.9.0が `IButton global` の参照配線後に返す `Invalid component type` を解消し、PhysicalButtonへのglobal bindingをmodule全体の成功として完走させる。
- [ ] Flux入出力の宣言型とworld targetの型互換性、未結線状態をdeploy前後に検証して構造化エラーにする。

Safe interaction probe:

- [x] `safe: true` とCLIの `--probe --yes` を必須にし、無許可probeを拒否する。
- [x] fieldを一時変更し、after assertionをpollした後、成功・失敗・cancel時に元値を復元して再読取確認する `set-member` transactional probeを追加する。
- [x] オフラインで成功時・assertion失敗時の復元を検証し、専用live fixtureでも一時変更と復元を確認する。
- [ ] Dynamic Impulseとvalue/reference付きDynamic Impulseを、一度限り・明示許可付きで送信するprobeを追加する。
- [ ] ProtoFluxのCallInputをReflectionで能力確認したうえで、安全に一度だけ起動するprobeを追加する。

### 次の実装候補

- [ ] Flux deployの返却値と実module childを分離し、`parentSlotId`、`moduleSlotIdBefore`、`moduleSlotIdAfter` を再観測結果から返す。
- [ ] `managedFields` / transform preservation、stable key migration、親Slot単位の安全なprune最適化を設計する。
- [ ] doctorで「managed-data未設定」と「Flux-SDKの自動発見成功/解決不能」をbuild probeにより区別する。

Flux bindingとinteraction probeは、型名・member名・メッセージを推測するとworldを壊す領域なので、pinned upstream確認、オフライン契約テスト、専用 `RLoop_Test*` live fixtureの順で進める。

## 実践で確認できたこと

- nested Slot、Component、List、`colorX`、Component/Member参照を含む宣言から、69 Slot・148 Component規模のワールドを構築できた。
- 同じ宣言の再適用に使える基本的な収束動作と、同名Slotの曖昧性を拒否する安全策は機能した。
- 鏡のButtonToggleについて、対象ComponentとMember参照が正しく接続されたことは構造上確認できた。
- 旧実装の初回適用には十数分を要した。P0実装後の同じfixtureは9.45秒、変更なし再適用は1.83秒・Link request 10件・world mutation 0件になった（2026-08-26、同一のローカル環境）。
- applyは進捗、事前plan、request/command timeout、checkpoint再開を備え、長い処理の現在位置と停止後の状態を報告できる。
- stable keyとstate fileにより、複数の同型Component、明示keyを持つSlotのrename、別接続セッションからの再解決が可能になった。
- P1実装後、house fixtureはincludeで3ファイルへ分割し、22個のboxと4本のtable legをprototype化した。69 Slot・148 Componentを維持し、宣言量は46,178 byteから42,430 byteへ8.1%減った。
- ResoniteLink 0.13.1にはscreenshot/event stream APIがないため、見た目は決定的なcamera-space SVG、interactionは公開SyncMethodがある場合だけprobeし、それ以外をstructural-onlyとして機械判定する方針にした。

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

- [x] 明示的なcamera、解像度、保存先を指定する `rloop capture` を、利用可能な公開APIだけで実装または連携する（0.13.1では決定的SVG投影、`screenshotAvailable: false`を明示）。
- [x] Slot群のworld bounds、配置、欠落material、無効参照を検査するscene summaryを追加する。
- [x] camera bookmarkと代表viewをmanifestで宣言できるようにする。
- [x] capture SVGとscene summary JSONを成果物としてCIから比較できる形式にする。

### 6. ギミックの動作検証

- [x] Field/Referenceを読むassertionと、変更前後を監視する `rloop test` の仕様を作る。
- [x] 公開APIで安全に可能な場合だけ、Button pressなどのinteraction probeを追加する。
- [x] 鏡の例をfixture化し、ButtonToggleのtarget/member wiringと、probe利用時のon/off結果を検証する。
- [x] runtime eventを呼び出せない環境では、構造検証までであることを明確に報告する。

### 7. 安全な差分収束

- [x] `rloop diff` でcreate/update/rename/delete候補と理由をJSON化する。
- [x] stable keyによるrenameを実装し、削除して作り直す挙動を避ける。
- [x] 削除は所有境界内だけを対象にし、previewと明示的 `--prune --yes` を必須にする。
- [x] 失敗時のrollback可否を操作単位で示し、非atomicな場合は復旧手順を出す。

## P1: 宣言形式の表現力

### 8. 再利用とファイル分割

- [x] include、parameter、variableを追加し、共通materialや寸法を一元化する。
- [x] prefab/prototypeとinstance、配列・格子向けのrepeatを追加する。
- [x] `$slot:key`、Component、Member、assetを統一的に扱う参照構文を設計する。
- [x] 循環include、key衝突、展開後サイズに上限を設ける。
- [x] house-world.jsonを複数ファイルと再利用部品で書き直し、宣言量を8.1%削減する。

### 9. 型とassetの対応範囲

- [x] 対応済みscalar/reference要素を持つListの一括書き込み
- [x] `colorX` とnamed floating-point値のJSON処理
- [x] Listの要素差分preview＋一括更新、Dictionary、SyncObjectの読み書き
- [x] generic Componentの型指定とReflectionによる検証
- [x] asset URI、enum flags、nullable、nested valueの境界テスト
- [x] material URI、texture、audio、meshなどのasset import/参照ライフサイクル

## P1: ProtoFlux反復開発

- [x] `flux watch` の成功buildだけを、検証済みparentへ安全に再deployする。
- [x] module manifest、複数module、依存順序を扱う。
- [x] deploy前後の構造差分と、失敗時の復旧方針を表示する。
- [x] Flux-SDK version互換性テストと代表的なgraph fixtureを追加する。
- [x] world宣言のstable keyとProtoFlux参照を統合する。

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

旧house-world由来のP0/P1は完了しています。ブロック崩し由来の次期P0では、stable Flux binding基盤とtransactional `set-member` probeまで完了しました。残件はFlux-SDK 1.9.0のglobal input型解決、binding型互換性・未結線検査、Dynamic Impulse、CallInputです。ResoniteLinkにtransaction、framebuffer、汎用event APIがない制約は、非atomic checkpoint recovery、決定的SVG、structural-only capabilityとして明示しています。引き続きrloopが管理する専用の `RLoop_Test*` Slotから導入し、`validate --strict` と `diff --changes-only` を先に実行する運用を推奨します。
