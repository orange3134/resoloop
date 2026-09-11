[English](README.md)｜[日本語](README_JA.md)

# resoloop

![resoloop_logo](./resource/resoloop_resonite_16_9.png)

resoloop は、Codex や Claude Code などの AI エージェントから Resonite のワールドを操作するための CLI です。

作りたいものを AI に伝えると、AI が現在のワールドを確認し、Slot や Component の構成をファイルへ記述して、ResoniteLink 経由で反映・検証します。作業内容がファイルとして残るため、同じ構成を繰り返し適用したり、Git で変更を管理したりできます。

resoloop が生成する作業ルートには `FrooxEngine.AI_GeneratedContent` を自動で付与し、`Source` に実行中のツール名とバージョン（例: `[resoloop 0.1.0-preview.9]`）を記録します。宣言ツリー内の持ち運び・装備用ルートにも同じタグを付与します。

> [!NOTE]
> 現在はプレビュー版です。ResoniteLink も Beta のため、更新によって動作が変わる可能性があります。

## インストール

必要なもの:

- Windows 10 / 11
- [`.NET 10 SDK`](https://dotnet.microsoft.com/download/dotnet/10.0)
- Resonite
- Codex、Claude Code などの AI コーディングエージェント

PowerShell で resoloop をインストールします。

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.9
resoloop --version
~~~

すでにインストール済みの場合は、次のコマンドで更新できます。

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.9
~~~

## 使い方

### 1. プロジェクトを作る

Resonite で作りたいものごとに、専用のプロジェクトを作成します。

~~~powershell
resoloop init MyResoniteProject
Set-Location MyResoniteProject
~~~

### 2. ResoniteLink を設定する

1. Resonite を起動し、編集したいワールドを開きます。
2. Dashboard の `Session` ページから `Settings` タブを開きます。
3. 画面左下の `Enable ResoniteLink` を選択します。
4. `ResoniteLink running on port: ...` と表示されたら、そのポート番号を確認します。
5. プロジェクトを開いている PowerShell で、表示されたポートを環境変数へ設定します。

たとえば、表示されたポートが `12449` の場合:

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
resoloop doctor
~~~

最後に `ready` と表示されれば準備完了です。

または、AIにポートを伝えて依頼してください。

~~~text
Resoniteで虹色に光るBOXを作って。ResoniteLinkは12449で起動中。
~~~

### 3. AI に作業を依頼する

作成したプロジェクトを AI エージェントで開きます。プロジェクト作成後から同じ AI セッションを使い続けている場合は、生成された Skill を認識させるため、一度セッションを開き直してください。

あとは、Resonite で実現したい内容を普段の言葉で依頼します。たとえば:

~~~text
Resoniteでテレポーターガンを作って。装備できるアイテムで、銃みたいな形。銃を撃つと弾が山なりに飛んでいって、当たった地点に自分がワープするようにして。
~~~

## Blenderによるモデル制作

preview.9では`resoloop hierarchy --under Root --depth 1 --include-components --summary --json`でメンバー値を省いた階層を確認できます。宣言内では`$slot-member:crystal.Rotation`でSlotの公開fieldを参照し、Reflectionで確認したnative driverへ接続できます。Component fieldは従来の`$member:componentKey.MemberName`を使います。[宣言リファレンス](docs/DECLARATIVE.md)と[鍛冶屋テストの改善記録](docs/BLACKSMITH-FEEDBACK.md)を参照してください。

ソースビルドには、PATH未登録でもBlenderを探す `blender find`、バックグラウンドPython実行の `blender run`、静的モデルを書き出す `blender export` を追加しています。同梱の `resonite-blender` スキルは、プロシージャルメッシュでは難しい形状でのBlender利用、VR向けの品質と負荷の調整、UV・法線・テクスチャ・マテリアルのインポートを案内します。未インストールの場合はユーザーに許可を確認し、CLIは自動インストールしません。

~~~powershell
resoloop blender find --json
resoloop blender run modeling/model.py --arg=artifacts/prop.blend --json
resoloop blender export artifacts/prop.blend --output content/prop-v1 --name Prop --parent VERIFIED_PARENT --json
resoloop validate content/prop-v1/model.apply.json --strict --json
resoloop diff content/prop-v1/model.apply.json --json
resoloop apply content/prop-v1/model.apply.json --state .resoloop/state/prop.json --json
~~~

プロジェクトのPythonスクリプトと確認済みの親Slotを指定してください。書き出しはレンダリングもワールド変更も行いません。[Blender制作の詳細](docs/BLENDER.md)に対応範囲・検証手順を記載しています。

UV付きn-gonと生成／編集画像のpixel bufferに対応しています。色空間enumは`resoloop type describe FrooxEngine.StaticTexture2D --member PreferredProfile --json`で実機Reflectionから取得できます。item監査には外部role候補と未使用の許可指定を表示します。[制作テストの改善計画と検証記録](docs/BLENDER-FEEDBACK.md)に詳細をまとめています。

`blender export`の`--preserve-hierarchy`で階層とpivot、`--pack-pbr`で対応するMetallic／Roughness直結画像のpackingを扱えます。新規出力のproviderは名前付きSlotへ分離し、中断後の再解決を容易にします。旧出力と同じ配置には`--legacy-root-providers`を使います。strictの値変換事前検証、nested設定の再適用、Slot field監査、外部meshのboundsも改善しました。[時計塔・戦車の統合改善記録](docs/CLOCKTOWER-FEEDBACK.md)を参照してください。制作は見た目を優先してから資源量を見直し、セッションFPSをモデル単体の合否基準にはしません。

## さらに詳しく

- [詳細資料](README-DETAILS.md) — コマンド、設計、宣言形式、Flux-SDK、制約
- [クイックスタート](docs/QUICKSTART.md) — 適用、検証、ProtoFlux を含む詳しい手順
- [宣言形式](docs/DECLARATIVE.md) — `content/*.json` の仕様
- [ロードマップ](docs/ROADMAP.md)

## ライセンス

[AGPL-3.0-or-later](LICENSE)
