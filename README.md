# resoloop

resoloop は、Codex や Claude Code などの AI エージェントから Resonite のワールドを操作するための CLI です。

作りたいものを AI に伝えると、AI が現在のワールドを確認し、Slot や Component の構成をファイルへ記述して、ResoniteLink 経由で反映・検証します。作業内容がファイルとして残るため、同じ構成を繰り返し適用したり、Git で変更を管理したりできます。

resoloop が生成する作業ルートには `FrooxEngine.AI_GeneratedContent` を自動で付与し、`Source` に実行中のツール名とバージョン（例: `[resoloop 0.1.0-preview.4]`）を記録します。宣言ツリー内の持ち運び・装備用ルートにも同じタグを付与します。

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
dotnet tool install --global ResoLoop --version 0.1.0-preview.4
resoloop --version
~~~

すでにインストール済みの場合は、次のコマンドで更新できます。

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.4
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

## さらに詳しく

- [詳細資料](README-DETAILS.md) — コマンド、設計、宣言形式、Flux-SDK、制約
- [クイックスタート](docs/QUICKSTART.md) — 適用、検証、ProtoFlux を含む詳しい手順
- [宣言形式](docs/DECLARATIVE.md) — `content/*.json` の仕様
- [ロードマップ](docs/ROADMAP.md)

## ライセンス

[AGPL-3.0-or-later](LICENSE)
