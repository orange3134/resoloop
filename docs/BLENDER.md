# Blender → Resonite

Blenderが得意な形状を背景Python処理で制作し、既存のvalidate/diff/applyへ接続します。新規プロジェクトでは `resoloop init` が `resonite-blender` も配置します。既存プロジェクトでは同じビルドの `skills sync --check` で差分を確認してから `skills sync --update` を実行します。ユーザーが編集したスキルは上書きしません。

## 検出と実行

`resoloop blender find --json` は次の順で実在する実行ファイルを探し、`--version` を実行します。

1. `--blender-executable` → `RESOLOOP_BLENDER_EXECUTABLE` → projectの `blenderExecutable` → user設定。
2. PATH。
3. Program Files / Program Files (x86) / LocalAppData/Programs のBlender Foundation配下のバージョン別フォルダ、およびBlenderフォルダ。各ルート内はバージョンの降順。
4. Windows App Paths / UninstallのInstallLocation（HKCU/HKLM、32/64bit）。

全ドライブの再帰検索やPATHの変更は行いません。Steamの別ライブラリ、portable版、独自フォルダは明示指定してください。明示パスが不正なら別バージョンへフォールバックせず `BLENDER_PATH_INVALID` を返します。未検出は `BLENDER_NOT_FOUND`。インストールが必要な場合はエージェントがユーザーの許可を確認します。

~~~powershell
resoloop blender find --blender-executable 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --json
resoloop blender run examples/blender/build_prop.py --arg=artifacts/blender/prop.blend --json
resoloop blender export artifacts/blender/prop.blend --output content/prop-v1 --name Prop --parent VERIFIED_PARENT --json
~~~

この例の制作スクリプトはリポジトリに含まれます。global toolから使う場合は自分のプロジェクトのスクリプトを指定します。`blender run` は `--blend FILE.blend` でファイルを先に読み込み、`--arg=VALUE` を繰り返してPythonへ渡せます。実行順は `--background --factory-startup --disable-autoexec [FILE] --python-exit-code 1 --python SCRIPT -- ARGS`。明示Pythonは通常のファイル操作権限を持ちます。Python例外／非ゼロ終了は `BLENDER_FAILED`、起動失敗は `BLENDER_START_FAILED`。`--json` ではBlenderログを結果に格納し、JSON出力へ混入させません。`--command-timeout`／キャンセルはプロセスツリーを停止します。

## 出力と対応範囲

新しい出力ディレクトリを指定します。既存ディレクトリには `BLENDER_OUTPUT_EXISTS` で停止します。`model.apply.json`、`*.mesh.json`、PNGテクスチャ、`report.json` を生成します。`.blend` は変更せず、レンダリングも行いません。失敗したbundleは適用せず、新しい出力先でやり直してください。

UV付きn-gonは一時メッシュで三角形化してから接線を計算するため、回避用Triangulateモディファイアの追加は不要です。UV・corner normal・色・材質の対応を保持します。画像は`Image.copy()`を使わず現在のpixel bufferをPNGへ保存します。buffer欠損時だけ画像が記録している保存先から回復を試し、warningを出します。失われた未保存pixelは復元できず、エラーに画像名・パス・対処を表示します。

制作時は画像を保存し、必要に応じて色空間を維持してreload／packしてから`.blend`を保存し、再openで画像を確認してください。Non-Colorはnormal／data mapのpixelを書き込む前に設定します。出力後にapplyへ材質や画像を追加した場合、`report.json`は自動更新されないため最終納品レポートで資源量を再集計します。

| データ | 処理 |
| --- | --- |
| メッシュ | 可視の静的Mesh。評価済みモディファイアを三角形化し、名前から安定keyを作成 |
| 座標 | world transformとscene unit scaleを焼込み、(x,y,z) → (x,z,y) metres。法線・接線・表裏を整合 |
| UV／法線 | corner単位の継ぎ目・ハードエッジを保持。render-active UVを0番、残りも保存 |
| 色／接線 | active vertex color、接線と符号を保存。vertex color表示用シェーダーは別途設定 |
| マテリアル | 使用material順のsubmeshとrendererのMaterialsを一致。共通material/imageは共有provider |
| PBS_Metallic | opaque Principledのbase color、metallic、roughness定数、直接接続したalbedo/emission画像、tangent-space normal画像 |
| 制限 | リグ、shape key、animation、Geometry Nodes/instance、任意shader graphは自動変換しない。静的コピー・実体化・ベイクを明示的に行う |

`--collection NAME` で対象を限定できます。階層は平坦化し、各メッシュへworld変換を焼き込みます。共有原点をBlender側で意図して設定してください。shader graphの非対応入力、マッピング変換、透過などは黙って捨てずエラーにします。細かな条件とVR制作方針は [resonite-blender SKILL.md](../skills/codex/resonite-blender/SKILL.md) にあります。

`--preserve-hierarchy`を指定すると、ローカルmesh座標・origin・親子transformを保持します。非表示の親もtransform Slotとして必要な範囲で保持し、shearや特異transformは明示的な静的処理を要求します。animation自体はexportしません。新規出力の画像／材質は`Blender_Providers`以下の個別Slotに配置し、結線途中の再接続でも名前とpathで識別します。従来bundleへの改訂は`--legacy-root-providers`で旧配置を維持し、配置モード変更は必ずdiffで確認してください。

~~~powershell
resoloop blender export artifacts/prop.blend --output content/prop-v2 --name Prop --parent VERIFIED_PARENT --preserve-hierarchy --pack-pbr --json
~~~

`--pack-pbr`はMetallic／Roughnessへ直結したNon-Colorの静止画像（grayscale ColorまたはAlpha、同寸法、active UV、Flat/Repeat/Linear sampling）に限定したpackingです。R=metallic、G=1、B=0、A=1−roughnessをPNGへ生成し、`PreferredProfile=Linear`を明示します。未接続入力は定数を使い、任意グラフのbakeや自動resampleはしません。未使用の編集用画像はpackに加えてfake user等で保持し、blend再openで確認してください。

## インポート

ResoniteLink 0.13.1は全シーンのGLB/FBX読み込みAPIではなく、メッシュと画像のasset APIを提供します。meshは保存用ImportMeshJSONを検査し、均一な静的属性のデータをアダプター内で `ImportMeshRawData` に変換して送信します。実機でJSON APIのUV入力が `UV channel 0 is already configured with 0 dimensions` となる問題を回避し、送信サイズも削減します。骨・blendshape等、損失なく変換できないデータには従来のJSON経路を残しています。

画像はResoniteホスト側からファイルを読み取る `ImportTexture2DFile` を使用するため、通常は同じPC上で実行します。別PCへ接続する場合は、CLIのhash計算とResoniteのimportの両方から同じ絶対パスで読める共有先を使うか、CLIもホスト側で実行してください。メッシュのasset URLだけでは表示されず、StaticMesh／StaticTexture2D／material／MeshRendererの接続も必要です。bundleのapplyがこの接続を記述します。Blenderの線形socket色はCLIのcolorX tupleが使用するsRGB値へ変換し、sRGB画像には二重のgamma変換をしません。

~~~powershell
resoloop type describe FrooxEngine.StaticMesh --json
resoloop type describe FrooxEngine.StaticTexture2D --json
resoloop type describe FrooxEngine.StaticTexture2D --member PreferredProfile --json
resoloop type describe FrooxEngine.PBS_Metallic --json
resoloop type describe FrooxEngine.MeshRenderer --json
resoloop validate content/prop-v1/model.apply.json --json
resoloop validate content/prop-v1/model.apply.json --strict --json
resoloop diff content/prop-v1/model.apply.json --state .resoloop/state/prop.json --changes-only --json
resoloop apply content/prop-v1/model.apply.json --state .resoloop/state/prop.json --json
resoloop inspect '$slot:root' --state .resoloop/state/prop.json --depth 2 --members --json
~~~

改訂時もownershipと名前を保ち、同じstateを指定します。source hashにより変更されたassetだけをimportします。返された `local://` / `resdb://` はそのまま扱い、永続保存・配布の完了とは区別してください。見た目はResoniteの小さなcaptureで確認します。UV確認には非対称な色配置を使い、面の向き・シェーディング・物理サイズも確認します。

roughness画像やORMを使う場合は、base PBS_Metallic用にR=metallic、G=occlusionまたはheight、A=1−roughnessへ組み直し、Reflectionで確認したprovider/memberへapplyで接続します。data mapの線形profile、color画像のsRGB、OpenGL normalの扱いを区別します。別のmaterial型には別のチャンネル規約があり得ます。[Resonite channel packing](https://wiki.resonite.com/Channel_Packing)、[PBS_Metallic](https://wiki.resonite.com/PBS_Metallic) を参照してください。

enumは上記`--member PreferredProfile`でassembly込みの型と値を取得します。外部参照の監査では`externalRoleCandidates`を確認し、参照先を調べてから完全修飾`Type:MemberPath`またはsession限定`ComponentID:MemberPath`を明示許可します。`unmatchedExternalRoles`は指定の誤りの手掛かりです。全ての`_shader`を無条件に許可しません。

見た目は遠景のシルエット、中景の構造と素材差、近景の摩耗や木目の3段階で確認します。曲面のsmooth normalと意図的なhard edgeを使い分け、三角形数と属性分割後の頂点数を両方確認してください。[海賊船テストの改善計画と検証記録](BLENDER-FEEDBACK.md)も参照できます。

制作方針は、要求する造形・素材の完成度を先に満たしてから負荷を調整する順序です。大型の主役モデルを小物向けの低い予算へ寄せず、近景に効く機構・継手などには十分なgeometryを使えます。材質数・画像寸法も一律に最小化しません。exportの数値警告は上限や削減命令ではありません。不可視・重複の無駄を省き、距離・同時配置数・実測負荷に照らして見た目を保った最適化を行います。[戦車テストの評価と改善計画](TANK-FEEDBACK.md)に指示変更の理由とCLI改善の優先順位をまとめています。

## 検証

通常の `dotnet test ResoLoop.slnx --no-build` はBlenderやResoniteを起動しません。実機往復は次のようにopt-inします。Blenderが見つからない場合は失敗し、勝手にインストールしません。

ResoniteなしのBlender実機回帰テストは `resoloop blender run tests/blender/test_export.py --arg=src/RLoop.ResoniteLink/blender_export.py --json` で実行します。UV seam、複数UV、vertex color、鏡映・非一様scale、法線と面の整合、normal PNG、非対応shaderの失敗を検査します。Python例外とキャンセルのプロセステストは `RESOLOOP_RUN_BLENDER_TESTS=1` を指定して `dotnet test tests/RLoop.Tests --no-build --filter FullyQualifiedName~BackgroundProcessReportsPythonFailureAndCancels` で実行できます。

加えてn-gonの接線生成と元mesh不変、生成／dirty画像の最新pixel保持、保存したblendの再open、欠損画像の診断を検査します。画像bufferの扱いは[Blender Image API](https://docs.blender.org/api/5.2/bpy.types.Image.html)と実行環境のRNAで確認しています。

~~~powershell
$env:RESOLOOP_RUN_INTEGRATION='1'
$env:RESOLOOP_RUN_BLENDER_TESTS='1'
$env:RESONITE_LINK_URL='ws://localhost:<current-port>'
# 任意: 実際の見た目を640x480で確認。写真は写真フォルダにも残ります。
$env:RESOLOOP_BLENDER_CAPTURE_FILE=Join-Path (Get-Location) 'artifacts/blender-check.jpg'
dotnet test tests/RLoop.IntegrationTests --no-build --filter FullyQualifiedName~BlenderIntegrationTests
~~~

専用 `ResoLoop_Test_Blender_*` Slot以下だけにimportし、finallyで同一Slotを再確認して削除します。モデル単体の評価にセッションFPSは使用せず、未完了の検証項目にも含めません。人数、他のアイテム、世界内で進行する編集や処理の影響を分離できないためです。三角形数・展開頂点・mesh／材質／描画セクション・画像寸法と概算容量・追加した灯や駆動Componentなど、そのモデルに帰属する資源量を記録します。これらは実測フレーム時間や実測draw-call値ではありません。実機の見た目・構造・必要な動作の確認は引き続き行います。

API根拠: [pinned ResoniteLink source](https://github.com/Yellow-Dog-Man/ResoniteLink/tree/067afad3d1977b3806cbad077f4e8a534f56f451/ResoniteLink/Models/Assets/Mesh)、[Blender command-line arguments](https://docs.blender.org/manual/en/latest/advanced/command_line/arguments.html)。Blenderのバージョン依存APIは実行環境のRNAと `--help` でも確認します。
