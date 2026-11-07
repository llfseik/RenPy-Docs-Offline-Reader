# Ren'Py ドキュメント オフラインリーダー

[简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · [English](README.md) · **日本語**

インターネットに接続せずに読める Ren'Py ドキュメントリーダーです。元の UI のスタイルと操作感を保ちながら、さまざまな使い勝手を改善しました。アプリ本体はとても小さく、日常のオフライン閲覧に適しています。

## 対応ドキュメント

| ドキュメント | 取得元 |
| --- | --- |
| Ren'Py 中文文档 | doc.renpy.cn/zh-CN |
| Ren'Py 繁體中文文件 | doc.renpy.cn/zh-TW |
| Ren'Py Documentation | www.renpy.org/doc/html |
| Ren'Py 日本語ドキュメント | ja.renpy.org/doc/html |
| Ren'Py Nightly Fix | nightly.renpy.org/current-8-fix/doc |
| Ren'Py Nightly Development | nightly.renpy.org/current-8/doc |

## 改善と新機能

- **検索表示** — 検索結果を見出し・定義・コードブロック・本文に分けて階層表示し、関連性の高い順に並べます。不要な結果は折りたたんで隠すことができ、検索にかかる時間も大幅に短縮しました。
- **検索履歴** — 検索履歴機能を追加し、必要な内容をより簡単に探し直せます。
- **クイックコピー** — ドキュメント内のコードをコピーボタンひとつでコピーできます。
- **ページタブ** — 上部のタブバーで、ブラウザーのように複数のドキュメントページを開き、いろいろな箇所をすばやく見られます。
- **ドキュメント切り替え** — ドキュメントをすばやく切り替えられ、オンライン版のような長い読み込み時間は必要ありません。
- **お気に入り** — ドキュメントのお気に入り機能を追加し、専用の検索欄から必要な内容をすばやく探せます。


## 動作環境

- Windows 10 / 11（64 ビット）
- Microsoft Edge WebView2 Runtime（Windows 10/11 には通常プリインストールされています）
- .NET Framework 4.x（Windows に含まれています）


## ダウンロードと起動

1. [Releases](../../releases) から最新のアーカイブをダウンロードします。
2. 任意のフォルダーに展開します。
3. `RenPyDocs.exe` をダブルクリックして起動します。

配布パッケージには実行ファイルとダウンロード用の内蔵 Python だけが含まれ、ドキュメントは含まれません。空白ページまたは「ドキュメント切り替え」パネルからダウンロードします（ネットワーク接続が必要です）。


## ソースからビルド

リポジトリには生成済みのドキュメントミラー（`app/site`）と内蔵 Python（`app/updater/python`）は含まれていません。Release のアーカイブからコピーするか、自分で取得してください：

```powershell
cd app
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\RenPyDocs" | Out-Null
python updater\download_doc.py site zh-CN "$env:LOCALAPPDATA\RenPyDocs" python
```

次に `app` ディレクトリでページを再構築し、exe をコンパイルします：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

Python 3.10 以上、.NET Framework 4.x、PowerShell が必要です。`site\<ドキュメント>\index.html` が存在すれば、ビルドスクリプトはそのドキュメントのページを再構築し、`RenPyDocs.exe` をコンパイルします。

## ディレクトリ構成

```text
app/
├─ src/            C# WinForms + WebView2 シェル
├─ updater/        Python クローラー、ページ再構築、検索インデックス、ページテンプレート
├─ tools/          build.ps1、ヘッドレス自己テスト、UI ブロックのコピー
├─ site/           ローカルのドキュメントミラー
└─ updater/python/ 内蔵 Python
```

## 仕組み

- Sphinx の静的ページをクロール → リンクとアセットのパスを書き換え → リーダー UI を注入 → 全文インデックスを生成。
- WinForms + WebView2 が `site` を仮想ホスト `renpy.localhost` にマッピングするため、ページは通常の Web サイトと同じように動作します。
- ダウンロードは別の Python プロセスで実行され、進捗はファイル経由で渡され、シェルが 500 ms ごとに確認します。そのためウィンドウを閉じても続行できます。

## データとプライバシー

- 設定、タブ、お気に入り、ダウンロード状態は `%LOCALAPPDATA%\RenPyDocs` に保存されます。
- 公開されているドキュメントのページだけをダウンロードし、個人データの収集や送信は行いません。解析やテレメトリーもありません。
- ドキュメントの内容の権利は Ren'Py プロジェクトおよび各ドキュメントサイトに帰属します。本ツールはオフライン閲覧用にミラーするだけです。本ツールは第三者製であり、Ren'Py プロジェクトや各ドキュメントサイトとは関係ありません。

## 既知の制限

- Windows 10/11 64 ビット専用で、WebView2 が必要です。
- ドキュメントミラーはオンライン版より古い場合があります。公式ページを正としてください。
- アプリの更新確認は新しいバージョンを通知してダウンロードページを開くだけで、自動インストールは行いません。
- Nightly ドキュメントは更新が頻繁なため、ダウンロードしたコピーはすぐに古くなる可能性があります。

## ライセンス

[MIT](LICENSE)
