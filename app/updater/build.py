# -*- coding: utf-8 -*-
"""Post-process crawled Ren'Py docs: rewrite asset URLs, build a full-text
search index (heading/paragraph granularity), and add helper pages."""
import glob
import hashlib
import html
import io
import json
import os
import re
import shutil
import sys

SITE = os.path.join("web", "site")
DOCS = os.path.join(SITE, "zh-CN")
TEMPLATE = os.path.join("tools", "search_body.html")
LANG = "zh-CN"

# every document the app knows about: the id is also the folder name under
# site/, `site` is where that document is downloaded from. Order is the order
# of the switcher panel / picker page.
CATALOG = [
    {"id": "zh-CN", "name": "Ren'Py 中文文档",
     "site": "https://doc.renpy.cn/zh-CN/"},
    {"id": "zh-TW", "name": "Ren'Py 繁體中文文件",
     "site": "https://doc.renpy.cn/zh-TW/"},
    {"id": "en", "name": "Ren'Py Documentation",
     "site": "https://www.renpy.org/doc/html/"},
    {"id": "ja", "name": "Ren'Py 日本語ドキュメント",
     "site": "http://ja.renpy.org/doc/html/"},
    {"id": "en-nightly-fix", "name": "Ren'Py Nightly Fix",
     "site": "https://nightly.renpy.org/current-8-fix/doc/"},
    {"id": "en-nightly", "name": "Ren'Py Nightly Development",
     "site": "https://nightly.renpy.org/current-8/doc/"},
]


def configure(site, template=None, lang="zh-CN"):
    """Point the builder at another mirror (used by the in-app updater)."""
    global SITE, DOCS, TEMPLATE, LANG
    SITE = site
    LANG = lang
    DOCS = os.path.join(site, lang)
    if template:
        TEMPLATE = template
VERSION = "8.5.4"

# The interface language is chosen in the desktop app (Settings). The shell
# and the generated pages are static, so every UI string lives here in four
# languages and the page picks one at runtime from window.__rpUiLang.
UI_TEXT = {
    # floating buttons / document switcher
    "fab.docs": ["切换文档", "切換文件", "Switch document", "文書を切り替え"],
    "fab.settings": ["设置", "設定", "Settings", "設定"],
    "fab.update": ["检查更新", "檢查更新", "Check for updates", "アップデートを確認"],
    "fab.favorites": ["收藏", "收藏", "Favourites", "お気に入り"],
    "fab.search": ["搜索文档", "搜尋文件", "Search the docs", "文書を検索"],
    "fab.theme": ["明暗主题", "明暗主題", "Light / dark theme", "ライト／ダーク"],
    "docs.modeAria": ["选择或删除文档", "選擇或刪除文件", "Select or delete documents", "文書の選択または削除"],
    "docs.select": ["选择", "選擇", "Select", "選択"],
    "docs.delete": ["删除", "刪除", "Delete", "削除"],
    "docs.empty": ["还没有下载的文档", "還沒有下載的文件", "No documents downloaded yet", "ダウンロード済みの文書はまだありません"],
    "docs.pause": ["暂停下载", "暫停下載", "Pause download", "ダウンロードを一時停止"],
    "docs.resume": ["继续下载", "繼續下載", "Resume download", "ダウンロードを再開"],
    "docs.download": ["下载这份文档", "下載這個文件", "Download this document", "この文書をダウンロード"],
    "docs.deleteDoc": ["删除这份文档", "刪除這個文件", "Delete this document", "この文書を削除"],
    "docs.confirm": ["确定要删除「{0}」吗？", "確定要刪除「{0}」嗎？", "Delete “{0}”?", "「{0}」を削除しますか？"],
    "fav.whole": ["收藏整篇文档", "收藏整篇文件", "Save the whole document", "文書全体をお気に入り"],
    "fav.wholeOff": ["取消收藏整篇文档（含小类）", "取消收藏整篇文件（含小類）", "Remove the whole document", "文書全体をお気に入りから外す"],
    "fav.entry": ["收藏这个词条", "收藏這個詞條", "Save this entry", "この項目をお気に入り"],
    "fav.entryOff": ["取消收藏", "取消收藏", "Remove from favourites", "お気に入りから外す"],
    "fav.savedWhole": ["已收藏整篇文档", "已收藏整篇文件", "Document saved to favourites", "文書をお気に入りに追加しました"],
    "fav.unsavedWhole": ["已取消收藏整篇文档", "已取消收藏整篇文件", "Document removed from favourites", "文書をお気に入りから外しました"],
    "fav.saved": ["已收藏", "已收藏", "Saved to favourites", "お気に入りに追加しました"],
    "fav.unsaved": ["已取消收藏", "已取消收藏", "Removed from favourites", "お気に入りから外しました"],
    "copy.signature": ["复制签名", "複製簽名", "Copy signature", "署名をコピー"],
    "copy.signatureDone": ["已复制签名", "已複製簽名", "Signature copied", "署名をコピーしました"],
    "copy.online": ["复制链接并在浏览器中打开线上文档", "複製連結並在瀏覽器中開啟線上文件", "Copy the link and open the online page", "リンクをコピーしてオンライン版を開く"],
    "copy.onlineDone": ["已复制线上链接", "已複製線上連結", "Online link copied", "オンラインリンクをコピーしました"],
    "copy.opening": ["链接已复制，正在打开浏览器", "連結已複製，正在開啟瀏覽器", "Link copied, opening your browser", "リンクをコピーしました。ブラウザを開きます"],
    "nav.back": ["后退", "後退", "Back", "戻る"],
    "nav.home": ["返回首页", "返回首頁", "Home", "ホーム"],
    "nav.forward": ["前进", "前進", "Forward", "進む"],
    "lightbox.close": ["关闭", "關閉", "Close", "閉じる"],
    # search page
    "search.title": ["搜索", "搜尋", "Search", "検索"],
    "search.ph": ["搜索文档…", "搜尋文件…", "Search the docs…", "文書を検索…"],
    "search.hint": ["在搜索框输入关键词，结果会即时显示在这里。多个关键词用空格分隔，表示同时包含。",
                    "在搜尋框輸入關鍵字，結果會即時顯示在這裡。多個關鍵字用空格分隔，表示同時包含。",
                    "Type keywords in the search box; results appear here as you type. Separate words with spaces to require all of them.",
                    "検索ボックスにキーワードを入力すると、結果がここに表示されます。複数の語を空白で区切ると、すべてを含むページに絞り込めます。"],
    "search.empty": ["还没有搜索内容。试试这些常见主题：", "還沒有搜尋內容。試試這些常見主題：",
                     "Nothing searched yet. Try one of these topics:",
                     "まだ検索していません。よく使うトピック："],
    "search.stats": ["本地索引收录 {0} 篇文档、{1} 个章节、{2} 个代码块、{3} 个 API 定义；完全离线运行。",
                     "本機索引收錄 {0} 篇文件、{1} 個章節、{2} 個程式碼區塊、{3} 個 API 定義；完全離線執行。",
                     "The local index covers {0} documents, {1} sections, {2} code blocks and {3} API entries. Everything runs offline.",
                     "ローカル索引には {0} 文書、{1} セクション、{2} コードブロック、{3} API 定義が入っています。完全オフラインで動作します。"],
    "search.toggle": ["收起/展开", "收合/展開", "Collapse / expand", "折りたたみ／展開"],
    "search.collapse": ["收起该文档的结果", "收合該文件的結果", "Collapse this document's results", "この文書の結果を折りたたむ"],
    "search.begin": ["开头部分", "開頭部分", "Beginning", "冒頭"],
    "search.def": ["定义", "定義", "definition", "定義"],
    "search.code": ["代码", "程式碼", "code", "コード"],
    "search.postTitle": ["搜索：{0}", "搜尋：{0}", "Search: {0}", "検索：{0}"],
    "search.postTitleEmpty": ["搜索", "搜尋", "Search", "検索"],
    "search.none": ["没有找到与 <b>{0}</b> 匹配的内容。", "沒有找到與 <b>{0}</b> 符合的內容。",
                    "Nothing matched <b>{0}</b>.", "<b>{0}</b> に一致する内容はありません。"],
    "search.results": ["找到 <b>{0}</b> 条结果", "找到 <b>{0}</b> 條結果",
                       "<b>{0}</b> results", "<b>{0}</b> 件の結果"],
    "search.merged": ["（合并为 {0} 条）", "（合併為 {0} 條）", " ({0} after merging)", "（統合後 {0} 件）"],
    "search.docs": ["{0} 篇文档", "{0} 篇文件", "{0} documents", "{0} 文書"],
    "search.showing": ["显示前 {0} 条", "顯示前 {0} 條", "showing the first {0}", "先頭 {0} 件を表示"],
    # favourites page
    "fav.title": ["收藏", "收藏", "Favourites", "お気に入り"],
    "fav.ph": ["在收藏里筛选…", "在收藏裡篩選…", "Filter favourites…", "お気に入りを絞り込む…"],
    "fav.clear": ["清空", "清空", "Clear", "クリア"],
    "fav.hint": ["按文档（大类）归档，命中过的词条（小类）列在下面；收藏存在本机，关掉程序也不会丢。",
                 "按文件（大類）歸檔，收藏過的詞條（小類）列在下面；收藏存在本機，關掉程式也不會遺失。",
                 "Grouped by document; the entries you saved are listed underneath. Favourites live on this machine and survive restarts.",
                 "文書ごとにまとめ、保存した項目をその下に並べます。お気に入りはこの PC に保存され、終了しても残ります。"],
    "fav.empty": ["还没有收藏。打开任意文档，点标题右侧的五角星：整篇文档就是大类，某个标题或定义就是它下面的小类。",
                  "還沒有收藏。開啟任意文件，點標題右側的五角星：整篇文件就是大類，某個標題或定義就是它下面的小類。",
                  "Nothing saved yet. Open any document and click the star next to a heading: the document becomes the group, the heading or definition becomes an entry under it.",
                  "まだお気に入りはありません。文書を開いて見出しの横の星を押すと、文書がグループ、見出しや定義がその項目になります。"],
    "fav.removeWhole": ["取消收藏整篇文档（含小类）", "取消收藏整篇文件（含小類）", "Remove the whole document", "文書全体をお気に入りから外す"],
    "fav.remove": ["取消收藏", "取消收藏", "Remove from favourites", "お気に入りから外す"],
    "fav.local": ["收藏保存在本机（localStorage），换台电脑不会同步。",
                  "收藏保存在本機（localStorage），換台電腦不會同步。",
                  "Favourites are stored on this machine (localStorage) and do not sync.",
                  "お気に入りはこの PC（localStorage）に保存され、同期はされません。"],
    "fav.stats": ["共 <b>{0}</b> 篇文档 · 匹配 <b>{1}</b> 条小类",
                  "共 <b>{0}</b> 篇文件 · 符合 <b>{1}</b> 條小類",
                  "<b>{0}</b> documents · <b>{1}</b> matching entries",
                  "<b>{0}</b> 文書 · <b>{1}</b> 件が一致"],
    "fav.statsAll": ["共 <b>{0}</b> 篇文档 · <b>{1}</b> 条小类收藏",
                     "共 <b>{0}</b> 篇文件 · <b>{1}</b> 條小類收藏",
                     "<b>{0}</b> documents · <b>{1}</b> entries",
                     "<b>{0}</b> 文書 · <b>{1}</b> 件のお気に入り"],
    "fav.none": ["没有匹配“{0}”的收藏。", "沒有符合「{0}」的收藏。",
                 "No favourites match “{0}”.", "「{0}」に一致するお気に入りはありません。"],
    # picker page
    "pick.title": ["请选择文档", "請選擇文件", "Choose a document", "文書を選択"],
    "pick.aria": ["文档列表", "文件清單", "Document list", "文書リスト"],
    "pick.empty": ["暂无可选文档", "暫無可選文件", "No documents available", "選択できる文書がありません"],
    # helper page titles (tab label + breadcrumb)
    "page.search": ["搜索", "搜尋", "Search", "検索"],
    "page.favorites": ["收藏", "收藏", "Favourites", "お気に入り"],
    "page.picker": ["请选择文档", "請選擇文件", "Choose a document", "文書を選択"],
    # bottom disclaimer
    "disc.1": ["本软件由 DeepSeek 编写加人工精修，代码完全开源，遵循 MIT 协议。",
               "本軟體由 DeepSeek 撰寫加人工精修，程式碼完全開源，遵循 MIT 協議。",
               "This software is written by DeepSeek with manual refinement. Its code is fully open source under the MIT licence.",
               "本ソフトウェアは DeepSeek が作成し、手作業で調整したものです。コードは完全にオープンソースで、MIT ライセンスに従います。"],
    "disc.2": ["本工具是 doc.renpy.cn 中文文档的离线镜像工具，与 Ren'Py 及相关官方文档无隶属关系。",
               "本工具是 doc.renpy.cn 中文文件的離線鏡像工具，與 Ren'Py 及相關官方文件無隸屬關係。",
               "This tool is an offline mirror of the doc.renpy.cn documentation. It is not affiliated with Ren'Py or the official documentation.",
               "本ツールは doc.renpy.cn のドキュメントのオフラインミラーです。Ren'Py および公式ドキュメントとは関係ありません。"],
    "disc.3": ["文档内容可能落后于线上版本或与线上不一致，请以官方页面为准，由本软件重新生成的索引与页面不保证完整无误。",
               "文件內容可能落後於線上版本或與線上不一致，請以官方頁面為準，由本軟體重新產生的索引與頁面不保證完整無誤。",
               "The documentation may lag behind or differ from the online version; the official pages are authoritative. Indexes and pages regenerated by this tool are not guaranteed to be complete or correct.",
               "ドキュメントの内容はオンライン版より古い場合や異なる場合があります。公式ページを正としてください。本ツールが再生成した索引とページの完全性・正確性は保証されません。"],
    "disc.4": ["更新功能只从公开页面下载文档到本地，不收集和上传任何个人数据。",
               "更新功能只從公開頁面下載文件到本機，不收集和上傳任何個人資料。",
               "The update feature only downloads documents from public pages to this machine. It does not collect or upload any personal data.",
               "更新機能は公開ページからドキュメントをこの PC にダウンロードするだけで、個人データの収集や送信は行いません。"],
}

UI_MARK_START = "<!--rp-ui-text-->"
UI_MARK_END = "<!--/rp-ui-text-->"


def ui_script():
    """Head script that gives every page the four-language UI dictionary.

    window.__rpUiLang is injected by the host before the page loads; without
    it (opening the mirror in a normal browser) Chinese is used."""
    data = json.dumps(UI_TEXT, ensure_ascii=False, separators=(",", ":"))
    return (UI_MARK_START + "<script>window.RP_UI=" + data + ";"
            "(function(){var m={'zh-CN':0,'zh-TW':1,'en':2,'ja':3};"
            "window.rpI=function(){var l=window.__rpUiLang||'zh-CN';var i=m[l];"
            "return i==null?0:i;};"
            "window.rpT=function(k){var v=window.RP_UI[k];"
            "return v?(v[window.rpI()]||v[0]):'';};"
            "window.rpF=function(k,a){return window.rpT(k).replace('{0}',a);};"
            "window.rpM=function(k,a){var s=window.rpT(k);"
            "for(var i=0;i<a.length;i++)s=s.split('{'+i+'}').join(a[i]);return s;};"
            "})();</script>" + UI_MARK_END)


# ---------------------------------------------------------------- rewrite
# Three floating buttons in one column at the bottom right: check-for-update,
# search, then the theme's own dark-mode switcher. Same size / colours / radius
# as `.theme-switcher`, with a lighter shadow.
FAB = r"""<style>
/* always reserve the scrollbar width, otherwise fixed-position buttons shift
   sideways on pages that are too short to scroll */
html{scrollbar-gutter:stable}
/* on wide screens the theme caps the content column and leaves a grey band
   on the right; use the full width instead */
.wy-nav-content-wrap{background-color:transparent}
.wy-nav-content{max-width:none}
#rp-docs-fab,#rp-update-fab,#rp-favorites-fab,#rp-search-fab,#rp-settings-fab,#rp-theme-fab{position:fixed;right:1.6em;z-index:3;
width:2.2em;height:2.2em;font-size:20px;border-radius:50%;border:none;
display:flex;align-items:center;justify-content:center;
background-color:#fcfcfc;color:#404040;line-height:1;text-decoration:none;
transition:all .3s ease-in-out}
#rp-search-fab{bottom:calc(1.4em + 2.2em + .55em)}
#rp-favorites-fab{bottom:6.9em}
#rp-update-fab{bottom:9.65em}
#rp-docs-fab{bottom:12.4em}
#rp-settings-fab{bottom:15.15em}
#rp-theme-fab{bottom:1.4em}
#rp-docs-fab svg,#rp-update-fab svg,#rp-search-fab svg,#rp-settings-fab svg,#rp-theme-fab svg{
width:1.08em;height:1.08em;display:block;fill:none;stroke:currentColor;
stroke-width:2.6;stroke-linecap:round;stroke-linejoin:round}
/* small list that pops out of the document-switch button */
/* 显示/隐藏靠 opacity + visibility 过渡，不用 display 切换，这样能淡入淡出；
   visibility 延迟到淡出结束才变，收起后也就点不到、Tab 也进不去 */
#rp-docs-panel{position:fixed;right:calc(1.6em + 2.2em + .55em);top:calc(100% - 14.6em);z-index:1001;
display:flex;flex-direction:column;font-size:20px;min-width:13em;max-width:19.5em;max-height:14.2em;overflow:hidden;padding:.3em;
background:#fcfcfc;border:1px solid #d7dee6;border-radius:8px;
box-shadow:0 6px 20px rgba(0,0,0,.18);
opacity:0;visibility:hidden;pointer-events:none;transform:translateY(4px);
transition:opacity .17s ease,transform .17s ease,visibility 0s linear .17s}
#rp-docs-panel.open{opacity:1;visibility:visible;pointer-events:auto;transform:none;
transition:opacity .17s ease,transform .17s ease,visibility 0s}
/* 打开时列表逐行淡入：.in 每次 open 重新挂一次（见 rpFadeRows），错峰延迟写在行上。
   只动 opacity、不动 transform——行往下平移哪怕 3px，都会让列表的 scrollHeight
   比 clientHeight 多出 3px，滚动条会在开场那 150ms 里闪一下 */
@keyframes rp-row-in{from{opacity:0}to{opacity:1}}
.rp-docs-list.in .rp-doc-item,.rp-docs-list.in .rp-docs-empty{animation:rp-row-in .2s ease both}
.rp-docs-head{flex:0 0 auto;display:flex;align-items:center;justify-content:space-between;gap:.5em;
padding:.35em .45em .45em .7em}
.rp-docs-title{font-size:.7em;color:#8a949e;letter-spacing:.03em;white-space:nowrap}
/* 选择 / 删除两档滑块：滑块停在当前一档上，选中的一档有颜色，另一档是灰的 */
.rp-docs-mode{position:relative;flex:0 0 auto;display:inline-flex;align-items:center;
background:#eef1f4;border-radius:999px;padding:2px;font-size:.7em;cursor:pointer;
user-select:none;-webkit-user-select:none;outline:none}
.rp-docs-mode:focus-visible{box-shadow:0 0 0 2px #9cc7e0}
.rp-docs-mode-thumb{position:absolute;left:2px;top:2px;width:calc(50% - 2px);
height:calc(100% - 4px);border-radius:999px;background:#fff;
box-shadow:0 1px 3px rgba(0,0,0,.16);transition:transform .2s ease-in-out}
.rp-docs-mode.del .rp-docs-mode-thumb{transform:translateX(100%)}
.rp-docs-mode-opt{position:relative;z-index:1;display:flex;align-items:center;justify-content:center;
padding:.32em .62em;color:#9aa4ae;transition:color .2s ease-in-out}
.rp-docs-mode-opt svg{width:1em;height:1em;display:block;fill:none;stroke:currentColor;
stroke-width:2.3;stroke-linecap:round;stroke-linejoin:round}
.rp-docs-mode-opt[data-mode="delete"] svg{stroke-width:1.9}
.rp-docs-mode:not(.del) .rp-docs-mode-opt[data-mode="select"]{color:#2980b9}
.rp-docs-mode.del .rp-docs-mode-opt[data-mode="delete"]{color:#e05555}
.rp-docs-list{flex:1 1 auto;min-height:0;overflow-y:auto;list-style:none;margin:0;padding:0}
.rp-docs-list::-webkit-scrollbar{width:6px}
.rp-docs-list::-webkit-scrollbar-track{background:transparent}
.rp-docs-list::-webkit-scrollbar-thumb{background:#ccd5de;border-radius:3px}
html[data-theme="dark"] .rp-docs-list::-webkit-scrollbar-thumb{background:#3d4a58}
.rp-doc-item{display:flex;align-items:center;gap:.45em;padding:.5em .62em;border-radius:6px;
cursor:pointer;font-size:.8em;color:#2c3945;line-height:1.3;white-space:nowrap}
.rp-doc-item:hover{background:#eef6fb}
.rp-doc-item.current{font-weight:600}
/* 左边这个 1em 的槽同时放着对勾和垃圾桶：选中状态显示蓝色 ✓，删除状态
   交叉淡入淡出成红色垃圾桶；两者同一个盒子、同一个居中方式、同一个时长 */
.rp-doc-ico{position:relative;flex:0 0 2.05em;height:2.05em;font-size:1.05em}
.rp-doc-dlc{position:absolute;left:0;top:0;width:100%;height:100%;display:flex;
align-items:center;justify-content:center;pointer-events:none}
.rp-doc-dlc .rp-doc-ring,.rp-doc-dlc .rp-doc-dl{pointer-events:auto}
.rp-doc-dlc .rp-doc-ring,.rp-doc-dlc .rp-ring{width:1.9em;height:1.9em}
.rp-doc-item.nodl{cursor:default}
.rp-doc-item.nodl .rp-doc-name{color:#8a949e}
html[data-theme="dark"] .rp-doc-item.nodl .rp-doc-name{color:#7d8894}
.rp-doc-check,.rp-doc-del{position:absolute;left:0;top:0;width:100%;height:100%;
display:flex;align-items:center;justify-content:center;color:#2980b9;
transition:opacity .2s ease-in-out,color .2s ease-in-out}
.rp-doc-del{opacity:0;color:#e04b4b;pointer-events:none}
.rp-docs-list.del .rp-doc-check{opacity:0}
.rp-docs-list.del .rp-doc-del{opacity:1;pointer-events:auto;cursor:pointer}
.rp-doc-del:hover{color:#c23434}
.rp-doc-del svg{width:1em;height:1em;display:block;fill:none;stroke:currentColor;
stroke-width:1.9;stroke-linejoin:round;stroke-linecap:round}
/* 切到删除模式：右槽里的下载箭头淡出（它会挡住垃圾桶），时长和缓动跟
   打勾 ↔ 垃圾桶的交叉淡入淡出一致（.2s ease-in-out）；
   但正在下载的圆环照旧显示、照旧能点暂停/继续。
   淡出期间就点不到了，淡完之后 visibility 才变，Tab 也不会停在它上面 */
.rp-docs-list.del .rp-doc-dlc .rp-doc-dl{opacity:0;visibility:hidden;pointer-events:none;
transition:opacity .2s ease-in-out,visibility 0s linear .2s}
/* 圆环占了这一格时，垃圾桶也退场，免得两个图标叠在一起 */
.rp-docs-list.del .rp-doc-item.busy .rp-doc-del{opacity:0;pointer-events:none}
.rp-docs-list.del .rp-doc-item{cursor:default}
.rp-docs-list.del .rp-doc-item:hover{background:#faf2f2}
.rp-docs-empty{font-size:.75em;color:#8a949e;padding:.7em .7em;text-align:center}
.rp-doc-name{flex:1 1 auto;overflow:hidden;text-overflow:ellipsis}
.rp-doc-ver{flex:0 0 auto;color:#8a949e;font-size:.9em;font-weight:400}
/* 未下载的文档：右侧换成下载按钮；下载中显示灰字 */
.rp-doc-act{flex:0 0 auto;display:flex;align-items:center;justify-content:flex-end;min-width:1.2em}
.rp-doc-dl{display:flex;align-items:center;justify-content:center;width:1.5em;height:1.5em;
border-radius:5px;color:#2980b9;cursor:pointer;
transition:background .12s,color .12s,opacity .2s ease-in-out,visibility 0s}
.rp-doc-dl:hover{background:#e6f2fa;color:#1f6a99}
.rp-doc-dl:focus-visible{outline:2px solid #9cc7e0;outline-offset:1px}
.rp-doc-dl svg{width:1.1em;height:1.1em;display:block;fill:none;stroke:currentColor;
stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
.rp-doc-busy{font-size:.82em;color:#8a949e;white-space:nowrap}
html[data-theme="dark"] .rp-doc-dl{color:#8ec8ea}
html[data-theme="dark"] .rp-doc-dl:hover{background:#243040;color:#bfe2f5}
html[data-theme="dark"] .rp-doc-dl:focus-visible{outline-color:#4f7f9e}
html[data-theme="dark"] .rp-doc-busy{color:#93a0ad}
.rp-doc-ring{position:relative;display:flex;align-items:center;justify-content:center;
width:2.0em;height:2.0em;cursor:pointer;border-radius:50%}
.rp-doc-ring .rp-ring{width:2.0em;height:2.0em;display:block}
.rp-ring-track{fill:none;stroke:#dbe3ea;stroke-width:2.4}
.rp-ring-fill{fill:none;stroke:#2980b9;stroke-width:2.4;stroke-linecap:round;
transition:stroke-dashoffset .3s linear}
.rp-ring-glyph{position:absolute;left:0;top:0;width:100%;height:100%;display:flex;
align-items:center;justify-content:center;opacity:0;transition:opacity .12s;
color:#2980b9;pointer-events:none}
.rp-ring-glyph svg{width:.86em;height:.86em;display:block;fill:currentColor;stroke:none}
.rp-doc-ring:hover .rp-glyph-pause{opacity:1}
.rp-doc-ring.paused .rp-glyph-play{opacity:1}
.rp-doc-ring.paused .rp-glyph-pause{opacity:0}
.rp-doc-ring.paused .rp-ring-fill{stroke:#8a949e}
.rp-doc-ring:focus-visible{outline:2px solid #9cc7e0;outline-offset:1px}
html[data-theme="dark"] .rp-ring-track{stroke:#3a4552}
html[data-theme="dark"] .rp-ring-fill{stroke:#8ec8ea}
html[data-theme="dark"] .rp-ring-glyph{color:#8ec8ea}
html[data-theme="dark"] .rp-doc-ring.paused .rp-ring-fill{stroke:#93a0ad}
html[data-theme="dark"] .rp-doc-ring:focus-visible{outline-color:#4f7f9e}
html[data-theme="dark"] #rp-docs-panel{background:#1b232c;border-color:#333d49;color:#c9d3dd}
html[data-theme="dark"] .rp-docs-title{color:#93a0ad}
html[data-theme="dark"] .rp-docs-mode{background:#243040}
html[data-theme="dark"] .rp-docs-mode:focus-visible{box-shadow:0 0 0 2px #4f7f9e}
html[data-theme="dark"] .rp-docs-mode-thumb{background:#39434f;box-shadow:0 1px 3px rgba(0,0,0,.45)}
html[data-theme="dark"] .rp-docs-mode-opt{color:#7d8894}
html[data-theme="dark"] .rp-docs-mode:not(.del) .rp-docs-mode-opt[data-mode="select"]{color:#8ec8ea}
html[data-theme="dark"] .rp-docs-mode.del .rp-docs-mode-opt[data-mode="delete"]{color:#f08a8a}
html[data-theme="dark"] .rp-doc-item{color:#dbe3ea}
html[data-theme="dark"] .rp-doc-item:hover{background:#243040}
html[data-theme="dark"] .rp-docs-list.del .rp-doc-item:hover{background:#2a2626}
html[data-theme="dark"] .rp-doc-check{color:#8ec8ea}
html[data-theme="dark"] .rp-doc-del{color:#f08a8a}
html[data-theme="dark"] .rp-doc-del:hover{color:#f6b0b0}
html[data-theme="dark"] .rp-docs-empty{color:#93a0ad}
html[data-theme="dark"] .rp-doc-ver{color:#93a0ad}
#rp-favorites-fab svg{width:1.06em;height:1.06em;display:block;fill:none;
stroke:currentColor;stroke-width:1.8;stroke-linejoin:round;stroke-linecap:round}
html[data-theme="dark"] #rp-docs-fab,html[data-theme="dark"] #rp-update-fab,html[data-theme="dark"] #rp-favorites-fab,
html[data-theme="dark"] #rp-search-fab,html[data-theme="dark"] #rp-settings-fab,html[data-theme="dark"] #rp-theme-fab{
background-color:#0b0c0d;color:var(--dark-text-color)}
/* the floating buttons share a lighter shadow than the theme default */
#rp-docs-fab,#rp-update-fab,#rp-favorites-fab,#rp-search-fab,#rp-settings-fab,#rp-theme-fab,.theme-switcher{box-shadow:0 1px 4px rgba(0,0,0,.18)}
html[data-theme="dark"] #rp-docs-fab,html[data-theme="dark"] #rp-update-fab,html[data-theme="dark"] #rp-favorites-fab,
html[data-theme="dark"] #rp-search-fab,html[data-theme="dark"] #rp-settings-fab,html[data-theme="dark"] #rp-theme-fab,
html[data-theme="dark"] .theme-switcher{box-shadow:0 1px 4px rgba(0,0,0,.45)}
/* recent search queries shown under the search box */
.rp-history{position:fixed;z-index:1001;background:#fcfcfc;border:1px solid #d7dee6;
border-radius:4px;box-shadow:0 4px 14px rgba(0,0,0,.18);display:none;overflow:hidden;
font-size:14px;color:#404040}
.rp-history.open{display:block}
.rp-history .rp-hist-item{display:flex;align-items:center;gap:6px;padding:7px 10px;
cursor:pointer;white-space:nowrap;border-bottom:1px solid #eef1f4}
.rp-history .rp-hist-item:last-child{border-bottom:none}
.rp-history .rp-hist-item:hover{background:#eef6fb}
.rp-history .rp-hist-text{flex:1 1 auto;min-width:0;overflow:hidden;
text-overflow:ellipsis;text-align:left}
.rp-history .rp-hist-del{flex:0 0 auto;border:none;background:none;color:#9aa4ae;
cursor:pointer;font-size:15px;line-height:1;padding:2px 4px;border-radius:3px}
.rp-history .rp-hist-del:hover{background:#e3e8ee;color:#e05a5a}
html[data-theme="dark"] .rp-history{background:#1b232c;border-color:#333d49;color:#c9d3dd}
html[data-theme="dark"] .rp-history .rp-hist-item{border-color:#2b3540}
html[data-theme="dark"] .rp-history .rp-hist-item:hover{background:#243040}
/* disclaimer at the bottom of the content column */
.rp-disclaimer{margin:3em 0 0;padding:1em 0 0;border-top:1px solid #e3e8ee;
color:#8a949e;font-size:12.5px;line-height:1.75}
.rp-disclaimer p{margin:0 0 .35em}
html[data-theme="dark"] .rp-disclaimer{color:#7d8894;border-color:#333d49}
/* the star, the permalink and the globe form one equal row: identical box,
   identical glyph size, identical gap, aligned on the same line */
.rp-star,.rp-copy-link,.rp-copy-text,.rst-content a.headerlink.headerlink{
width:18px;height:18px;padding:0;margin:0 0 0 4px;border:none;background:none;
box-sizing:border-box;cursor:pointer;vertical-align:middle;
line-height:18px;text-align:center;font-size:15px;
/* the four icons fade in and out with one timing */
transition:opacity .2s ease-in-out,color .2s ease-in-out}
.rp-star,.rp-copy-link,.rp-copy-text{opacity:0;color:inherit;display:inline-flex;
align-items:center;justify-content:center}
/* copy-permalink button: hidden until its heading is hovered, exactly like
   the theme's own permalink icon; colour is copied from that icon at runtime */
.rst-content a.headerlink.headerlink{display:inline-block}
*:hover>.rp-copy-link,.rp-copy-link:focus-visible{opacity:1}
.rp-copy-link svg{width:15px;height:15px;display:block}
/* favourite star: quiet until the heading is hovered, but always visible and
   yellow once the entry is saved. No transform on hover: scaling makes the
   icon look like it jumps when it takes focus after a click. */
.rp-star,.rp-copy-text{color:var(--rp-icon,inherit)}
.rp-copy-text svg{width:15px;height:15px;display:block;fill:none;
stroke:currentColor;stroke-width:1.7;stroke-linejoin:round;stroke-linecap:round}
.rp-star svg{width:15px;height:15px;display:block;fill:none;stroke:currentColor;
stroke-width:1.9;stroke-linejoin:round;stroke-linecap:round}
/* keyboard focus gets a ring drawn outside the box, so nothing shifts */
.rp-star:focus-visible,.rp-copy-link:focus-visible,.rp-copy-text:focus-visible{
outline:2px solid #9cc7e0;outline-offset:1px;border-radius:4px}
html[data-theme="dark"] .rp-star:focus-visible,
html[data-theme="dark"] .rp-copy-link:focus-visible{outline-color:#4f7f9e}
.rp-star.rp-on,.rp-star.rp-on:hover{opacity:1;color:#efaa00}
.rp-star.rp-on svg{fill:currentColor}
*:hover>.rp-star,.rp-star:focus-visible,*:hover>.rp-copy-text,.rp-copy-text:focus-visible{opacity:1}
html[data-theme="dark"] .rp-star.rp-on{color:#f4b41f}
.rp-toast{position:fixed;left:50%;bottom:44px;transform:translate(-50%,10px);z-index:1200;
background:rgba(32,40,48,.92);color:#fff;font-size:13px;padding:8px 16px;border-radius:16px;
opacity:0;pointer-events:none;transition:opacity .2s,transform .2s}
.rp-toast.open{opacity:1;transform:translate(-50%,0)}
/* image lightbox: keeps the reader inside the page instead of navigating
   to the raw image file (the app window has no back button) */
.rp-lightbox{position:fixed;inset:0;z-index:2000;background:rgba(10,12,14,.88);
display:flex;align-items:center;justify-content:center;padding:26px;cursor:zoom-out}
.rp-lightbox img{max-width:100%;max-height:100%;background:#fff;
box-shadow:0 10px 40px rgba(0,0,0,.55)}
.rp-lightbox-close{position:fixed;top:16px;right:20px;width:40px;height:40px;
border:none;border-radius:50%;background:rgba(255,255,255,.88);color:#333;
font-size:24px;line-height:1;cursor:pointer;padding:0}
.rp-lightbox-close:hover{background:#fff}
/* clicking a link never leaves a purple trail: visited looks like a normal link */
.rst-content a:visited{color:#2980b9}
html[data-theme="dark"] .rst-content a:visited{color:var(--dark-link-color)}/* back / home / forward: fixed above the content column, never scrolls */
#rp-navbar{position:fixed;top:26px;left:50%;transform:translateX(-50%);
width:258px;height:26px;display:flex;gap:6px;z-index:70;margin:0;padding:0;
background:rgba(252,252,252,.88);border-radius:0 0 5px 5px}
#rp-navbar button{flex:1 1 0;height:26px;padding:0;font-size:13px;line-height:1;
display:flex;align-items:center;justify-content:center;
border:1px solid #d7dee6;border-radius:4px;background:#fcfcfc;color:#404040;
cursor:pointer;transition:background .12s,border-color .12s,color .12s;
box-shadow:0 1px 3px rgba(0,0,0,.07)}
#rp-navbar button:hover{background:#eef6fb;border-color:#9cc7e0;color:#2980b9}
#rp-navbar button:active{background:#e3f1f9}
#rp-navbar button:disabled{opacity:.38;cursor:default}
#rp-navbar button:disabled:hover{background:#fcfcfc;border-color:#d7dee6;color:#404040}
html[data-theme="dark"] #rp-navbar{background:rgba(11,12,13,.88)}
html[data-theme="dark"] #rp-navbar button{background:#0b0c0d;color:#c1c1c1;border-color:#39434f}
html[data-theme="dark"] #rp-navbar button:hover{background:#1b232c;color:#8ec8ea;border-color:#3f4b58}
@media print{#rp-update-fab,#rp-docs-fab,#rp-docs-panel,#rp-settings-fab,#rp-theme-fab,#rp-favorites-fab,#rp-search-fab,.rp-history,.rp-copy-link,.rp-copy-text,.rp-star,.rp-toast,.rp-lightbox,#rp-navbar{display:none}}
</style>
<a id="rp-docs-fab" href="#" title="切换文档" aria-label="切换文档"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M4 5.6c2.6-1 5.2-1 8 .4 2.8-1.4 5.4-1.4 8-.4v12.6c-2.6-1-5.2-1-8 .4-2.8-1.4-5.4-1.4-8-.4z"/><path d="M12 6v12.6"/></svg></a>
<div id="rp-docs-panel" role="dialog" aria-label="切换文档">
<div class="rp-docs-head">
<div class="rp-docs-title">切换文档</div>
<div id="rp-docs-mode" class="rp-docs-mode" role="switch" aria-checked="false" aria-label="选择或删除文档" tabindex="0"><span class="rp-docs-mode-thumb"></span><span class="rp-docs-mode-opt" data-mode="select" title="选择"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M4.6 12.7l5 5L19.4 6.8"/></svg></span><span class="rp-docs-mode-opt" data-mode="delete" title="删除"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M3.6 6.2h16.8M9.2 6.2V3.6h5.6v2.6M6.4 6.2l.9 14.4h9.4l.9-14.4M10 10.2v6.4M14 10.2v6.4"/></svg></span></div>
</div>
<ul id="rp-docs-list" class="rp-docs-list"></ul>
</div>
<a id="rp-settings-fab" href="#" title="设置" aria-label="设置"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/></svg></a>
<a id="rp-update-fab" href="#" title="检查更新" aria-label="检查更新"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M20 12a8 8 0 1 1-2.34-5.66"/><path d="M20 4.5V9h-4.5"/></svg></a>
<a id="rp-favorites-fab" href="favorites.html" title="收藏" aria-label="收藏"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M12.00 3.20 L14.65 8.96 L20.94 9.70 L16.28 13.99 L17.53 20.20 L12.00 17.10 L6.47 20.20 L7.72 13.99 L3.06 9.70 L9.35 8.96 Z"></path></svg></a>
<a id="rp-search-fab" href="search.html" title="搜索文档" aria-label="搜索文档"><svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="10.5" cy="10.5" r="6"/><path d="M15 15l4.6 4.6"/></svg></a>
<script>(function(){
// interface language: the host injects window.__rpUiLang before this runs
function U(k){return window.rpT?window.rpT(k):'';}
function UF(k,a){return window.rpF?window.rpF(k,a):'';}
(function(){
var L={'rp-docs-fab':'fab.docs','rp-settings-fab':'fab.settings','rp-update-fab':'fab.update',
'rp-favorites-fab':'fab.favorites','rp-search-fab':'fab.search'};
for(var id in L){var e=document.getElementById(id);
if(e){var t=U(L[id]);e.title=t;e.setAttribute('aria-label',t);}}
var panel=document.getElementById('rp-docs-panel');
if(panel)panel.setAttribute('aria-label',U('fab.docs'));
var head=document.querySelector('.rp-docs-title');
if(head)head.textContent=U('fab.docs');
var mode=document.getElementById('rp-docs-mode');
if(mode)mode.setAttribute('aria-label',U('docs.modeAria'));
var sel=document.querySelector('.rp-docs-mode-opt[data-mode=select]');
if(sel)sel.title=U('docs.select');
var del=document.querySelector('.rp-docs-mode-opt[data-mode=delete]');
if(del)del.title=U('docs.delete');
// the theme's sidebar search box is part of the visible chrome too
var side=document.querySelector('#rtd-search-form input[name="q"]');
if(side){side.placeholder=U('search.ph');side.setAttribute('aria-label',U('search.ph'));}
})();
(function(){
var s=document.getElementById('rp-search');
if(s){
var h=s.querySelector('h1');if(h)h.textContent=U('search.title');
var q=document.getElementById('rp-mobile-q');
if(q){q.placeholder=U('search.ph');q.setAttribute('aria-label',U('search.ph'));}
var hint=s.querySelector('.rp-hint');if(hint)hint.textContent=U('search.hint');
var et=s.querySelector('.rp-empty-tip');if(et)et.textContent=U('search.empty');
}
var f=document.getElementById('rp-favorites');
if(f){
var h=f.querySelector('h1');if(h)h.textContent=U('fav.title');
var q=document.getElementById('fv-q');
if(q){q.placeholder=U('fav.ph');q.setAttribute('aria-label',U('fav.ph'));}
var c=document.getElementById('fv-clear');
if(c){c.title=U('fav.clear');c.setAttribute('aria-label',U('fav.clear'));}
var hint=f.querySelector('.fv-hint');if(hint)hint.textContent=U('fav.hint');
var et=f.querySelector('.fv-empty-tip');if(et)et.textContent=U('fav.empty');
}
var p=document.getElementById('rp-picker');
if(p){
var h=p.querySelector('.rp-pick-title');if(h)h.textContent=U('pick.title');
var list=document.getElementById('rp-pick-list');if(list)list.setAttribute('aria-label',U('pick.aria'));
}
var disc=document.getElementById('rp-disclaimer');
if(disc){
disc.innerHTML='';
for(var i=1;i<=4;i++){
var p=document.createElement('p');p.textContent=U('disc.'+i);disc.appendChild(p);}
}
})();
(function(){
var page=document.getElementById('rp-search')?'search'
:document.getElementById('rp-favorites')?'favorites'
:document.getElementById('rp-picker')?'picker':null;
if(!page)return;
var t=document.title||'';var d=t.lastIndexOf('—');
var suffix=d>=0?t.substring(d+1).trim():'';
document.title=U('page.'+page)+(suffix?' — '+suffix:'');
var crumb=document.querySelector('.wy-breadcrumbs .breadcrumb-item.active');
if(crumb)crumb.textContent=U('page.'+page);
})();
document.addEventListener('keydown',function(e){
if(e.key!=='/'||e.metaKey||e.ctrlKey||e.altKey)return;
var a=document.activeElement;
if(a&&/^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName))return;
e.preventDefault();
var i=document.querySelector('#rtd-search-form input[name="q"]');
if(i){i.focus();i.select();}});
// on the search page itself the button just focuses the query box
var fab=document.getElementById('rp-search-fab');
if(fab){fab.addEventListener('click',function(e){
if(!document.getElementById('rp-search'))return;
e.preventDefault();
var t=document.querySelector('#rtd-search-form input[name="q"]');
if(window.matchMedia('(max-width: 900px)').matches){
var m=document.getElementById('rp-mobile-q');if(m)t=m;}
if(t){t.focus();t.select();}});}
// on the favourites page the star only focuses its own filter box
var ffab=document.getElementById('rp-favorites-fab');
if(ffab){ffab.addEventListener('click',function(e){
if(!document.getElementById('rp-favorites'))return;
e.preventDefault();
var q=document.getElementById('fv-q');
if(q){q.focus();q.select();}});}
// 浮层列表每次打开都逐行淡入一次：给每行一个 22ms 的错峰延迟（超过 8 行就统一等 176ms，
// 免得长列表最后几行等太久），再把 .in 摘掉、强制回流、挂回去，动画才会重播。
function rpFadeRows(box){
if(!box)return;
var rows=box.children;
for(var i=0;i<rows.length;i++)rows[i].style.animationDelay=(i>8?176:i*22)+'ms';
box.classList.remove('in');
void box.offsetWidth;
box.classList.add('in');}
// document switcher: a small list pops out of the button above "update".
// Every document in the catalogue is listed; the ones that are not downloaded
// yet show a download button instead of a version. Data of each document is
// kept apart, see RP_DOC_ID.
(function(){
var DOCS=__RP_DOCS_JS__;
var DOC_ID='__RP_DOC_ID__';
window.RP_DOCS=DOCS;
window.RP_DOC_ID=DOC_ID;
var fab=document.getElementById('rp-docs-fab');
var panel=document.getElementById('rp-docs-panel');
var list=document.getElementById('rp-docs-list');
var mode=document.getElementById('rp-docs-mode');
if(!fab||!panel||!list)return;
var GONE='rp-docs-gone',built=false,cur='select',polls={};
// deleted documents are remembered, so the list does not grow back on reload
function gone(){try{var a=JSON.parse(localStorage.getItem(GONE)||'[]');
return Object.prototype.toString.call(a)==='[object Array]'?a:[];}catch(e){return[];}}
function bury(id){try{var a=gone();if(a.indexOf(id)<0)a.push(id);
localStorage.setItem(GONE,JSON.stringify(a));}catch(e){}}
function byId(id){for(var i=0;i<DOCS.length;i++)if(DOCS[i].id===id)return DOCS[i];return null;}
function allGone(){
if(!DOCS.length)return false;
var h=gone();
for(var i=0;i<DOCS.length;i++)if(h.indexOf(DOCS[i].id)<0)return false;
return true;}
// 一份都没下载时，文档页统一去空白的选择页（选择页自己不再跳）。
// 选择页在站点根目录：它得在文档目录被整个删掉以后还活着
if(!/\/picker\.html$/.test(location.pathname)&&allGone()){location.replace('/picker.html');return;}
function setMode(m){
cur=m==='delete'?'delete':'select';
var del=cur==='delete';
if(mode){mode.className='rp-docs-mode'+(del?' del':'');
mode.setAttribute('aria-checked',del?'true':'false');}
list.className='rp-docs-list'+(del?' del':'');}
function emptyCheck(){
var n=list.querySelectorAll('.rp-doc-item').length;
var e=list.querySelector('.rp-docs-empty');
if(!n&&!e){var li=document.createElement('li');
li.className='rp-docs-empty';li.textContent=U('docs.empty');
list.appendChild(li);}
if(n&&e)e.parentNode.removeChild(e);}
function drop(li){
var id=li.getAttribute('data-id');
if(id)bury(id);
if(li.parentNode)li.parentNode.removeChild(li);
emptyCheck();
// 删掉最后一份：没有文档可读了，去屏幕正中的空白选择页
if(!list.querySelector('.rp-doc-item'))location.replace('/picker.html');}
// 宿主删掉本地文件后回调：这一行还在，只是变成“没下载”的样子
window.__rpDocDeleted=function(id){
var d=byId(id);if(!d)return;
d.dl=false;d.busy=false;d.paused=false;d.pct=0;d.version='';
var li=rowOf(id);if(li)paint(d,li);};

// ---- 下载 -------------------------------------------------------------
var DL_ICON='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">'
+'<path d="M12 4v10.4M7.8 10.2L12 14.4l4.2-4.2M5.4 19.4h13.2"/></svg>';
function loadCatalog(){
try{
return fetch('../docs.json',{cache:'no-store'}).then(function(r){return r.ok?r.json():[];})
.catch(function(){return [];});
}catch(e){return Promise.resolve([]);}}
function applyCatalog(a){
if(!a||!a.length)return false;
for(var i=0;i<DOCS.length;i++)for(var j=0;j<a.length;j++)
if(a[j].id===DOCS[i].id){
DOCS[i].dl=!!a[j].downloaded;
DOCS[i].version=a[j].version||DOCS[i].version;}
return true;}
function rowOf(id){
var rows=list.querySelectorAll('.rp-doc-item');
for(var i=0;i<rows.length;i++)if(rows[i].getAttribute('data-id')===id)return rows[i];
return null;}
function fetchVersion(d,span){
if(d.version){span.textContent=d.version;return;}
var url=d.cur?'version.txt':('../'+d.id+'/version.txt');
fetch(url).then(function(r){return r.ok?r.text():'';}).then(function(t){
var v=(t||'').trim();if(!v)return;d.version=v;span.textContent=v;}).catch(function(){});}
var RING_C=50.27;
var PAUSE_ICON='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">'
+'<path d="M6.0 5.2h3.4v13.6H6.0zM14.6 5.2h3.4v13.6h-3.4z"/></svg>';
var PLAY_ICON='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">'
+'<path d="M8.2 5.2l11.6 6.8-11.6 6.8z"/></svg>';
function ring(d,li){
var w=document.createElement('span');
w.className='rp-doc-ring'+(d.paused?' paused':'');
w.setAttribute('role','button');w.setAttribute('tabindex','0');
w.title=d.paused?U('docs.resume'):U('docs.pause');
w.innerHTML='<svg class="rp-ring" viewBox="0 0 24 24" aria-hidden="true" focusable="false">'
+'<circle class="rp-ring-track" cx="12" cy="12" r="8"/>'
+'<circle class="rp-ring-fill" cx="12" cy="12" r="8" stroke-dasharray="'+RING_C+'"'
+' stroke-dashoffset="'+RING_C+'" transform="rotate(-90 12 12)"/></svg>'
+'<span class="rp-ring-glyph rp-glyph-pause">'+PAUSE_ICON+'</span>'
+'<span class="rp-ring-glyph rp-glyph-play">'+PLAY_ICON+'</span>';
function toggle(ev){
ev.preventDefault();ev.stopPropagation();
if(!(window.chrome&&window.chrome.webview))return;
window.chrome.webview.postMessage((d.paused?'rp-doc-resume\n':'rp-doc-pause\n')+d.id);
d.paused=!d.paused;paint(d,li);}
w.addEventListener('click',toggle);
w.addEventListener('keydown',function(ev){
if(ev.key==='Enter'||ev.key===' '){toggle(ev);}});
updateRing(w,d);
return w;}
// 进度是一秒两次刷新的：只改属性，别重建节点，否则鼠标悬停会闪
function updateRing(w,d){
var f=w.querySelector('.rp-ring-fill');
if(f){
var pct=Math.max(0,Math.min(100,d.pct||0));
f.setAttribute('stroke-dashoffset',(RING_C*(1-pct/100)).toFixed(1));}
var cls='rp-doc-ring'+(d.paused?' paused':'');
if(w.className!==cls)w.className=cls;
var t=d.paused?U('docs.resume'):U('docs.pause');
if(w.title!==t)w.title=t;}
function dlBtn(d,li){
var btn=document.createElement('span');
btn.className='rp-doc-dl';btn.setAttribute('role','button');btn.setAttribute('tabindex','0');
btn.title=U('docs.download');btn.innerHTML=DL_ICON;
btn.addEventListener('click',function(ev){ev.preventDefault();ev.stopPropagation();startDownload(d,li);});
btn.addEventListener('keydown',function(ev){
if(ev.key==='Enter'||ev.key===' '){ev.preventDefault();ev.stopPropagation();startDownload(d,li);}});
return btn;}
function paint(d,li){
var ico=li.querySelector('.rp-doc-ico');
var act=li.querySelector('.rp-doc-act');
if(!ico)return;
li.className='rp-doc-item'+(d.cur?' current':'')+(d.dl?'':' nodl')+(d.busy?' busy':'');
var chk=ico.querySelector('.rp-doc-check');
var bin=ico.querySelector('.rp-doc-del');
var dlc=ico.querySelector('.rp-doc-dlc');
if(!dlc)return;
if(d.dl){
if(chk)chk.style.display='';
if(bin)bin.style.display='';
if(dlc.firstChild)dlc.innerHTML='';
if(act){
act.innerHTML='';
var v=document.createElement('span');v.className='rp-doc-ver';
act.appendChild(v);fetchVersion(d,v);}
return;}
// 没下载：打勾的位置换成下载 / 进度圆环，名字变灰
if(chk)chk.style.display='none';
if(bin)bin.style.display='none';
if(act)act.innerHTML='';
if(d.busy){
var w=dlc.querySelector('.rp-doc-ring');
if(w){updateRing(w,d);}
else{dlc.innerHTML='';dlc.appendChild(ring(d,li));}
}else if(!dlc.querySelector('.rp-doc-dl')){
dlc.innerHTML='';dlc.appendChild(dlBtn(d,li));}}
function startDownload(d,li){
if(d.busy||d.dl)return;
if(!(window.chrome&&window.chrome.webview)){
window.open('https://www.renpy.org/doc/html/','_blank');return;}
d.busy=true;d.pct=0;d.paused=false;paint(d,li);
window.chrome.webview.postMessage('rp-doc-download\n'+d.id);}
// 宿主每 0.5 秒推一次：百分比 + 是否暂停
window.__rpDocProgress=function(id,pct,paused){
var d=byId(id);if(!d)return;
d.busy=true;d.pct=pct;d.paused=!!paused;
var li=rowOf(id);if(li)paint(d,li);};
window.__rpDocReady=function(id){
var d=byId(id);if(!d)return;
d.dl=true;d.busy=false;d.paused=false;
var li=rowOf(id);if(li)paint(d,li);};
window.__rpDocFailed=function(id){
var d=byId(id);if(!d)return;
d.busy=false;d.paused=false;d.pct=0;
var li=rowOf(id);if(li)paint(d,li);};

function addVersion(){}
function icons(d){
var box=document.createElement('span');box.className='rp-doc-ico';
var chk=document.createElement('span');chk.className='rp-doc-check';
chk.textContent=d.cur?'✓':'';
var bin=document.createElement('span');bin.className='rp-doc-del';
bin.setAttribute('role','button');bin.setAttribute('title',U('docs.deleteDoc'));
bin.innerHTML='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">'
+'<path d="M3.6 6.2h16.8M9.2 6.2V3.6h5.6v2.6M6.4 6.2l.9 14.4h9.4l.9-14.4'
+'M10 10.2v6.4M14 10.2v6.4"/></svg>';
var dlc=document.createElement('span');dlc.className='rp-doc-dlc';
box.appendChild(chk);box.appendChild(bin);box.appendChild(dlc);
return box;}
function build(){
if(built)return;built=true;
var hide=gone();
for(var i=0;i<DOCS.length;i++){
(function(d){
if(hide.indexOf(d.id)>=0)return;
var li=document.createElement('li');
li.className='rp-doc-item'+(d.cur?' current':'');
li.setAttribute('data-id',d.id);
var ic=icons(d);
var nm=document.createElement('span');
nm.className='rp-doc-name';
nm.textContent=d.name;
var act=document.createElement('span');act.className='rp-doc-act';
li.appendChild(ic);li.appendChild(nm);li.appendChild(act);
paint(d,li);
li.addEventListener('click',function(ev){
ev.preventDefault();ev.stopPropagation();
if(cur==='delete')return;   // 删除状态下只有左边的垃圾桶会删
if(!d.dl)return;            // 还没下载：只有右边的下载按钮能用
if(d.cur){close();return;}
// 切换文档：桌面版交给宿主（直接切，没有确认框），浏览器里直接跳
if(window.chrome&&window.chrome.webview){
window.chrome.webview.postMessage('rp-switch-doc\n'+d.id);}
else{location.href=d.home;}
close();});
ic.querySelector('.rp-doc-del').addEventListener('click',function(ev){
ev.preventDefault();ev.stopPropagation();
if(cur!=='delete')return;
// 桌面版交给宿主弹原生确认框（和“检查更新”的提示同一套），浏览器里退回 window.confirm
if(window.chrome&&window.chrome.webview){
window.chrome.webview.postMessage('rp-confirm-delete\n'+d.id+'\n'+d.name);return;}
if(window.confirm(UF('docs.confirm',d.name)))
drop(li);});
list.appendChild(li);
})(DOCS[i]);}
emptyCheck();}
function open(){build();panel.classList.add('open');rpFadeRows(list);
loadCatalog().then(function(a){
if(!applyCatalog(a))return;
var rows=list.querySelectorAll('.rp-doc-item');
for(var i=0;i<rows.length;i++){
var d=byId(rows[i].getAttribute('data-id'));
if(d)paint(d,rows[i]);}});
// 另一个浮层（测试列表）打开时，这个要收起来，两者位置会重叠
document.dispatchEvent(new CustomEvent('rp-panel',{detail:'rp-docs-panel'}));}
function close(){panel.classList.remove('open');}
document.addEventListener('rp-panel',function(e){if(e.detail!=='rp-docs-panel')close();});
if(mode){
mode.addEventListener('click',function(e){
e.preventDefault();e.stopPropagation();
var n=e.target,ds=null;
while(n&&n!==mode){var m=n.getAttribute&&n.getAttribute('data-mode');
if(m){ds=m;break;}n=n.parentNode;}
setMode(ds==='select'||ds==='delete'?ds:(cur==='delete'?'select':'delete'));});
mode.addEventListener('keydown',function(e){
if(e.key===' '||e.key==='Enter'){e.preventDefault();e.stopPropagation();
setMode(cur==='delete'?'select':'delete');}});}
fab.addEventListener('click',function(e){
e.preventDefault();e.stopPropagation();
if(panel.classList.contains('open'))close();else open();});
document.addEventListener('mousedown',function(e){
if(!panel.classList.contains('open'))return;
if(panel.contains(e.target)||fab.contains(e.target))return;
close();});
document.addEventListener('keydown',function(e){if(e.key==='Escape')close();});
})();
// 设置按钮（齿轮，在切换文档上方）：设置窗由桌面端绘制，浏览器里没有
var setfab=document.getElementById('rp-settings-fab');
if(setfab){
if(!(window.chrome&&window.chrome.webview)){setfab.style.display='none';}
else{setfab.addEventListener('click',function(e){
e.preventDefault();window.chrome.webview.postMessage('rp-open-settings');});}
}
// update checks only work inside the desktop app
var upd=document.getElementById('rp-update-fab');
if(upd){
if(!(window.chrome&&window.chrome.webview)){upd.style.display='none';}
else{upd.addEventListener('click',function(e){
e.preventDefault();window.chrome.webview.postMessage('rp-check-update');});}
}
// recent searches: replaces the browser's "saved information" popup
(function(){
var DOC_ID=window.RP_DOC_ID||'zh-CN';
var HM=parseInt(window.__rpSearchHistoryMax,10);
if(isNaN(HM))HM=5;
HM=Math.max(0,Math.min(10,HM));
var KEY='rp-search-history:'+DOC_ID,MAX=HM;
// 老版本没有按文档分家：第一次用新键时，把旧的中文数据搬过来
try{if(DOC_ID==='zh-CN'&&!localStorage.getItem(KEY)&&localStorage.getItem('rp-search-history'))
localStorage.setItem(KEY,localStorage.getItem('rp-search-history'));}catch(e){}
// 设置里把上限调小（或调成 0）后，本地存的历史也要跟着裁掉
try{var raw=localStorage.getItem(KEY);if(raw){var old=JSON.parse(raw);
if(Object.prototype.toString.call(old)==='[object Array]'&&old.length>MAX){
if(MAX<=0)localStorage.removeItem(KEY);else localStorage.setItem(KEY,JSON.stringify(old.slice(0,MAX)));}}}catch(e){}
function load(){try{var v=JSON.parse(localStorage.getItem(KEY)||'[]');
return Object.prototype.toString.call(v)==='[object Array]'?v.slice(0,MAX):[];}catch(e){return [];}}
function save(l){try{
if(MAX<=0){localStorage.removeItem(KEY);return;}
localStorage.setItem(KEY,JSON.stringify(l.slice(0,MAX)));}catch(e){}}
function remember(q){q=(q||'').trim();if(!q||MAX<=0)return;
var l=load().filter(function(x){return x!==q;});l.unshift(q);save(l);}
function bind(input){
if(!input)return;
input.setAttribute('autocomplete','off');
input.setAttribute('autocorrect','off');
input.setAttribute('spellcheck','false');
var panel=document.createElement('div');
panel.className='rp-history';
document.body.appendChild(panel);
var list=[];
function place(){var r=input.getBoundingClientRect();
panel.style.left=Math.round(r.left)+'px';
panel.style.top=Math.round(r.bottom+4)+'px';
panel.style.width=Math.round(r.width)+'px';}
function hide(){panel.classList.remove('open');}
function render(){
list=load();
panel.innerHTML='';
if(!list.length){hide();return;}
list.forEach(function(q){
var item=document.createElement('div');item.className='rp-hist-item';
var t=document.createElement('span');t.className='rp-hist-text';t.textContent=q;t.title=q;
var d=document.createElement('button');d.type='button';d.className='rp-hist-del';
d.textContent='×';d.title=U('docs.delete');
d.addEventListener('mousedown',function(ev){ev.preventDefault();ev.stopPropagation();});
d.addEventListener('click',function(ev){ev.preventDefault();ev.stopPropagation();
list=list.filter(function(x){return x!==q;});save(list);render();});
item.appendChild(t);item.appendChild(d);
item.addEventListener('mousedown',function(ev){ev.preventDefault();});
item.addEventListener('click',function(ev){ev.preventDefault();ev.stopPropagation();
input.value=q;hide();input.focus();
input.dispatchEvent(new Event('input',{bubbles:true}));
var form=document.getElementById('rtd-search-form');
if(form&&!document.getElementById('rp-search'))form.submit();});
panel.appendChild(item);});
place();panel.classList.add('open');}
input.addEventListener('focus',render);
input.addEventListener('click',render);
input.addEventListener('blur',function(){setTimeout(hide,160);});
input.addEventListener('keydown',function(ev){if(ev.key==='Enter')remember(input.value);});
document.addEventListener('mousedown',function(ev){if(!panel.contains(ev.target)&&ev.target!==input)hide();});
window.addEventListener('scroll',function(){if(panel.classList.contains('open'))place();},true);
window.addEventListener('resize',function(){if(panel.classList.contains('open'))place();});
}
var form=document.getElementById('rtd-search-form');
if(form){form.setAttribute('autocomplete','off');
form.addEventListener('submit',function(){var i=form.querySelector('input[name="q"]');
if(i)remember(i.value);});}
bind(document.querySelector('#rtd-search-form input[name="q"]'));
bind(document.getElementById('rp-mobile-q'));
})();
// copy the matching online link for every heading that has a permalink
(function(){
var toast=document.createElement('div');
toast.className='rp-toast';
document.body.appendChild(toast);
var timer=0;
function flash(msg){
toast.textContent=msg;toast.classList.add('open');
clearTimeout(timer);timer=setTimeout(function(){toast.classList.remove('open');},1500);}
function fallback(text){
var t=document.createElement('textarea');
t.value=text;t.setAttribute('readonly','');
t.style.position='fixed';t.style.top='-1000px';t.style.opacity='0';
document.body.appendChild(t);t.select();
try{document.execCommand('copy');}catch(e){}
document.body.removeChild(t);}
function copy(text,done){
var finished=false;
function finish(){if(finished)return;finished=true;done();}
if(navigator.clipboard&&window.isSecureContext){
try{
var p=navigator.clipboard.writeText(text);
if(p&&p.then){p.then(finish,function(){fallback(text);finish();});}
else{fallback(text);finish();}
}catch(e){fallback(text);finish();}
// some environments leave the promise pending forever
setTimeout(function(){if(!finished){fallback(text);finish();}},500);
}else{fallback(text);finish();}}
var online='https://doc.renpy.cn'+location.pathname;
var GLOBE='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"'
+' stroke-linecap="round" stroke-linejoin="round">'
+'<circle cx="12" cy="12" r="9"/>'
+'<ellipse cx="12" cy="12" rx="4.2" ry="9"/>'
+'<path d="M3.2 12h17.6"/>'
+'<path d="M4.7 7.3c2.3 1.45 12.3 1.45 14.6 0"/>'
+'<path d="M4.7 16.7c2.3-1.45 12.3-1.45 14.6 0"/>'
+'</svg>';
var CHECK='<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"'
+' stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.6l4.6 4.6L19 7.4"/></svg>';
var links=document.querySelectorAll('.headerlink');
// ---- favourites: localStorage["rp-favorites"], one star per entry ----------
var STAR='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M12.00 3.20 L14.65 8.96 L20.94 9.70 L16.28 13.99 L17.53 20.20 L12.00 17.10 L6.47 20.20 L7.72 13.99 L3.06 9.70 L9.35 8.96 Z"></path></svg>';
var DOC_ID=window.RP_DOC_ID||'zh-CN';
var FKEY='rp-favorites:'+DOC_ID;
// 老版本没有按文档分家：第一次用新键时，把旧的中文收藏搬过来
try{if(DOC_ID==='zh-CN'&&!localStorage.getItem(FKEY)&&localStorage.getItem('rp-favorites'))
localStorage.setItem(FKEY,localStorage.getItem('rp-favorites'));}catch(e){}
function favList(){
try{var v=JSON.parse(localStorage.getItem(FKEY)||'[]');
return Object.prototype.toString.call(v)==='[object Array]'?v:[];}catch(e){return [];}}
function favSave(list){try{localStorage.setItem(FKEY,JSON.stringify(list));}catch(e){}}
function favKey(u,a){return u+'#'+a;}
function plainText(el){
var c=el.cloneNode(true);
var kill=c.querySelectorAll('.headerlink,.rp-star,.rp-copy-link,.rp-copy-text');
for(var i=0;i<kill.length;i++)kill[i].parentNode.removeChild(kill[i]);
return (c.textContent||'').replace(/\s+/g,' ').trim();}
// the description that belongs to this entry: the paragraph under a heading,
// or the definition body of a <dt>
function excerptOf(node){
var txt='';
if(node.nodeName==='DT'){
var dd=node.nextElementSibling;
while(dd&&dd.nodeName!=='DD'&&dd.nodeName!=='DT')dd=dd.nextElementSibling;
if(dd)txt=dd.textContent;
}else{
var el=node.nextElementSibling;
while(el){
if(/^H[1-6]$/.test(el.nodeName))break;
if(el.nodeName==='P'||el.nodeName==='PRE'||el.nodeName==='UL'||el.nodeName==='OL'||el.nodeName==='TABLE'){txt=el.textContent;break;}
el=el.nextElementSibling;}}
txt=(txt||'').replace(/\s+/g,' ').trim();
return txt.length>180?txt.slice(0,180)+'…':txt;}
function favEntry(a,order){
var leaf=plainText(a.parentNode),crumbs=[],el=a.parentNode.parentNode;
while(el&&el!==document.body){
if(el.nodeName==='SECTION'&&el.id){
var h=el.querySelector('h1,h2,h3,h4,h5,h6');
if(h){var t=plainText(h);if(t)crumbs.unshift(t);}}
el=el.parentNode;}
var doc='';
var h1=document.querySelector('.rst-content h1')||document.querySelector('article h1');
if(h1)doc=plainText(h1);
if(!doc)doc=(document.title||'').replace(/\s*[—–-]\s*Ren[’'′]?Py\s*中文文档\s*$/i,'').trim();
// the innermost section heading is the entry itself, already in `leaf`
while(crumbs.length&&crumbs[crumbs.length-1]===leaf)crumbs.pop();
var anchor=(a.getAttribute('href')||'').slice(1);
try{anchor=decodeURIComponent(anchor);}catch(e){}
// the page title is the 大类; everything below it is a 小类
var kind=(a.parentNode.nodeName==='H1')?'doc':'entry';
return {u:location.pathname.split('/').pop(),a:anchor,d:doc,p:crumbs.join(' › '),
t:leaf,x:excerptOf(a.parentNode),k:kind,n:typeof order==='number'?order:-1,ts:Date.now()};}
function favIndex(list,key){
for(var i=0;i<list.length;i++)if(favKey(list[i].u,list[i].a)===key)return i;
return -1;}
function paintStar(btn,on){
btn.className='rp-star'+(on?' rp-on':'');
btn.setAttribute('aria-pressed',on?'true':'false');
var doc=btn.getAttribute('data-kind')==='doc';
btn.title=doc?(on?U('fav.wholeOff'):U('fav.whole'))
:(on?U('fav.entryOff'):U('fav.entry'));
btn.setAttribute('aria-label',btn.title);}
// a 小类 makes its 大类 collect too, so the page title's star follows the page
function paintAll(){
var u=location.pathname.split('/').pop(),list=favList(),page=false,i;
for(i=0;i<list.length;i++)if(list[i].u===u){page=true;break;}
var stars=document.querySelectorAll('.rp-star');
for(i=0;i<stars.length;i++){
var s=stars[i],doc=s.getAttribute('data-kind')==='doc';
paintStar(s,doc?page:favIndex(list,s.getAttribute('data-key'))>=0);}}
// copy the signature of a definition box: AlphaDissolve(control, delay=0.0, ...)
// without the leading kind word ("class", "def", "screen" ...) and without the
// description underneath
var COPY='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">'+
'<rect x="9" y="9" width="11" height="11" rx="2.2"/>'+
'<path d="M6 15H5.2A2.2 2.2 0 0 1 3 12.8V5.2A2.2 2.2 0 0 1 5.2 3h7.6A2.2 2.2 0 0 1 15 5.2V6"/></svg>';
var KIND=/^(class|def|screen|define|transform|style|property|function|method|classmethod|staticmethod|attribute|variable|data|exception|module|decorator|option|envvar|init)$/;
function signatureOf(dt){
var c=dt.cloneNode(true);
var kill=c.querySelectorAll?c.querySelectorAll('.headerlink,.rp-star,.rp-copy-link,.rp-copy-text'):[];
for(var i=0;i<kill.length;i++)kill[i].parentNode.removeChild(kill[i]);
var first=c.firstElementChild;
if(first){
var w=(first.textContent||'').replace(/\s+/g,' ').trim().toLowerCase();
if(KIND.test(w))first.parentNode.removeChild(first);}
return (c.textContent||'').replace(/\s+/g,' ').trim();}
function addCopyText(a,globe){
var node=a.parentNode;
// only definition boxes carry a signature worth copying
if(!node||node.nodeName!=='DT')return;
if(!node.className||(''+node.className).indexOf('sig')<0)return;
var s=document.createElement('button');
s.type='button';s.className='rp-copy-text';
s.title=U('copy.signature');s.setAttribute('aria-label',s.title);
s.innerHTML=COPY;
s.addEventListener('click',function(ev){
ev.preventDefault();ev.stopPropagation();
var text=signatureOf(node);
if(!text)return;
copy(text,function(){
s.innerHTML=CHECK;s._busy=true;
flash(U('copy.signatureDone'));
setTimeout(function(){s.innerHTML=COPY;s._busy=false;},1200);});});
if(globe&&globe.parentNode)globe.parentNode.insertBefore(s,globe.nextSibling);
else a.parentNode.insertBefore(s,a.nextSibling);}function addFav(a,order){
var e=favEntry(a,order),key=favKey(e.u,e.a);
var s=document.createElement('button');
s.type='button';s.innerHTML=STAR;s.className='rp-star';
s.setAttribute('data-kind',e.k);
s.setAttribute('data-key',key);
// entries saved before the grouped view existed may lack kind / description
var saved=favList(),at0=favIndex(saved,key);
if(at0>=0&&(!saved[at0].x||!saved[at0].k||typeof saved[at0].n!=='number')){
if(!saved[at0].x)saved[at0].x=e.x;
if(!saved[at0].k)saved[at0].k=e.k;
if(typeof saved[at0].n!=='number')saved[at0].n=e.n;
favSave(saved);}
s.addEventListener('click',function(ev){
ev.preventDefault();ev.stopPropagation();
var list=favList(),i;
if(e.k==='doc'){
// 大类: nothing yet means "collect the whole document"; otherwise the whole
// document (including its 小类) is taken out again
var had=false,out=[];
for(i=0;i<list.length;i++){
if(list[i].u!==e.u){out.push(list[i]);continue;}
had=true;}
if(had){favSave(out);flash(U('fav.unsavedWhole'));}
else{e.ts=Date.now();out.push(e);favSave(out);flash(U('fav.savedWhole'));}
}else{
var at=favIndex(list,key);
if(at>=0){list.splice(at,1);favSave(list);flash(U('fav.unsaved'));}
else{e.ts=Date.now();list.push(e);favSave(list);flash(U('fav.saved'));}}
paintAll();
});
// the star takes the place right after the heading text, pushing the
// permalink and the globe one slot back:  ★ ¶ 🌐
a.parentNode.insertBefore(s,a);}
var copyBtns=[];
function syncColor(b){
// the globe takes the permalink's colour; the star beside it uses the same
// one through a CSS variable, so all the icons read as one row. Clicking an
// icon never leaves a colour behind (no visited tint).
var raw='';
try{raw=getComputedStyle(b._link).color;}catch(e){}
if(raw)b.style.color=raw;
if(raw){try{b._link.parentNode.style.setProperty('--rp-icon',raw);}catch(e){}}
}
for(var i=0;i<links.length;i++){
(function(a){
var href=a.getAttribute('href')||'';
if(href.charAt(0)!=='#')return;
// the permalink itself now also copies the online address
a.addEventListener('click',function(){
copy(online+href,function(){flash(U('copy.onlineDone'));});});
// globe button: copy the address and open the online page
var btn=document.createElement('button');
btn.type='button';btn.className='rp-copy-link';btn.title=U('copy.online');
btn.setAttribute('aria-label',U('copy.online'));
btn.innerHTML=GLOBE;
btn._link=a;
copyBtns.push(btn);
syncColor(btn);
btn.addEventListener('click',function(ev){
ev.preventDefault();ev.stopPropagation();
var parent=a.parentNode;
copy(online+href,function(){
btn.innerHTML=CHECK;btn._busy=true;
flash(U('copy.opening'));
window.open(online+href,'_blank');
// after leaving the app the mouse is often still over the heading, which
// would keep the icon visible; hide it until the pointer leaves again
btn.style.opacity='0';
var restore=function(){
parent.removeEventListener('mouseleave',restore);
btn.style.opacity='';};
parent.addEventListener('mouseleave',restore);
setTimeout(function(){btn.innerHTML=GLOBE;btn._busy=false;},1200);});});
a.parentNode.insertBefore(btn,a.nextSibling);
addCopyText(a,btn);
addFav(a,i);
})(links[i]);}
paintAll();
// timers can be throttled while another window is in front; on return make
// sure no button is left showing the "copied" tick
function resetBusy(){
for(var k=0;k<copyBtns.length;k++){
var b=copyBtns[k];
if(b._busy){b.innerHTML=GLOBE;b._busy=false;}}}
window.addEventListener('focus',resetBusy);
document.addEventListener('visibilitychange',function(){if(!document.hidden)resetBusy();});
// keep the copy icon the same colour as the permalink, also after a theme switch
try{
new MutationObserver(function(){
for(var k=0;k<copyBtns.length;k++)syncColor(copyBtns[k]);
}).observe(document.documentElement,{attributes:true,attributeFilter:['data-theme']});
}catch(e){};
})();
// back / home / forward: fixed across the top of the content column
(function(){
var bar=document.createElement('div');
bar.id='rp-navbar';
function add(icon,title,fn){
var b=document.createElement('button');
b.type='button';b.title=title;b.setAttribute('aria-label',title);
b.innerHTML='<i class="fa '+icon+'" aria-hidden="true"></i>';
b.addEventListener('click',function(e){e.preventDefault();e.stopPropagation();fn();});
bar.appendChild(b);}
add('fa-arrow-left',U('nav.back'),function(){
if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('rp-back');
else history.back();});
add('fa-home',U('nav.home'),function(){location.href='index.html';});
add('fa-arrow-right',U('nav.forward'),function(){
if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('rp-fwd');
else history.forward();});
document.body.appendChild(bar);
// the desktop shell reports what history allows; grey out what it does not
window.__rpNav=function(canBack,canFwd){
var bs=bar.getElementsByTagName('button');
if(bs.length<3)return;
bs[0].disabled=!canBack;
bs[2].disabled=!canFwd;
};
// sits at the window centre; only nudged right when it would cover the title
function place(){
bar.style.left='';
bar.style.transform='';
try{
var crumb=document.querySelector('.wy-breadcrumbs .breadcrumb-item.active')
||document.querySelector('.wy-breadcrumbs li:first-child');
if(!crumb)return;
var b=bar.getBoundingClientRect();
var c=crumb.getBoundingClientRect();
if(b.left<c.right+12){
bar.style.left=Math.round(c.right+12)+'px';
bar.style.transform='none';
}
}catch(e){}
}
place();
window.addEventListener('resize',place);
window.addEventListener('load',place);
})();
// images open in an overlay instead of replacing the page with the raw file
(function(){
var box=null;
function close(){
if(!box)return;
box.parentNode.removeChild(box);
box=null;
document.removeEventListener('keydown',onKey,true);}
function onKey(e){if(e.key==='Escape'){e.preventDefault();close();}}
function open(src,alt){
close();
box=document.createElement('div');
box.className='rp-lightbox';
var img=document.createElement('img');
img.src=src;img.alt=alt||'';
var btn=document.createElement('button');
btn.type='button';btn.className='rp-lightbox-close';
btn.title=U('lightbox.close');btn.setAttribute('aria-label',U('lightbox.close'));
btn.innerHTML='&times;';
box.appendChild(img);box.appendChild(btn);
box.addEventListener('click',function(e){
if(e.target===box||e.target===btn)close();});
document.body.appendChild(box);
document.addEventListener('keydown',onKey,true);}
document.addEventListener('click',function(e){
var el=e.target;
while(el&&el!==document.body&&el.nodeName!=='A')el=el.parentNode;
if(!el||el.nodeName!=='A')return;
var href=el.getAttribute('href')||'';
if(!/\.(png|jpe?g|gif|webp|svg|bmp)(\?|#|$)/i.test(href))return;
e.preventDefault();
var im=el.querySelector('img');
open(href,im?im.getAttribute('alt'):'');
},true);
// keyboard back for the app shell (it has no browser toolbar)
document.addEventListener('keydown',function(e){
var t=e.target;
var typing=t&&(/^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)||t.isContentEditable);
if(e.altKey&&e.key==='ArrowLeft'){
e.preventDefault();
if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('rp-back');
}else if(e.key==='Backspace'&&!typing){
e.preventDefault();
if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('rp-back');
}
});
})();
// the English theme ships no light/dark switch: put one in the same slot the
// Chinese theme uses, and keep its icon in step with data-theme
(function(){
function ensure(){
if(document.querySelector('.theme-switcher')||document.getElementById('rp-theme-fab'))return;
var a=document.createElement('a');
a.id='rp-theme-fab';a.href='#';a.setAttribute('aria-label',U('fab.theme'));a.title=U('fab.theme');
var MOON='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M20 14.5A8.5 8.5 0 0 1 9.5 4a8.5 8.5 0 1 0 10.5 10.5z"/></svg>',SUN='<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4"/></svg>';
function icon(){
var dark=document.documentElement.getAttribute('data-theme')==='dark';
a.innerHTML=dark?SUN:MOON;}
icon();
a.addEventListener('click',function(e){
e.preventDefault();e.stopPropagation();
var d=document.documentElement;
var dark=d.getAttribute('data-theme')==='dark';
if(dark)d.removeAttribute('data-theme');else d.setAttribute('data-theme','dark');
try{localStorage.setItem('theme',dark?'light':'dark');}catch(err){}
icon();});
document.body.appendChild(a);
try{new MutationObserver(icon).observe(document.documentElement,{attributes:true,attributeFilter:['data-theme']});}catch(e){}
}
// the English theme builds its own switcher a moment later: if it shows up,
// ours is the duplicate and goes away
window.addEventListener('load',function(){
setTimeout(function(){
var own=document.getElementById('rp-theme-fab');
if(own&&document.querySelector('.theme-switcher')&&own.parentNode)own.parentNode.removeChild(own);},120);
});
setTimeout(ensure,300);
})();
// report the colour scheme to the desktop shell so its window chrome matches
(function(){
function now(){return document.documentElement.getAttribute('data-theme')==='dark'?'dark':'light';}
function send(){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('rp-theme:'+now());}
send();
try{new MutationObserver(send).observe(document.documentElement,{attributes:true,attributeFilter:['data-theme']});}catch(e){}
})();
})();</script>"""


def doc_catalog_js():
    """The document list every page hands to the switcher panel."""
    items = []
    for d in CATALOG:
        folder = os.path.join(SITE, d["id"])
        ver = ""
        vp = os.path.join(folder, "version.txt")
        if os.path.exists(vp):
            ver = io.open(vp, encoding="utf-8").read().strip()
        items.append({
            "id": d["id"],
            "name": d["name"],
            "cur": d["id"] == LANG,
            "home": "../%s/index.html" % d["id"],
            "dl": os.path.exists(os.path.join(folder, "index.html")),
            "version": re.match(r"\d+\.\d+\.\d+", ver).group(0) if re.match(r"\d+\.\d+\.\d+", ver) else ver,
        })
    return json.dumps(items, ensure_ascii=False, separators=(",", ":"))


def fab_html():
    """The UI block for the document being built."""
    return (FAB.replace("__RP_DOCS_JS__", doc_catalog_js())
               .replace("__RP_DOC_ID__", LANG))


def strip_ui_blocks(s):
    """Remove a previously injected button block: the <style> element that
    mentions rp-search-fab, plus the markup and script that follow it."""
    while True:
        removed = False
        for m in re.finditer(r"<style>(?:(?!</style>)[\s\S])*?</style>", s):
            if "rp-search-fab" not in m.group(0):
                continue
            end = s.find("</script>", m.end())
            end = end + len("</script>") if end != -1 else m.end()
            s = s[:m.start()] + s[end:]
            removed = True
            break
        if not removed:
            return s


DISCLAIMER = """<div class="rp-disclaimer" id="rp-disclaimer">
<p>本软件由 DeepSeek 编写加人工精修，代码完全开源，遵循 MIT 协议。</p>
<p>本工具是 doc.renpy.cn 中文文档的离线镜像工具，与 Ren'Py 及相关官方文档无隶属关系。</p>
<p>文档内容可能落后于线上版本或与线上不一致，请以官方页面为准，由本软件重新生成的索引与页面不保证完整无误。</p>
<p>更新功能只从公开页面下载文档到本地，不收集和上传任何个人数据。</p>
</div>
"""


DISCLAIMER_EN = """<div class="rp-disclaimer" id="rp-disclaimer">
<p>Offline mirror of the English Ren'Py documentation, for personal reading and search.</p>
<p>The documentation text, images and page theme belong to the Ren'Py project;
the online pages at www.renpy.org/doc/html are authoritative.</p>
<p>Provided as is, without warranty of any kind.</p>
</div>
"""


PRE_OPEN = re.compile(r"<pre(?![^>]*\sid=)[^>]*>", re.I)

def add_pre_anchors(s):
    """Give every code block a stable id so search hits can link straight to it."""
    seen = [0]

    def repl(m):
        seen[0] += 1
        seg = s[m.end():m.end() + 3000]
        digest = hashlib.md5((seg + "|" + str(seen[0])).encode("utf-8")).hexdigest()[:10]
        return m.group(0)[:-1] + ' id="rp-cb-' + digest + '">'

    return PRE_OPEN.sub(repl, s)


def rewrite_html(path):
    s = io.open(path, encoding="utf-8").read()
    before = s
    # drop cache-busting queries so file:// opening works
    s = re.sub(r'((?:href|src)="[^"]*?_static/[^"]*?)\?v=[0-9a-fA-F]+"', r'\1"', s)
    # absolute doc links -> relative
    s = s.replace("https://doc.renpy.cn/zh-CN/", "")
    s = s.replace("http://doc.renpy.cn/zh-CN/", "")
    s = s.replace("https://www.renpy.org/doc/html/", "")
    s = s.replace("http://www.renpy.org/doc/html/", "")
    # canonical points at the forum, harmless but noisy offline
    s = re.sub(r'\s*<link rel="canonical"[^>]*>', "", s)
    # which document this page belongs to, known before any of our scripts run
    s = re.sub(r'<script>window\.RP_DOC_ID="[^"]*";</script>\n?', "", s)
    s = re.sub(re.escape(UI_MARK_START) + r"[\s\S]*?" + re.escape(UI_MARK_END), "", s)
    if "</head>" in s:
        s = s.replace("</head>",
                      ui_script() + "\n" +
                      '<script>window.RP_DOC_ID="' + LANG + '";</script>\n</head>', 1)
    # floating buttons: drop any previous injection first (older builds left
    # several copies behind), then add the current one
    s = strip_ui_blocks(s)
    if "</body>" in s:
        s = s.replace("</body>", fab_html() + "</body>", 1)

    # clean up helpers injected by earlier builds (sidebar experiment)
    s = re.sub(r"<!--rp-nav-init-->[\s\S]*?<!--/rp-nav-init-->", "", s)
    s = re.sub(r"<!--rp-side-restore-->[\s\S]*?<!--/rp-side-restore-->", "", s)

    # small grey disclaimer at the bottom of the content column
    s = re.sub(r'<div class="rp-disclaimer"[\s\S]*?</div>\s*', "", s, count=1)
    if "<footer" in s:
        note = DISCLAIMER_EN if LANG == "en" else DISCLAIMER
        s = re.sub(r'<footer\b', lambda m: note + m.group(0), s, count=1)
    # the crawled pages declare no icon; reuse the site logo, else browsers
    # request /favicon.ico and log a 404
    if 'rel="icon"' not in s and "</head>" in s:
        s = s.replace("</head>",
                      '<link rel="icon" type="image/png" href="_static/navbar-logo.png">\n</head>', 1)
    s = add_pre_anchors(s)
    if s != before:
        io.open(path, "w", encoding="utf-8", newline="").write(s)
    return s


# ------------------------------------------------------------ text utils
TAG = re.compile(r"<[^>]+>")
WS = re.compile(r"\s+")
PRE = re.compile(r'<pre[^>]*\bid="([^"]+)"[^>]*>(.*?)</pre>', re.S | re.I)
DT = re.compile(r'<dt[^>]*\bid="([^"]+)"[^>]*>(.*?)</dt>', re.S | re.I)
DL = re.compile(r'<dl class="([^"]+)"[^>]*>', re.I)

# Sphinx domain object kinds -> short Chinese labels
KINDS = [
    ("function", "函数"), ("method", "方法"), ("classmethod", "方法"),
    ("staticmethod", "方法"), ("class", "类"), ("variable", "变量"),
    ("var", "变量"), ("data", "数据"), ("attribute", "属性"),
    ("property", "属性"), ("module", "模块"), ("exception", "异常"),
    ("decorator", "装饰器"), ("option", "选项"), ("envvar", "环境变量"),
    ("cmdoption", "命令行选项"), ("describe", "描述"),
]


def plain(fragment):
    fragment = re.sub(r"<(script|style)\b.*?</\1>", " ", fragment, flags=re.S | re.I)
    fragment = re.sub(r'<a class="headerlink".*?</a>', " ", fragment, flags=re.S | re.I)
    fragment = TAG.sub(" ", fragment)
    fragment = html.unescape(fragment)
    fragment = fragment.replace("\u00b6", " ")
    return WS.sub(" ", fragment).strip()


def tight(fragment):
    """Strip markup without inserting spaces: keeps `renpy.save(...)` intact."""
    fragment = re.sub(r'<a class="headerlink".*?</a>', " ", fragment, flags=re.S | re.I)
    fragment = TAG.sub("", fragment)
    fragment = html.unescape(fragment).replace("\u00b6", " ")
    return WS.sub(" ", fragment).strip()


def code_lines(inner):
    txt = TAG.sub("", inner)
    txt = html.unescape(txt)
    lines = [re.sub(r"[ \t]+", " ", ln).rstrip() for ln in txt.split("\n")]
    while lines and not lines[0].strip():
        lines.pop(0)
    while lines and not lines[-1].strip():
        lines.pop()
    return "\n".join(lines)[:2500]


def kind_label(raw, pos):
    found = DL.findall(raw[:pos])
    if not found:
        return ""
    tokens = found[-1].split()
    for token, label in KINDS:
        if token in tokens:
            return label
    return ""


ID_PREFIXES = ("var-", "envvar-", "cmdoption-", "std-", "option-", "py-",
               "attrib-", "data-", "mod-", "class-", "meth-", "func-",
               "screen-property-", "transform-property-")


def display_name(ident, text):
    """Turn `define config.name = ""` + id `var-config.name` into `config.name`."""
    if ident.startswith(("cmdoption-", "option-")):
        return text[:60]
    name = ident
    for p in ID_PREFIXES:
        if name.startswith(p):
            name = name[len(p):]
            break
    if name and name in text:
        return name
    base = re.sub(r"\s*=.*$", "", text).strip()
    base = re.sub(r"\(.*$", "", base).strip()
    # Sphinx suffixes duplicate object ids (`action_alt_alt`); the readable
    # name is the signature text, not the id
    if base and base in name:
        return base
    return text[:60]


HEADING = re.compile(r"<h([1-4])\b[^>]*>(.*?)</h\1>", re.S | re.I)
SECTION_ID = re.compile(r'<(?:section|span|div)\s+id="([^"]+)"', re.I)


def parse_page(path):
    s = io.open(path, encoding="utf-8").read()
    title = re.search(r"<title>(.*?)</title>", s, re.S)
    title = html.unescape(WS.sub(" ", TAG.sub("", title.group(1)))).strip() if title else os.path.basename(path)
    title = re.sub(r"\s*[—-]\s*Ren.Py 中文文档\s*$", "", title)

    m = re.search(r'<div itemprop="articleBody">', s)
    start = m.end() if m else 0
    end = s.find("<footer", start)
    body = s[start:end if end > 0 else len(s)]
    # the injected disclaimer is chrome, not documentation
    body = re.sub(r'<div class="rp-disclaimer"[\s\S]*?</div>', " ", body)

    marks = [(h.start(), int(h.group(1)), plain(h.group(2))) for h in HEADING.finditer(body)]
    sections = []
    stack = []
    anchors = [(x.start(), x.group(1)) for x in SECTION_ID.finditer(body)]
    ai = 0
    cur_anchor = ""

    def anchor_at(pos):
        nonlocal ai, cur_anchor
        while ai < len(anchors) and anchors[ai][0] <= pos:
            cur_anchor = anchors[ai][1]
            ai += 1
        return cur_anchor

    bounds = [0] + [p for p, _, _ in marks] + [len(body)]
    for i, (pos, level, text) in enumerate(marks):
        raw = body[pos:bounds[i + 2]]
        stack = [x for x in stack if x[0] < level] + [(level, text)]
        anchor = anchor_at(pos)

        defs = []
        for d in DT.finditer(raw):
            sig = tight(d.group(2))
            if not sig or len(sig) > 160:
                continue
            defs.append([d.group(1), kind_label(raw, d.start()), display_name(d.group(1), sig)])

        codes = []
        for c in PRE.finditer(raw):
            txt = code_lines(c.group(2))
            if len(txt) < 2:
                continue
            codes.append([c.group(1), txt])

        prose = PRE.sub(" ", raw)
        prose = DT.sub(" ", prose)
        prose = plain(prose)
        prose = re.sub(r"^\s*" + re.escape(text), "", prose, count=1).strip()

        if not prose and not codes and not defs:
            continue
        # the permalink of a heading does not always point at the anchor this
        # parser picked (Sphinx likes <section id="x"><span id="y"><h2>), so
        # keep the href as an alias: the favourites page needs it to sort a
        # document's entries in page order
        mhead = re.match(r"<h([1-4])\b[^>]*>(.*?)</h\1>", body[pos:], re.S | re.I)
        head_html = mhead.group(2) if mhead else body[pos:pos + 400]
        mlink = re.search(r'class="headerlink"\s+href="#([^"]+)"', head_html)
        sections.append({"a": anchor, "h": " › ".join(t for _, t in stack),
                         "x": prose, "c": codes, "d": defs,
                         "l": mlink.group(1) if mlink else ""})

    # text before the first heading (rare)
    first = bounds[1] if marks else len(body)
    pre = plain(PRE.sub(" ", body[:first]))
    if len(pre) > 40:
        sections.insert(0, {"a": "", "h": title, "x": pre, "c": [], "d": []})
    return title, sections


BREADCRUMB = (
    '<ul class="wy-breadcrumbs">\n'
    '      <li><a href="index.html" class="icon icon-home" aria-label="Home"></a></li>\n'
    '      <li class="breadcrumb-item active">搜索</li>\n'
    '  </ul>'
)


def build_helper_page(name, title, crumb, template, out_dir=None, shell=None, doc_id=None):
    """Build a helper page (search / favourites) inside the docs' own theme
    shell, so it shows up in the content pane next to the sidebar.

    `out_dir` / `shell` let the picker page be written outside a document's
    folder (it has to survive that folder being deleted); `doc_id` overrides the
    document id the generated page believes it belongs to."""
    body = io.open(template, encoding="utf-8").read()
    out_dir = out_dir or DOCS
    shell = shell or os.path.join(DOCS, "index.html")
    src = io.open(shell, encoding="utf-8").read()
    src = re.sub(r"<title>.*?</title>",
                 lambda m: "<title>" + title + " — Ren'Py 中文文档</title>",
                 src, count=1, flags=re.S)

    crumb_html = (
        '<ul class="wy-breadcrumbs">\n'
        '      <li><a href="index.html" class="icon icon-home" aria-label="Home"></a></li>\n'
        '      <li class="breadcrumb-item active">' + crumb + '</li>\n'
        '  </ul>')
    src = re.sub(r'<ul class="wy-breadcrumbs">.*?</ul>',
                 lambda m: crumb_html, src, count=1, flags=re.S)

    # the disclaimer may sit between the article body and the footer now
    src = re.sub(
        r'(<div itemprop="articleBody">)(.*?)(\s*</div>\s*</div>\s*'
        r'(?:<div class="rp-disclaimer"|<footer))',
        lambda m: m.group(1) + "\n" + body + "\n" + m.group(3),
        src, count=1, flags=re.S)
    # a helper page is not a toctree entry: drop "current page" highlighting
    src = src.replace(' current"', '"')
    # index.html's sidebar links home with "#"; here that must go to index.html
    src = src.replace('<a href="#" style="margin-bottom: 5px">',
                      '<a href="index.html" style="margin-bottom: 5px">')
    # a page that belongs to no document: the switch panel's "current" id has to
    # be empty, or clicking another document would be taken as "already here"
    if doc_id is not None:
        src = re.sub(r"var DOC_ID='[^']*';",
                     "var DOC_ID='" + doc_id + "';", src, count=1)
    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    io.open(os.path.join(out_dir, name), "w", encoding="utf-8", newline="\n").write(src)


def build_search_page():
    build_helper_page("search.html", "搜索", "搜索", TEMPLATE)


def build_favorites_page():
    tpl = os.path.join(os.path.dirname(TEMPLATE), "favorites_body.html")
    if not os.path.exists(tpl):
        tpl = os.path.join("updater", "favorites_body.html")
    build_helper_page("favorites.html", "收藏", "收藏", tpl)


def copy_theme_assets(doc_dir):
    """site/_static: the picker page needs the theme's css / js / fonts, and it
    lives outside the document folders, so it gets its own copy of them."""
    src = os.path.join(doc_dir, "_static")
    dst = os.path.join(SITE, "_static")
    if not os.path.isdir(src):
        return
    if os.path.isdir(dst):
        shutil.rmtree(dst, ignore_errors=True)
    shutil.copytree(src, dst)


def build_picker_page():
    """The blank "pick a document" page shown when nothing can be read.

    It is written to the site root, not into a document folder: the document
    you are reading can now be deleted, and the page that replaces it must not
    disappear with it."""
    tpl = os.path.join(os.path.dirname(TEMPLATE), "picker_body.html")
    if not os.path.exists(tpl):
        tpl = os.path.join("updater", "picker_body.html")
    # prefer the Chinese shell: this is the app's own page, not a document's
    shell_lang = "zh-CN" if os.path.exists(os.path.join(SITE, "zh-CN", "index.html")) else LANG
    shell_dir = os.path.join(SITE, shell_lang)
    if not os.path.exists(os.path.join(shell_dir, "index.html")):
        return
    build_helper_page("picker.html", "请选择文档", "请选择文档", tpl,
                      out_dir=SITE, shell=os.path.join(shell_dir, "index.html"),
                      doc_id="")
    copy_theme_assets(shell_dir)
    # older builds put a copy inside every document folder
    stale = os.path.join(DOCS, "picker.html")
    if os.path.exists(stale):
        os.remove(stale)


def nav_order():
    """Page order as listed in the left sidebar of index.html."""
    path = os.path.join(DOCS, "index.html")
    if not os.path.exists(path):
        return []
    s = io.open(path, encoding="utf-8").read()
    m = re.search(r'wy-menu wy-menu-vertical(.*?)</nav>', s, re.S)
    seg = m.group(1) if m else s
    order = []
    for href in re.findall(r'href="([^"#?]+?\.html)"', seg):
        name = href.rsplit("/", 1)[-1]
        if name not in order:
            order.append(name)
    return order


def main():
    if not os.path.exists(os.path.join(DOCS, "index.html")):
        print("缺少 index.html：抓取不完整", file=sys.stderr)
        return 2
    all_pages = sorted(glob.glob(os.path.join(DOCS, "*.html")))
    # rewrite every page (including Sphinx' generated index pages)
    for p in all_pages:
        if os.path.basename(p) not in ("search.html", "favorites.html", "picker.html"):
            rewrite_html(p)

    pages = [p for p in all_pages if os.path.basename(p) not in ("search.html", "favorites.html",
                                                                "picker.html", "genindex.html")
             and not os.path.basename(p).endswith("-index.html")]
    docs = []
    chars = 0
    for p in pages:
        name = os.path.basename(p)
        title, sections = parse_page(p)
        if not sections:
            continue
        docs.append({"u": name, "t": title, "s": sections})
        chars += sum(len(x["x"]) + sum(len(c[1]) for c in x["c"]) for x in sections)

    # genindex (term index) is a generated page; bring it in and tidy it
    if os.path.exists("tools/genindex.html"):
        shutil.copyfile("tools/genindex.html", os.path.join(DOCS, "genindex.html"))
        rewrite_html(os.path.join(DOCS, "genindex.html"))

    # drop Sphinx's own search index (unused, 2.3 MB)
    for junk in ("searchindex.js",):
        jp = os.path.join(DOCS, junk)
        if os.path.exists(jp):
            os.remove(jp)

    # results are shown in sidebar order, best matching first
    nav = {}
    for pos, name in enumerate(nav_order()):
        nav.setdefault(name, pos)
    for i, d in enumerate(docs):
        d["o"] = nav.get(d["u"], 1000 + i)

    payload = json.dumps(
        {"generated": "2026-10-27", "version": VERSION, "docs": docs},
        ensure_ascii=False,
        separators=(",", ":"),
    )
    out = os.path.join(DOCS, "search-index.js")
    io.open(out, "w", encoding="utf-8", newline="\n").write(
        "window.RENPY_SEARCH_INDEX = " + payload + ";\n"
    )

    # root redirect for people who open site/index.html
    home = "zh-CN/index.html"
    if not os.path.exists(os.path.join(SITE, "zh-CN", "index.html")):
        home = "picker.html"
    io.open(os.path.join(SITE, "index.html"), "w", encoding="utf-8", newline="\n").write(
        '<!DOCTYPE html><meta charset="utf-8">'
        '<meta http-equiv="refresh" content="0; url=' + home + '">'
        '<title>Ren\'Py 中文文档</title>'
        '<p>正在打开 <a href="' + home + '">Ren\'Py 中文文档</a>…</p>'
    )

    build_search_page()
    build_favorites_page()
    build_picker_page()

    # human-readable docs version, kept inside the document's own folder.
    # The Chinese builds carry it in the sidebar ("8.5.4 说明文档首页" / "8.5.4
    # 說明文件首頁"); every other language only has the Sphinx options file, and
    # on doc.renpy.cn that file is years stale, so the sidebar wins when there is
    # one and the options file is the second source.
    index_path = os.path.join(DOCS, "index.html")
    index_html = ""
    if os.path.exists(index_path):
        index_html = io.open(index_path, encoding="utf-8", errors="replace").read()
    ver = ""
    m = re.search(r"(\d+\.\d+\.\d+)[^<]{0,12}(?:说明文档首页|說明文件首頁)", index_html)
    if m:
        ver = m.group(1)
    if not ver:
        opts = os.path.join(DOCS, "_static", "documentation_options.js")
        if os.path.exists(opts):
            m = re.search(r"VERSION\s*:\s*'([^']+)'",
                          io.open(opts, encoding="utf-8", errors="replace").read())
            if m:
                ver = m.group(1)
    if not ver:
        own = os.path.join(DOCS, "version.txt")
        if os.path.exists(own):
            ver = io.open(own, encoding="utf-8").read().strip()
    # "8.5.4.26101301+unofficial" reads better as "8.5.4" in the switch panel
    m2 = re.match(r"(\d+\.\d+\.\d+)", ver)
    if m2:
        ver = m2.group(1)
    if ver:
        io.open(os.path.join(DOCS, "version.txt"), "w", encoding="utf-8",
                newline="\n").write(ver + "\n")
        if LANG == "zh-CN":
            io.open(os.path.join(SITE, "version.txt"), "w", encoding="utf-8",
                    newline="\n").write(ver + "\n")

    # the desktop shell reads this to know which documents exist and where
    meta = []
    for d in CATALOG:
        folder = os.path.join(SITE, d["id"])
        v = ""
        vp = os.path.join(folder, "version.txt")
        if os.path.exists(vp):
            v = io.open(vp, encoding="utf-8").read().strip()
        meta.append({
            "id": d["id"],
            "name": d["name"],
            "site": d.get("site", ""),
            "home": d["id"] + "/index.html",
            "version": v,
            "downloaded": os.path.exists(os.path.join(folder, "index.html")),
        })
    io.open(os.path.join(SITE, "docs.json"), "w", encoding="utf-8", newline="\n").write(
        json.dumps(meta, ensure_ascii=False, indent=2) + "\n")

    print("docs:", len(docs), "sections:", sum(len(d["s"]) for d in docs),
          "chars:", chars, "index MB: %.2f" % (os.path.getsize(out) / 1048576),
          "version:", ver or "?")


if __name__ == "__main__":
    # optional: build.py <site dir> [search_body template] [lang]
    if len(sys.argv) > 1:
        configure(sys.argv[1],
                  sys.argv[2] if len(sys.argv) > 2 else None,
                  sys.argv[3] if len(sys.argv) > 3 else "zh-CN")
    main()
