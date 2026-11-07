# Ren'Py 文件離線閱讀器

[简体中文](README.zh-CN.md) · **繁體中文** · [English](README.md) · [日本語](README.ja.md)

無需連網即可閱讀的 Ren'Py 文件閱讀器，在保留原版 UI 風格與互動效果的同時，優化了許多使用體驗，而且軟體本體非常小，適合日常離線查閱！

## 支援的文件

| 文件 | 來源 |
| --- | --- |
| Ren'Py 中文文档 | doc.renpy.cn/zh-CN |
| Ren'Py 繁體中文文件 | doc.renpy.cn/zh-TW |
| Ren'Py Documentation | www.renpy.org/doc/html |
| Ren'Py 日本語ドキュメント | ja.renpy.org/doc/html |
| Ren'Py Nightly Fix | nightly.renpy.org/current-8-fix/doc |
| Ren'Py Nightly Development | nightly.renpy.org/current-8/doc |

## 優化與新增功能

- **搜尋呈現**：優化了文件的搜尋呈現，將內容依標題、定義、程式碼區塊、正文分層顯示，並依內容關聯性優先排序；不需要的內容也可以收合隱藏，而且大幅減少了搜尋所需時間。
- **搜尋記錄**：新增搜尋記錄功能，可以更方便地找回需要的內容。
- **快速複製**：新增文件程式碼內容的快速複製，按一下複製按鈕即可複製。
- **頁面標籤**：新增頂部標籤列，可以像瀏覽器一樣開啟多個文件頁面，快速瀏覽多處內容。
- **文件切換**：新增文件快速切換功能，而且不必像線上文件那樣等待較長的載入時間。
- **文件收藏**：新增文件收藏功能，並配備獨立的搜尋列，可以在裡面快速尋找需要的內容。


## 執行需求

- Windows 10 / 11（64 位元）
- Microsoft Edge WebView2 Runtime（Win10/11 通常已預裝）
- .NET Framework 4.x（Windows 內建）


## 下載與執行

1. 到 [Releases](../../releases) 下載最新的壓縮檔。
2. 解壓縮到任意目錄。
3. 雙擊 `RenPyDocs.exe` 執行。

發佈包僅包含執行檔和下載用的內嵌 Python，不包含任何文件，需要在空白頁或「切換文件」面板內下載（需要連網）。


## 從原始碼建置

儲存庫不包含已產生的文件鏡像（`app/site`）和內嵌 Python（`app/updater/python`）。可以從 Release 壓縮檔複製，或自行抓取：

```powershell
cd app
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\RenPyDocs" | Out-Null
python updater\download_doc.py site zh-CN "$env:LOCALAPPDATA\RenPyDocs" python
```

然後在 `app` 目錄下重建頁面並編譯 exe：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

需要 Python 3.10+、.NET Framework 4.x 和 PowerShell。只要 `site\<文件>\index.html` 存在，建置腳本就會重建該文件的頁面並編譯 `RenPyDocs.exe`。

## 目錄結構

```text
app/
├─ src/            C# WinForms + WebView2 外殼
├─ updater/        Python 抓取、頁面重建、搜尋索引、頁面範本
├─ tools/          build.ps1、無頭自檢、UI 程式碼區塊副本
├─ site/           本機的文件鏡像
└─ updater/python/ 內嵌 Python
```

## 實作概要

- 抓取 Sphinx 靜態頁 → 改寫連結和資源路徑 → 注入閱讀器介面 → 產生全文索引。
- WinForms + WebView2 把 `site` 對應到虛擬網域 `renpy.localhost`，頁面行為和一般網站一致。
- 下載在獨立的 Python 程序裡執行，進度透過檔案回傳，外殼每 500 ms 輪詢，工作可以脫離視窗繼續。

## 資料與隱私

- 設定、標籤、收藏、下載狀態都保存在 `%LOCALAPPDATA%\RenPyDocs`。
- 只從公開文件頁面下載內容，不收集、不上傳任何個人資料，也沒有統計或遙測。
- 文件內容版權歸 Ren'Py 專案及對應文件站點所有，本工具只做離線鏡像；本工具是第三方工具，與 Ren'Py 專案及文件站點沒有隸屬關係。

## 已知限制

- 僅支援 Windows 10/11 64 位元，依賴 WebView2。
- 文件鏡像可能落後於線上；請以官方頁面為準。
- 「軟體更新檢查」目前只儲存網址和開關，還沒有實際連網檢查。
- Nightly 文件更新頻繁，下載後可能很快過期。

## 授權

[MIT](LICENSE)
