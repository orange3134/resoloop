# テクスチャの転送方式（textureTransfer）

ResoniteはtextureとaudioのassetをResonite自身のホストでファイルとして開きます（`ImportTexture2DFile`）。meshはCLIが内容を送るため影響しません。CLIとResoniteがファイルシステムを共有しない構成（WindowsのResoniteとWSL2上のDev Container、別PC）では、CLI側のパス（例: `/workspaces/...`）をResoniteが開けず、新規textureを含むapplyが失敗します。Resoniteは開けないパスの失敗を約50秒後に返すため、既定の要求タイムアウト（30秒）では`REQUEST_TIMEOUT`になります。

`textureTransfer`を`data`にすると、CLIが画像を復号し、RGBA 8bitの画素を`ImportTexture2DRawData`で送ります。Resoniteからパスが読めるかどうかに左右されません。

## 設定

| 指定 | 値 |
| --- | --- |
| CLI | `--texture-transfer file\|data` |
| 環境変数 | `RESOLOOP_TEXTURE_TRANSFER` |
| `.resoloop.json`、`~/.resoloop/config.json` | `"textureTransfer": "data"` |

既定は`file`です。優先順位はほかの設定と同じ（CLI > 環境変数 > `.resoloop.json` > ユーザー設定）で、`--verbose`の`configSources`に出どころが出ます。`file`・`data`以外の値は`INVALID_TEXTURE_TRANSFER`で止まります（CLIの値は終了コード2、環境変数・設定ファイルの値は3）。

~~~bash
resoloop diff content/prop-v1/model.apply.json --state .resoloop/state/prop.json --texture-transfer data --json
resoloop apply content/prop-v1/model.apply.json --state .resoloop/state/prop.json --texture-transfer data --json
~~~

## `data`の動作

- 対象は、kindが`texture`（`texture2d`）で、このapplyで取り込む（plan/diffの`create`）assetです。`resdb:`などのURIのasset、取り込み済みで変わっていないassetは今までどおりです
- 読める形式はPNG・JPEG・BMP・TGA・GIF（最初のframe）・PSDです。16bitのPNGは8bitに落ちます。JPEGのEXIFの向きは反映しません（Resoniteの`file`の取り込みも反映しないため、見た目は同じです）
- plan/diff（applyの変更前の準備を含む）で画像のヘッダーを確かめ、読めない形式（WebPなど）とHDR（Radiance `.hdr`）は`ASSET_TEXTURE_UNDECODABLE`（終了コード6）で止まります。ワールドもassetも変えません。`context`にasset key（`asset`）、パス（`source`）、理由（`reason`: `unknown-format`・`hdr`・`decode-failed`）が入ります。8bitのPNGなどに変換するか、Resoniteがこのファイルを開ける構成なら`file`を使ってください。自動で`file`に切り替えることはしません
- audioは`data`でもファイルのパスで取り込みます。新規に取り込むaudioがあると、plan/diffが警告`ASSET_AUDIO_FILE_TRANSFER`を出します（別ホストのResoniteでは失敗しえます）
- 取り込み済みかどうかは、今までどおりCLI側で計算した画像ファイルのSHA-256（stateの`sourceHash`）で判定します。`file`と`data`を切り替えても、変わっていない画像は取り込み直しません

## `file`の失敗の案内

`file`でtextureの取り込みが`REQUEST_TIMEOUT`、またはパスが見つからない`ASSET_IMPORT_FAILED`（Resoniteのエラーに`DirectoryNotFoundException`・`FileNotFoundException`を含む）で失敗すると、`suggestions`の先頭に`--texture-transfer data`の案内が入り、`context`にasset key（`asset`）とCLIのパス（`source`）が入ります。エラーコードと終了コードは変わりません。同じapplyを繰り返さず、構成を確かめてください。

## 実機での比較（2026-10-26）

Resonite 2026.9.18.82 / ResoniteLink 0.13.1（Windows 11）に、WSL2上のDev ContainerのCLIから接続して比べました。`file`はResoniteから読めるパス（`\\wsl.localhost\...`）を直接渡しました。256×256の平らな色の区画を並べた画像を、`StaticTexture2D`（`Uncompressed=true`）とUIXのImageで画面いっぱいに表示して撮影し、同じ画像の`file`の撮影と画素を比べました。同じ`file`の画像を2回撮った差（撮影のノイズ）を基準にしています。

| 画像 | 設定 | `data`と`file`の差 |
| --- | --- | --- |
| sRGBのカラーPNG | 既定 | ノイズと同じ（平均0.13、ノイズ0.13） |
| アルファ付きPNG（255/192/128/64） | 既定 | ノイズと同じ |
| ノーマルマップPNG | `IsNormalMap=true` | ノイズと同じ |
| データ用のグレーの段階PNG | `PreferredProfile=Linear` | ノイズと同じ |
| パレットPNG、グレースケールPNG | 既定 | ノイズと同じ |
| 16bit RGB PNG | 既定 | 平均0.30（ノイズ0.13）。8bitへの丸めの差 |
| EXIFの向き6のJPEG | 既定 | ノイズと同じ。EXIFの向きに回した画素を送ると大きく違う（平均99.9）ため、Resoniteの`file`は向きを反映しない |
| 64×64のノイズ（RGB・RGBA）、`FilterMode=Point`で拡大 | 既定 | ノイズと同じ（平均0.86、ノイズ0.85） |

- `colorProfile`: 同じ画素を未指定・`sRGB`・`Linear`で送ると、3つとも同じasset URL（内容のhash）になり、見た目は`file`と同じでした。この版では取り込んだassetに影響しないため、画像ファイルと同じ`sRGB`を明示して送ります。色空間の扱いは今までどおり`StaticTexture2D`の`PreferredProfile`で決まります
- 保存の形式: `data`で取り込んだassetは`local://…/….webp`、`file`のPNGは`local://…/….png`として返りました。拡大したノイズでも画素は`file`と同じに表示され、劣化は見られませんでした
- 大きさ（ノイズを含む画像。同じPC上、WSL2経由）:

| 大きさ | 復号 | `data`の取り込み | `file`の取り込み |
| --- | --- | --- | --- |
| 2048²（画素16 MiB） | 0.23秒 | 2.7秒 | 0.74秒 |
| 4096²（画素64 MiB） | 0.77秒 | 12.4秒 | 5.8秒 |

どちらも既定の要求タイムアウト（30秒）に収まり、WebSocketの送信で失敗しませんでした。`data`は画素を1枚ずつ復号して送るため、CLIが一度に持つのは1枚分の画素です。時間は画素の量より速く伸びる（4倍で約4.6倍）ので、8192²の画像や別PCへの遅い回線では30秒を超えることがあります。`data`の取り込みが`REQUEST_TIMEOUT`になると、`context`にasset key（`asset`）とパス（`source`）が入り、`suggestions`の先頭に`--timeout`（`RESOLOOP_TIMEOUT_SECONDS`）を延ばす案内が画素の大きさとともに出ます。

## 実機での受け入れ（2026-10-26）

WSL2上のDev ContainerのCLIから、WindowsのResonite（同じ版）の新しいワールドへ、Blenderから書き出した実際のbundle（mesh 10、texture 14）をapplyしました。

| 手順 | 結果 |
| --- | --- |
| `file`（既定）でapply | 30秒で`REQUEST_TIMEOUT`（終了コード8）。Slot・Componentの作成は0件。`suggestions`の先頭に`--texture-transfer data`の案内 |
| `--texture-transfer data`でapply | 成功（約20秒）。asset 24件を取り込み、Slot 49・Component 58を作成。textureは正しく表示された |
| もう一度diff（`data`・`file`のどちらも） | 変更0件（no-op 131件） |
| ワールドを保存し、保存したワールドを開き直してdiff | 変更0件。asset 24件はすべて「保存でresdbに移ったURL」と判定。textureの`StaticTexture2D.URL`は`local://…/….webp`から`resdb:///….webp`に変わり、表示も同じ |
| 開き直した後のapply | 書き込み0件で成功し、stateに`resdb:///`のURLを記録。もう一度diffして変更0件 |

開き直した直後のapplyは、`STABLE_COMPONENT_AMBIGUOUS`（`reason: saved-id-not-found`、変更なし）で1回止まりました。stateに記録したconnection ID（開き直す前のワールドの`4`）と、開き直したワールドでの5回目の接続のconnection ID（`4`）がたまたま同じで、同じセッションと判定されたためです。ResoniteLinkのconnection IDはワールドごとに0から数えます。接続を1回はさむとIDがずれ、上のとおり成功しました。転送方式とは関係しない既存の挙動です。
