# Ren'Py Documentation Offline Reader

[简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md) · **English** · [日本語](README.ja.md)

A Ren'Py documentation reader that works without an internet connection. It keeps the original UI style and interactions, improves many parts of the experience, and the app itself is very small — perfect for everyday offline reading.

## Supported documents

| Document | Source |
| --- | --- |
| Ren'Py 中文文档 | doc.renpy.cn/zh-CN |
| Ren'Py 繁體中文文件 | doc.renpy.cn/zh-TW |
| Ren'Py Documentation | www.renpy.org/doc/html |
| Ren'Py 日本語ドキュメント | ja.renpy.org/doc/html |
| Ren'Py Nightly Fix | nightly.renpy.org/current-8-fix/doc |
| Ren'Py Nightly Development | nightly.renpy.org/current-8/doc |

## Improvements and new features

- **Search presentation** — search results are layered by headings, definitions, code blocks and body text, and sorted by relevance first. Results you don't want can be collapsed and hidden, and searches take much less time.
- **Search history** — a search history makes it easier to find what you need again.
- **Quick copy** — code in the documentation can be copied with one click on the copy button.
- **Page tabs** — a tab bar at the top lets you open multiple documentation pages like a browser and move between them quickly.
- **Document switching** — switch between documents quickly, without the long loading times of online documentation.
- **Favourites** — save documents and entries, with a dedicated search box to find them quickly.


## Requirements

- Windows 10 / 11 (64-bit)
- Microsoft Edge WebView2 Runtime (usually preinstalled on Windows 10/11)
- .NET Framework 4.x (included with Windows)


## Download and run

1. Download the latest archive from [Releases](../../releases).
2. Extract it to any folder.
3. Double-click `RenPyDocs.exe` to run it.

The release package contains only the executable and the embedded Python used for downloads; it does not include any documentation. Documents need to be downloaded from the blank page or the “Switch document” panel, which requires an internet connection.


## Build from source

The repository does not include the generated documentation mirror (`app/site`) or the embedded Python runtime (`app/updater/python`). Copy them from a Release archive, or fetch them yourself:

```powershell
cd app
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\RenPyDocs" | Out-Null
python updater\download_doc.py site zh-CN "$env:LOCALAPPDATA\RenPyDocs" python
```

Then, inside the `app` folder, rebuild the pages and compile the executable:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

You need Python 3.10+, .NET Framework 4.x and PowerShell. Whenever `site\<document>\index.html` exists, the build script rebuilds that document's pages and compiles `RenPyDocs.exe`.

## Project layout

```text
app/
├─ src/            C# WinForms + WebView2 shell
├─ updater/        Python crawler, page rebuild, search index, page templates
├─ tools/          build.ps1, headless self-test, UI block copy
├─ site/           local documentation mirror
└─ updater/python/ embedded Python
```

## How it works

- Crawl the static Sphinx pages → rewrite links and asset paths → inject the reader UI → generate the full-text index.
- WinForms + WebView2 maps `site` to the virtual host `renpy.localhost`, so the pages behave like they would on a normal website.
- Downloads run in a separate Python process; progress is written to files and polled by the shell every 500 ms, so a download can continue after the window is closed.

## Data and privacy

- Settings, tabs, favourites and download state are stored in `%LOCALAPPDATA%\RenPyDocs`.
- Only public documentation pages are downloaded; no personal data is collected or uploaded, and there is no analytics or telemetry.
- Documentation content remains the property of the Ren'Py project and the respective documentation sites; this tool only mirrors it for offline use. This is a third-party tool and is not affiliated with the Ren'Py project or the documentation sites.

## Known limitations

- Windows 10/11 64-bit only; WebView2 is required.
- The documentation mirror may lag behind the live site; the official pages are authoritative.
- The app update check only opens the download page; it does not install updates automatically.
- Nightly documentation changes frequently, so a downloaded copy can go out of date quickly.

## License

[MIT](LICENSE)
