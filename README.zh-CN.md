# Ren'Py 文档离线阅读器

**简体中文** · [繁體中文](README.zh-TW.md) · [English](README.md) · [日本語](README.ja.md)

无需联网即可阅读的 Ren'Py 文档阅读器，在保留原版 UI 风格与交互效果的同时，优化了诸多使用体验，并且软件本体非常小，适合日常离线查阅！

## 支持的文档

| 文档 | 来源 |
| --- | --- |
| Ren'Py 中文文档 | doc.renpy.cn/zh-CN |
| Ren'Py 繁體中文文件 | doc.renpy.cn/zh-TW |
| Ren'Py Documentation | www.renpy.org/doc/html |
| Ren'Py 日本語ドキュメント | ja.renpy.org/doc/html |
| Ren'Py Nightly Fix | nightly.renpy.org/current-8-fix/doc |
| Ren'Py Nightly Development | nightly.renpy.org/current-8/doc |

## 优化与新增功能

- **搜索展示**：优化了文档的搜索展示，将内容按照标题、定义、代码块、正文分层展示，并且按照内容关联性优先排序，对于不想要的内容也可以折叠隐藏，并且大大减少了搜索所需时间。
- **搜索历史**：增加了搜索历史功能，可以更方便地查找所需内容。
- **快速复制**：增加了文档代码内容的快速复制，点一下复制按钮即可复制。
- **页面标签**：增加了顶部标签栏，可以像浏览器那样打开多个文档页面，快速浏览多处内容。
- **文档切换**：增加了文档快速切换功能，并且不必像在线文档那样等待较长的加载时间。
- **文档收藏**：增加了文档收藏功能，并配备了独立的搜索栏，可以在里面快速查找所需内容。


## 运行要求

- Windows 10 / 11（64 位）
- Microsoft Edge WebView2 Runtime（Win10/11 通常已预装）
- .NET Framework 4.x（Windows 自带）


## 下载与运行

1. 到 [Releases](../../releases) 下载最新的压缩包。
2. 解压到任意目录。
3. 双击 `RenPyDocs.exe` 运行。

发布包仅包含可执行文件和下载用的内嵌 Python，不包含任何文档，需要在空白页或「切换文档」面板内下载（需要联网）。


## 从源码构建

仓库不包含生成的文档镜像（`app/site`）和内嵌 Python（`app/updater/python`）。可以从 Release 压缩包里复制，或自行抓取：

```powershell
cd app
New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\RenPyDocs" | Out-Null
python updater\download_doc.py site zh-CN "$env:LOCALAPPDATA\RenPyDocs" python
```

然后在 `app` 目录下重建页面并编译 exe：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

需要 Python 3.10+、.NET Framework 4.x 和 PowerShell。只要 `site\<文档>\index.html` 存在，构建脚本就会重建该文档的页面并编译 `RenPyDocs.exe`。

## 目录结构

```text
app/
├─ src/            C# WinForms + WebView2 外壳
├─ updater/        Python 抓取、页面重建、搜索索引、页面模板
├─ tools/          build.ps1、无头自检、UI 代码块副本
├─ site/           本地的文档镜像
└─ updater/python/ 内嵌 Python
```

## 实现概要

- 抓取 Sphinx 静态页 → 改写链接和资源路径 → 注入阅读器界面 → 生成全文索引。
- WinForms + WebView2 把 `site` 映射到虚拟域名 `renpy.localhost`，页面行为与普通网站一致。
- 下载在独立的 Python 进程里跑，进度通过文件回传，外壳每 500 ms 轮询，任务可以脱离窗口继续。

## 数据与隐私

- 设置、标签、收藏、下载状态都保存在 `%LOCALAPPDATA%\RenPyDocs`。
- 只从公开文档页面下载内容，不收集、不上传任何个人数据，也没有统计或遥测。
- 文档内容版权归 Ren'Py 项目及对应文档站点所有，本工具只做离线镜像；本工具是第三方工具，与 Ren'Py 项目及文档站点没有隶属关系。

## 已知限制

- 仅支持 Windows 10/11 64 位，依赖 WebView2。
- 文档镜像可能落后于线上；请以官方页面为准。
- 软件更新检查会下载并合并新版本文件，然后重启软件；不会静默安装。
- Nightly 文档更新频繁，下载后可能很快过期。

## 许可

[MIT](LICENSE)
