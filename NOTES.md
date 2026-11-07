# Ren'Py 中文文档离线版 · 项目笔记

> 记录已完成的功能、实现方式、踩过的坑和当前状态。
> 项目根目录下只有 `app\`，网页版已删除（用户手动删除），之后只维护桌面端。

---

## 1. 成果概览

- **形态**：单文件桌面程序，双击即用，无命令行、无地址栏、不依赖服务器
- **数据**：doc.renpy.cn 中文参考文档 Ren'Py **8.5.4** 的完整本地镜像
- **规模**：112 个 HTML（104 篇正文 + 4 个索引页 + 术语总索引 + 首页 + 搜索页）、94 个静态资源，约 20 MB；另有构建时生成的 `favorites.html`（每份文档一份）和**站点根目录**的 `picker.html` + `_static`（主题资源副本，3.42 MB，见 4.10）
- **索引**：104 篇正文 / 1239 个章节 / 约 75 万字、1117 个代码块、1975 个 API 定义，`search-index.js` 约 1.62 MB
- **程序**：`RenPyDocs.exe` 约 415 KB，整个 `app` 目录自包含约 45.5 MB

---

## 2. 目录结构（当前）

```
renpy文档/
├─ NOTES.md                  ← 本文件（放在 app 之外）
└─ app/
   ├─ RenPyDocs.exe          主程序（C# WinForms + WebView2）
   ├─ Ren'Py 中文文档.lnk     快捷方式（桌面同名一份由构建脚本生成）
   ├─ app.ico                图标（由脚本生成多尺寸 ico）
   ├─ Microsoft.Web.WebView2.*.dll / WebView2Loader.dll
   ├─ README.md              面向使用者的说明
   ├─ site/                  文档镜像（唯一数据源）
   │  ├─ index.html          跳转到 zh-CN/index.html
   │  ├─ version.txt         中文文档版本号
   │  ├─ docs.json           六份文档的目录（id / 名字 / 站点 / 版本 / 是否已下载）
   │  └─ zh-CN/              页面 + _static + search-index.js + version.txt + build.txt
   │     （zh-TW / en / ja / en-nightly / en-nightly-fix 下载后同样各占一个目录）
   ├─ src/
   │  ├─ Program.cs          MainForm / UpdateDialog / InfoDialog / ProgressForm / BusyForm / TaskbarProgress
   │  └─ Updater.cs          SiteUpdater：抓取 + 重建 + 替换
   ├─ updater/               应用内更新载荷
   │  ├─ build.py            页面改写 + 索引 + 搜索页生成（与 tools 构建同源）
   │  ├─ search_body.html    搜索页模板
   │  └─ python/             内嵌 CPython 3.12（约 21 MB）
   └─ tools/
      ├─ build.ps1           一键重建
      └─ ui_block.html       注入所有页面的界面代码块（唯一样式/脚本来源）
```

---

## 3. 程序（Program.cs）已实现

| 功能 | 实现要点 |
| --- | --- |
| 站点加载 | `SetVirtualHostNameToFolderMapping("renpy.localhost", site, Allow)` → `https://renpy.localhost/zh-CN/index.html` |
| 窗口记忆 | 位置/大小/最大化存 `%LOCALAPPDATA%\RenPyDocs\window.txt`，多显示器按屏幕工作区校正；首次主屏居中 |
| 外链 | `NavigationStarting`/`NewWindowRequested` 拦截，http(s)/mailto 交给系统浏览器，其余忽略 |
| 启动检查更新 | `CheckForUpdates(false)`，尊重“本次更新不再提示”（`update-ignore.txt`） |
| 手动检查更新 | 页面上的刷新悬浮按钮 postMessage `rp-check-update` → `CheckForUpdates(true)`，忽略忽略列表；检查时显示 `BusyForm`「正在检查更新…」，**进度条按步骤推进**（连接文档站 8% → 读取线上版本 20% → 获取构建时间 52% → 读取版本文件 78% → 比较版本 92/95%），同一时间只跑一次检查（`checkBusy`，连点不会叠窗） |
| 已是最新 | 自绘 `InfoDialog`（与更新弹窗同版式、无系统提示音），标题“当前已是最新版本” |
| 更新执行 | 主窗口隐藏 → `ProgressForm`（任务栏真实进度条）→ 隐藏 WebView2 抓取 → 内嵌 Python 重建 → 备份 `site.bak` 后替换 → 完成提示“立即启动/稍后” |
| 取消更新 | 进度窗口可取消，丢弃暂存目录，本地不受影响 |
| 旧备份清理 | 下次正常启动时删除 `site.bak` |
| 后退快捷键 | 页面把 `Alt+←` / `Backspace` 转为 postMessage `rp-back` → `core.GoBack()` |
| 导航按钮状态 | `HistoryChanged` / `NavigationCompleted` → `ExecuteScriptAsync("window.__rpNav(canBack,canFwd)")`，页面据此禁用按钮 |
| 启动清历史 | 启动时 `Profile.ClearBrowsingDataAsync(BrowsingHistory)`，保证 `:visited` 紫色不会跨次留痕 |
| 日志 | `%LOCALAPPDATA%\RenPyDocs\update.log` |
| 标签栏 | `src\TabStrip.cs`：自绘标签条，`Dock = Top` 固定在窗口最上方，WebView2 `Dock = Fill` 占其余区域——**页面完全不参与**，页面自己的布局（`#rp-navbar` 的 `top:26px` 等）保持原样 |
| 标签切换 | 只有一个 WebView2，切换 = 重新加载该地址；切走前 `ExecuteScriptAsync` 读 `location.href` + `pageYOffset` 存进标签，加载完成后按需 `scrollTo` |
| 搜索词条开新标签 | 搜索页 postMessage `rp-open-tab\n词条名\n绝对地址` → `StartTab()` 新建标签并带标题；普通链接仍在当前标签内跳转（宿主从 `NavigationCompleted` 的 `core.Source` 判断） |
| 标签主题 | 页面 postMessage `rp-theme:dark|light`（`MutationObserver` 监听 `data-theme`）→ 标签栏配色；同时把最后一次主题写进 `theme.txt`，下次启动先按它画，避免白条闪一下 |
| 标签快捷键 | `IMessageFilter` 在 WebView2 拿到键盘之前拦 `Ctrl+T/W/Tab/1..9`（`ProcessCmdKey` 收不到） |
| 标签持久化 | `%LOCALAPPDATA%\RenPyDocs\tabs.txt`（首行是当前标签序号，之后每行 `地址\t标题\t滚动位置\t位置所属地址`，最多 16 条）；关窗前先异步读一次滚动位置再写盘 |

编译约束：**只能用 `csc.exe`（C# 5）**——不能写字符串插值、表达式体成员、`nameof` 等。

---

## 4. 页面侧功能（界面文案在 `UI_TEXT` 字典，注入的按钮/脚本在 `FAB`）

注入方式：`updater/build.py` 先删掉旧代码块（含 `rp-search-fab` 的 `<style>` 到其后的 `</script>`），再在 `</body>` 前插入当前块；四语言界面字典 `UI_TEXT` 由 `ui_script()` 注入 `<head>`（`<!--rp-ui-text-->` 标记，重复构建先删旧的）。改界面文案改 `UI_TEXT`，改按钮/脚本改 `FAB`，然后跑构建。

> `tools/ui_block.html` 只是 `FAB` 的一份副本（内容逐字相同，方便单独查看）。
> 改完 `build.py` 后要顺手同步它，否则两份会不一致：
> `python -c "import sys,io; sys.path.insert(0,'updater'); import build; io.open('tools/ui_block.html','w',encoding='utf-8',newline='').write(build.FAB)"`

### 4.1 悬浮按钮（右下角竖排六个，同尺寸同阴影）

自下而上：**明暗主题**（主题自带，`bottom:1.4em`）→ **搜索**（`4.15em`）→ **收藏**（五角星，`6.9em`）→ **检查更新**（`9.65em`）→ **切换文档**（书本图标，`12.4em`）→ **设置**（齿轮，`15.15em`）。

- 搜索：文档页跳搜索页；在搜索页上只聚焦输入框
- 收藏：跳收藏页；在收藏页上只聚焦收藏页自己的筛选框
- 更新：浏览器中隐藏，仅桌面版显示
- 切换文档：点开一个固定定位的小面板（`#rp-docs-panel`，`right:calc(1.6em + 2.2em + .55em); top:calc(100% - 14.6em)`）——**在按钮列的左侧**，**顶边与按钮顶边齐平**（14.6em = 12.4em 底部位置 + 2.2em 按钮高），往下长，与按钮留 11px 间距；用 `top:calc(100% - …)` 而不是 `bottom`，面板多高都不用算，里面列出可选的文档
  - 标题行：左边「切换文档」（14px），右边一个**选择 / 删除两档滑块**（`#rp-docs-mode`，**67×27** 的圆角胶囊 + 白色滑块 31×23，两档各一个 **14×14** 的 SVG 图标：**✓** 表示选择、**垃圾桶**表示删除，鼠标悬停有 `title` 提示；选中一档为彩色：选择蓝 `#2980b9`、删除红 `#e05555`，未选中一档灰 `#9aa4ae`；滑块 `translateX(100%)` 滑到右档，`.2s ease-in-out`）
  - 面板整体：宽 260～390（`min-width:13em; max-width:19.5em`），每行 **36.8px 高、名字 16px**，左侧图标槽 16.8px；面板最高 `14.2em`（284px，底部离窗口下沿 8px），**头行固定不滚**，只有列表滚动（`display:flex` + 列表 `flex:1 1 auto;min-height:0;overflow-y:auto`，细滚动条 6px）。名字列 201～214px（随面板自适应），最长的是「Ren'Py Nightly Development」（16px 下约 201px）；名字再长就会被 `.rp-doc-name` 的 `text-overflow:ellipsis` 截掉，得先放宽面板
  - 选择状态：文档左侧 1em 的槽里是蓝色 ✓（当前打开的那一份）；删除状态：同一个槽交叉淡入淡出成**红色垃圾桶**（`.2s ease-in-out`，与 ✓ 同一盒子、同一居中方式；放大后实测 ✓ 11.5×13.5px、垃圾桶 13×13px，盒子都是 16.8×16.8）
  - 删除状态下**下载箭头要藏起来**（它就压在垃圾桶上，不藏点不动删除），藏法和打勾↔垃圾桶完全一样：换 `opacity`（`.2s ease-in-out`，逐帧实测和 √ 的曲线一模一样，0.99→0.77→0.36→0.06→0）、淡完再 `visibility:hidden`（`0s linear .2s`），Tab 也停不上去；但**下载中的圆环照旧显示**、照旧能点暂停/继续；圆环在的时候垃圾桶自己先退场，避免两个图标叠影
  - 删除状态下面板里的行只能靠**左边的垃圾桶**删（点名字不会删）；点垃圾桶先弹**确认框**（见 4.12），确认后宿主删掉 `site/<id>` 的本地文件并重写 `docs.json`，**这一行不会消失**，而是原地变成「未下载」：名字变灰、左边变成下载箭头，收藏 / 标签页 / 搜索记录都保留（`rp-docs-gone` 这套已不再使用）。**正在读的那份也能删**：确认文案是「本文档「<名字>」正在使用，是否删除？浏览数据将会保留」，删完宿主保存标签 → 删目录 → 切到根目录的空白选择页（4.10）
  - 再点滑块（或按空格/回车，滑块可聚焦）切回选择状态；点按钮 / 点面板外 / Esc 关面板，但滑块位置保留到页面刷新
  - **显示 / 收起带过渡**：面板不再用 `display:none` 切换，改成 `opacity` + `visibility`（`visibility 0s linear .17s` 延迟到淡出结束才变，收起后 `pointer-events:none`、Tab 也进不去），`.17s ease`，同时从下方 4px 移到位；打开时**列表逐行淡入**（`rpFadeRows()`：每行 22ms 错峰延迟，摘掉 `.in` → 强制回流 → 挂回去重播，`.2s`，超过 8 行统一 176ms）
  - 行动画**只改 `opacity`，绝对不能带 `translateY`**：列表是 `overflow-y:auto` 的滚动容器，行往下平移 3px 就会让 `scrollHeight` 比 `clientHeight` 多 3px，滚动条会在开场那 ~150ms 里闪一下（实测 101 → 104，`offsetWidth-clientWidth` 从 0 跳到 6），动画结束又消失。上下位移统一交给外层面板（`overflow:hidden`，剪掉就完事）
  - 版本号：打开面板时 `fetch('../docs.json')` 拉一次目录（`applyCatalog()` 就地更新行状态），缺版本号的再 `fetch(.../version.txt)` 补上；点**当前这一条**只是关掉面板（在「请选择文档」页上则是直接回它的首页）
  - 结构是 `var DOCS=[{id,name,cur,home}]` 数组（同时挂到 `window.RP_DOCS` 给选择页用），以后加别的文档只要往这个数组里填、在 `click` 里接上切换逻辑
  - 面板字号按 `font-size:20px` 继承（和悬浮按钮同一套 em 单位），所以在任何缩放上都和按钮对齐
- 设置：齿轮，在切换文档上方（`bottom:15.15em`，原来 `+` 的位置）。只在桌面版显示（没有 `chrome.webview` 时整个按钮 `display:none`），点一下给宿主发 `rp-open-settings`，由 C# 弹出 `SettingsDialog`（白色底、18pt 粗体标题、右下 124×44 的「关闭」）。设置项还没填，见 4.11

### 4.2 顶部导航按钮（内容区顶部，与面包屑同一行）

- 三个按钮：`←` 后退、`🏠` 首页、`→` 前进，各 82×26，间距 6px
- 图标是 FontAwesome 字形（`fa fa-arrow-left` / `fa-home` / `fa-arrow-right`）；`#rp-navbar button` 用 `display:flex;align-items:center;justify-content:center` 让字形落在按钮正中（试过换成内联 SVG，块级元素不居中会贴左边，而且线条比原来的实心字形单薄，已改回）
- `position: fixed`，不随滚动移动
- 水平位置：窗口居中；若会压住页面标题则右移到标题右侧 12px
- 垂直位置：`top: 34px`，紧贴标签栏下沿，与标题、“文档捉虫”中心线对齐
- 不可用时由宿主置灰（`disabled`，透明度 0.38，悬停无反应）

### 4.3 搜索页

- 边输入边出结果（80ms 防抖），结果在右侧内容区
- **结果按文档（大类）分组**：每个大类是一行 1.34em 的蓝色文档名（链接到该页），下面用左侧细线缩进列出它命中的小类词条；大类之间用一条浅色分隔线
  - 大类顺序：**名称命中优先**（文档标题或章节名里含关键词的文档排前面，只有正文/代码命中的排后面），同一档内按**左侧目录的文档顺序**（`index.html` 侧栏 toctree 顺序，构建时写入索引的 `o` 字段）
  - 组内顺序：先名称命中的词条，再只有正文/代码命中的词条；同档内按页面中章节的先后（索引 `s` 的下标），同一章节内仍是 定义 → 代码 → 标题 → 正文
  - 文档只有一个命中时不出标题行，蓝色文档名直接写在那一行最前面（`大类 › 章节 › 词条`），避免出现“标题 + 一行内容”
- **大类可折叠**：每个大类标题左侧有一个灰色倒三角（`▾`），点击收起/展开该文档的命中（收起后转为 `▸`）
  - 三角是内联 SVG（`viewBox 0 0 16 9`，扁三角 + `stroke-linejoin:round` 做出圆角），渲染约 13.5×7px；不再依赖字体里的 `▾` 字形（会随字体变形、偏上）
  - 收起时把 `<svg>` 旋转 -90° 变成圆角 `▶`，尺寸不变
  - 标题行 `.rp-head` 是 flex 行，三角 `top:calc(50% - .09em)` + `translateY(-50%)` 对齐标题中心线（换字号也不会歪）
  - 常态透明不可见；鼠标移到大类所在区域（含左侧留白，用 `::before` 撑出热区）或三角本身时才浮现；**收起后不保留常显**，鼠标移开即淡出
  - 折叠有过渡：`.rp-fold` 用 `grid-template-rows: 1fr → 0fr`（0.24s ease），不用写死 `max-height`，内容多高都能自然收放；三角同步旋转 0.24s
  - `aria-expanded` 同步
  - 折叠状态加在 `li.rp-group` 上，按钮在 `.rp-head` 里，所以用 `closest('.rp-group')` 找祖先
  - 点击要取 `closest('.rp-toggle')`（点在 SVG 上时 `ev.target` 是 `<svg>/<path>`），并给 `svg` 加 `pointer-events:none` 让命中落在按钮上
- **小类词条**：`章节 › 子节 › 词条`，前面的层级用 0.86em 灰色小字，最后一节（命中项）用 1.06em 深色加粗，中间用 `›` 分隔
  - 路径数据来自索引里的 `s.h`（构建时已按标题层级拼好），首段与文档名相同时去掉，避免和分组标题重复
  - 命中词条是 API 时路径末尾加词条名（等宽字体）+ 类型标签（函数/类/变量/方法/定义）
  - 命中代码块时路径停在所属章节，后面跟「代码」标签和代码片段
  - 点击条目跳到该词条自己的锚点（`#dt id` / `#rp-cb-…` / `#section-id`）
- **相同内容合并**：同一文档 + 同一锚点的命中只保留得分最高的一条（正文条目与标题条目不再重复出现）
- 结果分层：**API 定义 → 代码块 → 章节标题 → 正文**；上限 8 条定义、每文档每层 3 条、总计 60 条（分组后仍按 60 条截断，可能截到某一组的中间）
- 代码块高亮命中行并带上下文，锚点 `rp-cb-<md5>`；定义复用 `<dt id>`
- 关键词高亮、地址栏同步 `?q=`
- **更换关键词时滚回页面顶部**
- 已删除所有快捷键提示（`↑ ↓ 选择 · Enter 打开 · Esc 清空`）
- 窄屏（≤900px）内容区顶部另有搜索框

### 4.4 搜索历史

- 点击/聚焦搜索框，下方浮出最近 5 条记录，宽度与输入框一致
- 文字左对齐、超长省略号，右侧 `×` 删除
- 点击条目直接搜索（文档页跳搜索页，搜索页即时刷新）
- 存储：`localStorage['rp-search-history']`，仅在真正提交时记录
- 输入框与表单加 `autocomplete="off"`，屏蔽浏览器“保存的信息”气泡

### 4.5 词条链接

- 每个带锚点的标题旁有地球图标（悬停标题时出现，颜色取自锚点）
- 点击：复制 `https://doc.renpy.cn/zh-CN/<页面>#<锚点>` 并打开浏览器；图标短暂变对勾
- **点过之后不留颜色**：早先“已访问变紫”的那套（`sessionStorage['rp-visited-links']` + `.rp-visited`）和文章内的 `a:visited` 紫色都已去掉，正文链接访问过后仍是普通蓝色（`.rst-content a:visited{color:#2980b9}`，深色用 `--dark-link-color`）
- 原锚点图标（¶）点击时也会复制线上链接

### 4.6 图片灯箱

- 点击文档图片不再跳转到图片文件，改为页内遮罩放大
- 关闭方式：右上 `×`、点遮罩、`Esc`

### 4.7 其他

- 免责声明（12.5px 灰字，正文底部、页脚之上）：四行——“本软件由 DeepSeek 编写加人工精修，代码完全开源，遵循 MIT 协议。”+ 离线镜像无隶属关系、内容以官方页面为准、更新只下载公开页面不收集个人数据
- `html{scrollbar-gutter:stable}` 修复无滚动条页面的按钮位移
- 宽屏灰边修复：`wy-nav-content-wrap` 透明 + `max-width:none`
- `/` 聚焦搜索框

### 4.8 标签页（窗口最顶部的标签栏，原生控件）

**结构上和页面完全分离**：`src\TabStrip.cs` 是自绘的 `Control`，`Dock = Top` 贴在窗口顶部（标题栏下面），WebView2 `Dock = Fill` 占满其余空间。页面那边一行布局代码都不需要——`body` 没有 padding、侧栏没有位移、`#rp-navbar` 仍在 `top:26px`，以后改页面 UI 也不会和标签栏互相顶。

外观（跟着页面主题走）：条高 = 字体高 + 19（约 35px）；标签圆角 `7px 7px 0 0`、最大宽 220 / 最小 78，标签多了按比例压缩（不低于 64），再放不下就让当前标签可见地横向滑动；当前标签用页面同色（浅 `#fcfcfc` / 深 `#141414`）、标题加粗，`×` 只出现在当前标签或悬停的标签上，`+` 紧跟在最后一个标签后面。

行为：

- **点搜索结果 → 新标签**：`search_body.html` 的结果链接带 `data-tab="词条名"`，点击时 postMessage，宿主新建标签并用这个词条名做标题，然后跳到锚点
- **普通链接 → 当前标签**：页面内跳转只更新当前标签的地址和标题（标题取 `document.title`，去掉 ` — Ren'Py 中文文档` 后缀，首页统一叫“首页”）
- 搜索页标签名跟着关键词：`搜索：save`
- 关闭：`×`、鼠标中键、`Ctrl+W`；关掉当前标签后激活右邻，关掉最后一个 → 回首页标签
- 新建：`+` 或 `Ctrl+T`；切换：`Ctrl+Tab` / `Ctrl+Shift+Tab` / `Ctrl+1`…`Ctrl+9`
- 重启后标签还在（`tabs.txt`，最多 16 个）
- **前进 / 后退跟标签联动**（宿主自己维护每个标签的历史，不再用浏览器的）：
  - 后退先退本标签内的历史（点链接翻过页，就退回上一页，标签不动）
  - 退到本标签的“出生页”再后退 → **关掉这个标签并回到它的来源标签**（搜索结果点开的那条就是这样被收回的，不会越点越多）
  - 前进先走本标签的前进栈；没有了就**把刚被后退关掉的标签重新开出来**（`reopen` 列表）
  - 有没有可后退 / 可前进由宿主的模型算，通过 `__rpNav(canBack,canFwd)` 告诉页面，按钮据此置灰
  - 页面上的 `←` `→` 和 `Alt+←` / `Backspace` 都改成 postMessage `rp-back` / `rp-fwd`（浏览器里没有宿主时才退回 `history.back()`）

实现要点（一个 WebView2，所以切换 = 重新加载地址）：

1. **滚动位置**：切走前用 `ExecuteScriptAsync("location.href+\\u0001+pageYOffset")` 把地址和位置一起读回来存进标签；下次加载完这段地址时再 `scrollTo` 回去。位置记的是「哪个地址 + 多少像素」，地址对不上就不动，所以普通翻页不会误跳到旧位置。
2. **锚点优先**：从搜索结果新开的标签先把该标签的位置清零，落点完全由地址里的锚点决定。
3. **标题归属**：搜索结果带来的词条名存在 `pendingTitle`，只有那一次导航用它；之后页面内跳转才换成页面标题。
4. 关闭窗口时会先异步读一次当前滚动位置再写 `tabs.txt`（`OnFormClosing` 先 `e.Cancel` 再关，否则总是存到旧值）。
5. **历史归属**：`hostNav` 标记“这次加载是宿主发起的”（切标签 / 后退 / 前进 / 新建），`NavigationCompleted` 里只有非 `hostNav` 的加载才算“页面自己翻页”，推进 `TabInfo.History`；同一页面只改 `#锚点` 的（`NoHash` 比较）只更新末尾那条，不新增。
6. `TabInfo.Opener` 是对象引用（不写进 `tabs.txt`），只有从搜索结果新开的标签才有；它决定“退到底之后关标签、回到谁”。

### 4.9 收藏

**词条上的图标**（`FAB` 里的 `addFav()` / `addCopyText()`）：每个带 `a.headerlink` 的标题 / 定义都插一排 `★ ¶ 🌐` —— 收藏星在标题文字右边，然后是原有的永久链接 ¶、地球（打开线上页）；**只有定义框（`dt.sig`）再多一个复制按钮 📋**，标题行没有。星和复制按钮常态透明，鼠标移到标题上才出现；已收藏时星常显且变黄（浅色 `#efaa00` / 深色 `#f4b41f`，实心 + 圆角描边）。

- **复制按钮** `.rp-copy-text`：内联 SVG（两张叠起来的圆角方片，24 viewBox、1.7 描边），点一下复制**这个定义的签名**——只要函数/类那一行，不要下面的说明文字，也不带 `class` / `def` / `screen` / `define` 这类前缀（`signatureOf()` 克隆 `dt`，先删掉 `.headerlink`、`.rp-star`、`.rp-copy-link`、`.rp-copy-text`，再删掉第一个文字是类型关键字（`KIND` 正则）的子元素）。实测 `AlphaDissolve(control, delay=0.0, alpha=False, reverse=False)`、`ComposeTransition(trans, before, after)`、`config.name = ""`。复制成功图标短暂（1.2s）变成对勾并弹「已复制签名」
  - 没被删干净的 FontAwesome 永久链接字形（``）会跟着进剪贴板，所以删除那几个图标节点这一步不能省
  - 只认 `dt.sig`（`class` 里含 `sig`），`dl.field-list` 里那种字段名 `dt` 不会长出按钮

- 星形是内联 SVG（`viewBox 0 0 24 24`，外径 9.4 / 内径 4.5 的十点路径），靠 `stroke-linejoin:round` + 1.9 的描边把五个尖角磨圆，不会太扎眼
- **四个图标是同一套尺寸**：`.rp-star` / `.rp-copy-link` / `.rp-copy-text` / `a.headerlink` 统一 `width/height:18px; padding:0; margin-left:4px; vertical-align:middle; font-size:15px`，图标本体 15×15，所以盒子等宽等高、间距都是 4px（实测墨迹 13×13 / 14×13 / 13×13，中心都在盒子中央）。主题给 `dl dt .headerlink` 的 `margin-left:.5em` 需要靠 `a.headerlink.headerlink` 的双类选择器压过去（同特异性但注入在主题之后）
- **四个图标同一个颜色**：`.rp-star, .rp-copy-text` 用 `color:var(--rp-icon,inherit)`，变量由 `syncColor()` 从标题永久链接（`a.headerlink`）的 `getComputedStyle(...).color` 取到后设到该标题元素的 `style` 上；地球则是直接把这颜色写在自己的 inline style 上。这样四个图标同色：定义行是主题给 `dt .headerlink` 的 `#404040`（深浅色都不变），标题行是链接蓝
  - 变量必须设在**标题元素**上（`b._link.parentNode`），不能设在地球按钮上——`syncColor()` 在地球插入文档之前就被调用，那时 `btn.parentNode` 还是 `null`（这个坑第一次改的时候踩了，页面颜色完全没变）
- **点击后不再“粘住”**：显示规则用的是 `:focus-visible` 而不是 `:focus`。用 `:focus` 时，鼠标点过的按钮会一直拿到焦点，`opacity:1` 被焦点规则顶住，鼠标移开也不消失（而 `a.headerlink` 点完会失去焦点，所以只有它会自己淡掉）。改成 `:focus-visible` 后鼠标点击不触发它，只有键盘 Tab 过来才显示（键盘焦点另有 2px 描边）；已收藏的星仍然由 `.rp-on` 常显，这是有意的
  - 已收藏的黄色由 `.rp-star.rp-on{color:#efaa00}` 覆盖（class 特异性高于 `.rp-star`），不需要为黄色清内联样式
- **四个图标同一个渐隐节奏**：统一写 `transition:opacity .2s ease-in-out,color .2s ease-in-out`。之前 ¶ 用的是主题的 `all .2s ease-in-out`，而星和地球是 `opacity .12s ease`，悬停时出场快慢不一样；现在四个的 `transitionProperty/Duration/TimingFunction` 完全相同（实测 `opacity, color` / `0.2s, 0.2s` / `ease-in-out, ease-in-out`）
- 点击切换：`localStorage['rp-favorites']`，一条 = `{u, a, d, p, t, x, k, ts}`（页面文件名 / 锚点 / 文档标题 / 章节路径 / 词条名 / 描述摘录 / `doc` 或 `entry` / 时间戳）
- 词条信息在注入时从 DOM 现算：标题文字去掉 `.headerlink`、`.rp-star`、`.rp-copy-link` 后取 `textContent`；路径是往上找带 id 的 `<section>`，各取第一个标题；最内层标题与词条名相同时去掉，避免重复
- 描述摘录 `x`：标题取它下面第一个 `p / pre / ul / ol / table` 的文字，定义取 `dt` 后面的 `dd`；压成一行、超过 180 字截断加省略号
- **大类 / 小类**：页面 `<h1>` 上的星是 `k:'doc'`（整篇文档 = 大类），其余标题和定义是 `k:'entry'`（小类）
  - 收藏任意小类时，页面标题的星也会亮（`paintAll()` 里「本页有任何收藏 → 大类算已收藏」），这就是“小类收藏后大类同步收藏”
  - 收藏大类只写一条 `k:'doc'`，不带任何小类
  - 大类星再点一次会清掉整页收藏（含小类），提示语是“取消收藏整篇文档”
- 去重键是 `页面#锚点`，所以同一个词条不会出现两条
- 老数据（没有 `k` / `x`）在打开对应文档时由 `addFav()` 就地补全；`k` 缺失时收藏页用 `t===d` 兜底判断
- 存 localStorage 而不是文件：跟 WebView2 的 profile 走，关掉程序、更新文档都还在

**收藏页**（`updater/favorites_body.html` → 由 `build_helper_page()` 生成 `favorites.html`，和搜索页同一套主题壳子、同样在内容区）：

- 顶部是**它自己的筛选框**（不是侧栏那个），带星形图标、可一键清空，只筛收藏；匹配范围含文档名、章节路径、词条名和描述
- **按大类分组**，与搜索结果同构：一行 1.34em 的蓝色文档名（后面跟小类数量徽标，右侧一个黄星），下面用 2px 灰色竖线缩进列出小类
- 每个小类一行：`章节路径 › 词条`（路径 0.86em 灰、词条 1.06em 加粗），下一行是 2 行截断的灰色描述，行尾一个黄星可单独移除
- 只有大类收藏的文档不出现小类列表，只有标题那一条，标题下面补一行**这篇文档的描述**（`x`，创建收藏时从 h1 后的第一段取）；一旦有了小类，这一行就不再显示
- 点击文档名/小类行：有宿主时 postMessage `rp-open-tab`（新标签打开），浏览器里就是普通跳转
- **排序跟收藏时间无关**：`favorites.html` 先加载 `search-index.js`，用索引里的 `d.o`（左侧目录顺序）排大类，小类按在文档里的先后排。小类的“先后”有两套：新收藏存 `n`（收藏时它在页面所有 `.headerlink` 里的序号，最准）；老收藏没有 `n` 时退回索引的顺序号
  - 坑：Sphinx 有时写成 `<section id="transform"><span id="list-of-transform-properties"><h2>…`，标题上的永久链接指向 `#transform`，但索引原来的锚点是 `#list-of-transform-properties`——两套名字对不上就会掉到末尾按时间排（正是“pos 排在 transform 特性列表前面”的原因）。现在 `parse_page` 会把标题里 `a.headerlink` 的 href 作为别名存进 `sections[].l`，收藏页把 `a` 和 `l` 都映射到同一个顺序号；改完后 3349 个永久链接里落在索引外的从 1314 降到 135 个
  - 索引里查不到的条目排在最后，同分时按时间兜底
- 空状态、无匹配、筛选计数都各有文案

### 4.10 空白选择页（没有可读的文档时）

- 页面：**站点根目录的 `site/picker.html`**（不是任何文档目录里的），由 `build_helper_page()` 用 `updater/picker_body.html` 生成；沿用主题外壳（字体、明暗主题都跟着走），但左侧目录、面包屑、页脚、顶部返回条、切换文档按钮全部 `display:none`
  - 放在根目录是必须的：**正在读的文档可以被删除**，删完就要停在这张页面上，页面不能跟着 `site/<id>` 一起消失
  - 主题的 css / js / 字体由 `copy_theme_assets()` 复制一份到 `site/_static`（3.42 MB，33 个文件），选哪个外壳取 `site/zh-CN/index.html`（没有就用当前 lang），所以根目录这份页面不依赖任何文档目录
  - 生成时把外壳里注入的 `var DOC_ID='<lang>'` 改写成 `''`：这一页不属于任何文档，点任何一份文档都交给宿主判断（否则“点自己”会被当成“已经在这里”）
- 版式：`div[itemprop="articleBody"]` 撑满一屏（`min-height:100vh`）做成 flex 居中，屏幕正中一块最宽 420px 的「请选择文档」+ 文档列表；每份文档一行卡片（书本图标 + 名字 + 右箭头 / 下载箭头 / 进度圆环），悬停描边变蓝并抬头阴影。六份时整块 420×400、中心 (633,450)，正好是 1280×900 视口的中心（含滚动条留白后的 1265 布局宽度）；名字列 311px，最长的「Ren'Py Nightly Development」约 200px
- 数据：`FAB` 里 `window.RP_DOCS=DOCS` 把文档表暴露出去，选择页在 `DOMContentLoaded` 后照它建行；打开时再 `fetch('../docs.json')` 补一次真实状态（根目录下 `../docs.json` 解析到 `/docs.json`）
- **独立页面**：进到选择页时宿主 `SetPickerMode(true)` 把原生标签栏 `tabs.Visible=false`（`NavigationCompleted` 里按 URL 判断 `IsPickerUrl`，`/picker.html` 一样匹配），标签快捷键（Ctrl+T/W/Tab/1-9）也一并停用；右下角只保留主题切换按钮，搜索/收藏/更新/切换文档/设置全部 `display:none`
- 进入条件：
  - **在切换文档面板里删掉正在读的那份文档**（4.12）：宿主 `SaveTabs()` → 删目录 → `SetPickerTabs()`（一个标签、地址是选择页）→ 导航过去
  - 启动时当前文档已经没有目录（`CurrentDocAvailable == false`）→ 直接以选择页开局，不再载入那份文档的标签
  - 任何页面加载时发现 `DOCS` 全部在 `rp-docs-gone` 里 → `location.replace('/picker.html')`（选择页自己按 `pathname` 排除，不会循环跳）
- 从选择页回到文档：点一份已下载的文档 → 页面发 `rp-switch-doc`；宿主发现是**同一份**（重新下载回来的那份）就 `LoadTabs()` 恢复删除时保存的那套标签，直接回它的首页；是别的文档就走正常的切换流程（直接切，没有确认框）
- 坑：主题给 `li`（左 24px）、`ul`（padding-left 19px）和 `li > *`（上下 12px）都塞了 margin，还有 `.rst-content h1` 的 margin-bottom 比单类选择器更“重”——卡片和标题必须用 `#rp-picker …` 前缀再 `margin:0`，否则行会被撑到 76px、标题字号也会被改掉
- 坑：写进选择页的标签栏必须**不覆盖**删除时保存的那套 —— `SaveTabs()` 里遇到「当前文档不存在 + 只有一个选择页标签」就整段跳过，否则重下载回来时标签就没了

### 4.11 设置按钮（齿轮）

- 位置：右下角按钮列最上面一个（`#rp-settings-fab`，`bottom:15.15em`），就是原来 `+` 的位置；和切换文档按钮的间距还是 11px（设置 553–597、书本 608–652）
- 外观：和下面几个按钮**同一个选择器组**——2.2em 圆、`right:1.6em`、`transition:all .3s ease-in-out`、同一套阴影与暗色底色；图标是 Lucide 齿轮线稿（24 viewBox，`stroke-width:2.6`，显示 1.08em）
- 行为：只在桌面版显示（没有 `chrome.webview` 时按钮 `display:none`）；点击 `postMessage('rp-open-settings')`
- 弹窗：C# 的 `SettingsDialog(AppSettings current)`，660×649，白色底、34,28 处 18pt 粗体标题，五组设置自上而下（组与组之间统一 24px，标题到控件 28px，复选框之间 28px；只有搜索记录数量 → 软件更新检查这段用 12px，因为 TrackBar 底部带刻度、视觉上本身多占一截）：
  1. **系统语言设置** — `ComboBox`（`DropDownList`）：简体中文 / 繁體中文 / English / 日本語
  2. **文档更新检查** — 勾选「启动时检查文档更新」；下面还有「每天最多检查一次」（`doc-update-daily`，**默认勾选**），勾上后启动检查会在 `%LOCALAPPDATA%\RenPyDocs\doc-check.txt` 里记下当天日期（`yyyy-MM-dd`，本地时间），同一天再启动就跳过（日志 `check: startup skipped (already checked today)`），手动检查不受限制
  3. **关闭清除标签** — 勾选「关闭后清除所有标签页」
  4. **搜索记录数量** — 横向 `TrackBar`，0–10，右侧数字实时跟着滑块走；控制每个文档最多保留几条搜索历史，0 表示不保留（`search-history-max`）
  5. **软件更新检查** — 网址输入框（`EM_SETCUEBANNER` 灰色提示 `https://github.com/llfseik/RenPy-Docs-Offline-Reader`，留空即用项目默认仓库）+ 勾选「启动时检查软件更新」（`app-update`）+ 勾选「每天最多检查一次」（`app-update-daily`，两项默认开启）；启动时查询 GitHub Release，有新版本时确认后下载合并
  底部两个 124×44 的按钮：**取消**（左，`DialogResult.Cancel`）/ **保存**（右，`DialogResult.OK`）。控件的值只在点「保存」时由 `Collect()` 收进 `Result`；取消 / Esc / 关闭窗口都不写文件、不改任何状态
- 存储：`%LOCALAPPDATA%\RenPyDocs\settings.txt`，八行 `key=value`：`language` / `doc-update` / `doc-update-daily` / `search-history-max` / `app-update` / `app-update-daily` / `app-update-url` / `clear-tabs`。`AppSettings.Load()` 对缺文件、缺键、坏值一律回默认（语言 `zh-CN`、文档更新开、每天一次开、搜索历史 5 条、软件更新开、软件更新每天一次开、清标签关）；旧文件没有 `app-update-daily` 时按新默认迁移为两项都开
- 行为（保存后生效）：
  - 语言：`Lang.Set(settings.Language)` 立即切换软件自身的界面语言（所有 `Lang.T(zh, tw, en, ja)` 弹窗、按钮、提示、标签标题）；`settings.txt` 里的值在下次启动时由 `Main()` 先读一次。界面字体也跟着语言走（`Lang.UiFont`）：简中 `Microsoft YaHei UI`、繁中 `Microsoft JhengHei UI`、英语 `Segoe UI`、日语 `Yu Gothic UI`——之前一律用雅黑画英/日/繁中，字体回退后字宽和基线跟按钮中心对不上，按钮文字会偏；换成本地字体后按钮文字重新居中
  - 标签标题：**文档自身的标签**用的是文档页面标题（内容，中文文档就显示中文标题，不随界面语言变）；**应用自己的页面**（搜索 / 收藏 / 请选择文档 / 首页）在 `CleanTitle()` → `KnownPageTitle()` 里映射成当前语言，`DocumentTitleChanged` 也会同步改当前标签（`pendingTitle` 存在时让搜索命中的标签名优先）
  - 下载 / 更新进度与报错：`Updater.cs` 里原来写死的中文（页面 a/b、静态资源、重建索引、访问校验超时、下载失败、重建失败等）已经接入 `Lang.T`，下载失败弹窗里显示的异常文案也会跟着语言走
  - 页面侧文案：`build.py` 里有一套四语言 `UI_TEXT` 字典，`ui_script()` 把它注入每个页面 `<head>`（`window.RP_UI` + `rpT/rpF/rpM`，带 `<!--rp-ui-text-->` 标记，重复构建会先删旧的）；宿主启动时用 `AddScriptToExecuteOnDocumentCreatedAsync` 注入 `window.__rpUiLang`，换语言时重新注入并 `Reload()` 当前页。悬浮按钮 / 切换文档面板 / 搜索页 / 收藏页 / 选择页的静态文案由 FAB 脚本在载入时替换，动态文案（结果统计、toast、下载/暂停标题）改用 `rpT/rpF/rpM`；选择页 / 搜索页 / 收藏页的 `<title>` 和面包屑也按语言重写（文档页标题是文档内容，不动）
  - 仍然保持原文的：文档正文、左侧目录、页面标题里的站名（`— Ren'Py 中文文档`）、搜索页的常见主题 chips（它们是中文文档里的检索词）、搜索结果里的章节名和 API 类型标签（索引内容）。底部免责声明四段也进了 `UI_TEXT`（`disc.1`…`disc.4`），由 FAB 脚本在载入时整块重建
  - 文档更新：`StartupChecks()` 先检查软件更新，再调用 `AutoCheckDocumentUpdates()` 按 `doc-update` 检查**当前打开的文档**——`zh-CN` 走 `CheckForUpdates(true)`，其余走 `CheckDocUpdate(id, true)`；和页面上的「检查更新」完全同一条路径：先弹 `BusyForm` 进度窗，检查完弹「当前已是最新版本」/「无需更新」（带「校验完整性」），有新版则弹更新确认框
  - 搜索历史：`PageSettingsScript()` 把 `window.__rpUiLang` 和 `window.__rpSearchHistoryMax` 一起注入；页面 `load/save/remember` 都按 `MAX` 截断，`MAX<=0` 时不再记录、并删掉存储键。保存设置时 `TrimSearchHistoryAsync()` 先遍历 `localStorage` 里所有 `rp-search-history*` 键裁到新上限，再 `Reload()` 当前页（页面载入时也会再裁一次自己的键）
  - 软件更新：`StartupChecks()` 在启动 0.8 秒后先查 `app-update`，查完（有更新时等弹窗关闭）才走文档检查，所以两者都有更新时软件更新弹窗在前；`app-update-daily` 打开时用 `%LOCALAPPDATA%\RenPyDocs\app-check.txt` 记当天日期（`yyyy-MM-dd`），同一天再启动就跳过（日志 `app-update: startup skipped (already checked today)`），只有检查成功才记日期，离线/失败下次启动会重试。`AppUpdater` 把 GitHub 仓库地址转换成 `api.github.com/repos/<owner>/<repo>/releases/latest`，也支持返回 `{"version":"1.0.1","url":"…","download_url":"…zip"}` 的自定义清单；比较 `AppUpdate.cs` 里的 `CurrentVersion`（1.0.1）。发现新版后确认「立即更新」：`AppUpdateInstaller` 下载 Release 里的 ZIP（优先带 renpy/offline/windows 的资产），解压后写一个 `apply-update.ps1`，等本进程退出后把文件合并进安装目录（**跳过 `site\docs.json`**，保留本地文档目录和版本），再重启 `RenPyDocs.exe`；合并失败写 `%TEMP%\RenPyDocs-update.log`。没有可下载资产时才退回打开下载页面；无更新/无 Release 时启动过程不弹窗
  - 清标签：`clear-tabs` 打开时，`OnFormClosing` 的第二次收尾（`SaveTabs()` 之后）调 `ClearSavedTabs()` 删掉全部 `tabs-*.txt`；`doc.txt` 保留，所以下次启动 `LoadStartupTabs()` 找不到列表，只开**当前文档的首页**
- 语言覆盖范围：`Program.cs` 的弹窗/按钮/提示，以及页面侧 `UI_TEXT` 字典覆盖的悬浮按钮、切换面板、搜索页 / 收藏页 / 选择页和底部免责声明；文档正文、目录、文档标签标题保持原文
- 旧「测试」入口连同 `#rp-test-panel`、`TESTS` 数组、`rp-open-tab` 的页面侧调用一起删掉了（`rp-open-tab` 仍留给搜索命中开标签用）

### 4.12 删除确认框（WinForms 原生，与「检查更新」同一套）

- 点删除状态里的垃圾桶 → 页面 postMessage `rp-confirm-delete\n<id>\n<名字>`；宿主 `Program.cs` 在 `BeginInvoke` 里弹 `ConfirmDialog`（新加的类，布局照 `UpdateDialog` / `InfoDialog`：`FixedDialog`、白底、`Microsoft YaHei UI`、660×330，标题 18pt 加粗在 (34,28)，正文 11.5pt 在 (36,88)，124×44 的大按钮在 y=250）
- 两个按钮：**删除**（左，`DialogResult.OK`）/ **取消**（右，`DialogResult.Cancel`），和更新提示里「确认 / 取消」的位置一致；`AcceptButton = CancelButton = 取消`，回车和 Esc 都只会取消
- 选「删除」→ 宿主 `DeleteDocFiles()` 删掉 `site/<id>` 再 `WriteDocsJson()`；删的是**别的**文档就 `ExecuteScriptAsync("window.__rpDocDeleted(<id>)")`，让面板上那一行原地变成「未下载」；删的正是**当前**文档则 `SaveTabs()` → `SetPickerTabs()` → 导航到根目录的空白选择页（4.10）。选「取消」/ 关窗口什么都不做
  - 文案：别的文档「是否要删除「<名字>」？浏览数据将会保留」；**正在用的**那份「本文档「<名字>」正在使用，是否删除？浏览数据将会保留」（删完会回到「请选择文档」页）；页面没带名字时退回「此文档」/「本文档」
  - 正在下载的那份要删，先 `CancelDownload()`（`taskkill /T /F`），否则后台 python 会把目录再写回来
  - 删除失败（文件被占用）会弹 `InfoDialog`「删除失败」/「请稍后重试」，不动任何状态
- 页面侧只在桌面宿主里走原生框（`window.chrome.webview` 存在时），浏览器里退回 `window.confirm()` 仅作调试用；旧的网页版卡片弹窗（`#rp-confirm`）已整块删除，不留余代码
- 坑：宿主回调要在 `BeginInvoke` 里弹，不能在 `WebMessageReceived` 里直接 `ShowDialog`（会卡住消息处理）；`JsString()` 负责给 id 加引号转义

### 4.13 校验完整性（`src/Integrity.cs`）

- 入口：「检查更新」查到「当前已是最新版本」或「无需更新」时，弹窗里 `确定` 的**左边**多一个「校验完整性」按钮（`InfoDialog` 的第三个参数，按钮答 `DialogResult.Retry`，宽度 124、和 `确定` 间距 16px）
- 校验在后台线程跑，`BusyForm(text, true)` 用**连续的 0~100 进度条**（`ProgressBarStyle.Continuous`）显示「正在校验 N%…」；跑完先把进度条填满、停 300ms 再换结果窗，否则“100%”和“满格”永远对不上（默认的 `BusyForm` 还是跑马灯，给没有百分比的耗时操作用）
- 检查内容（遍历 `site/<id>` 全部文件）：
  - `.html`：至少 600 字节、含 `<html`、标题里要有 `Ren'Py`、不能是反爬脚本；再把页面里引用的 `_static` / `_images` / css / js / 字体逐个对磁盘核对，**引用缺失也会报**
  - 图片（png/jpg/jpeg/gif/ico/webp/svg）：文件头必须是真图片——`_0x` 挑战页就是在这被抓出来的；png/jpg/gif/ico 再用 GDI+ `Image.FromStream` **真解码一次**（截断/损坏的图会在这一步抛异常），svg 要求含 `<svg`/`<?xml`
  - `.css` / `.js`：不能以 `<` 开头（挑战页伪装）
  - `index.html` 里必须出现「中文」（仅 zh-CN）：整份被换成英文文档时 `index.html` 是唯一能看出来的地方
- 结果：没有问题 →「文件完整」+ 检查了多少文件/多少张图片；有问题 →「发现文件损坏」，**只报总数和错误类型**（`· 图片损坏：2 处` / `· 文件缺失：1 处` / `· 页面异常：1 处` / `· 资源异常：1 处`），不列文件名、不写建议语——读者要的是“坏了多少、坏在哪类”，路径列表只是噪音（`IntegritySummary()` 负责拼这段，`SelfTest` 里断言它不含 `.png` / `.html`）。窗口里带一个「重新下载」按钮，点了直接 `DownloadDocument(id, update=true)`（重新抓一遍，坏文件会被覆盖，失败时保留现有副本）
- 每种问题都带一个 `Kind`（`图片损坏` / `文件缺失` / `页面异常` / `资源异常` / `空文件` / `内容不符` / `读取失败` / `目录异常`），`IntegrityReport.Kinds()` 统计数量，`IntegritySummary()` 按固定顺序输出
- `InfoDialog` 现在会按正文长度自动长高（长列表不会被按钮压住），并支持自定义窗口标题；`BusyForm` 多了 `SetText()` 用来显示百分比
- 自检：`_selftest.exe site\zh-CN out.txt check` —— 先造一棵坏样本（挑战页 png、截断 png、缺失引用、挑战页 css），必须正好报 4 处且不误报那张好图；然后扫真实文档，必须 0 处问题。全量自检里还验了 `InfoDialog` 会按正文长高/带附加按钮、`BusyForm(text,true)` 是 `Continuous` 且最大 100、默认构造仍是跑马灯

### 4.14 六份文档的目录（`CATALOG` / `docs.json`）

- 目录的唯一来源是 `updater/build.py` 的 `CATALOG`：`{id, name, site}`，顺序就是面板 / 选择页的顺序
  - `zh-CN` Ren'Py 中文文档 → `https://doc.renpy.cn/zh-CN/`
  - `zh-TW` Ren'Py 繁體中文文件 → `https://doc.renpy.cn/zh-TW/`
  - `en` Ren'Py Documentation → `https://www.renpy.org/doc/html/`
  - `ja` Ren'Py 日本語ドキュメント → `http://ja.renpy.org/doc/html/`
  - `en-nightly-fix` Ren'Py Nightly Fix → `https://nightly.renpy.org/current-8-fix/doc/`
  - `en-nightly` Ren'Py Nightly Development → `https://nightly.renpy.org/current-8/doc/`
- `build.py` 把目录写进 `site/docs.json`（`id / name / site / home / version / downloaded`）；`FAB` 里的 `var DOCS=[…]` 是同一份表的精简版（`cur/dl/version`），所以**每次改目录都要重跑 `build.ps1`**，把六份文档的页面里那段 JS 一起更新
- 没下载的文档 `dl:false`，面板 / 选择页显示下载箭头；点一下走 `download_doc.py`，它按 `site/docs.json` 里该文档的 `site` 决定抓哪个站（`DOCS` 字典只是没有 `docs.json` 时的兜底，里面也补了六个）
- 抓到的文档一律落在 `site/<id>/`，与 `zh-CN` 平级；`build.ps1` 只重建**磁盘上存在**的文档目录（`Test-Path site\<id>\index.html`），所以没下载的那几份不会凭空出现
- 每份文档自己的 `version.txt` / `build.txt`：`build.py` 先从侧栏 `(\d+\.\d+\.\d+)[^<]{0,12}(说明文档首页|說明文件首頁)` 取版本（中文、繁中），取不到再读该文档的 `_static/documentation_options.js` 的 `VERSION`（英文 / 日文 / 每夜版都准，只有 doc.renpy.cn 那份是 2024 年的死数据），最后才用旧的 `version.txt`
- 「检查更新」对**当前文档**做，版本与构建时间两个信号都比：非 `zh-CN` 的文档走 `CheckDocUpdate(id)`，站点地址取自 `docs.json` 的 `site`，同样 GET 首页拿 `Last-Modified`（页面里若有侧栏版本就一起拿），失败再 `FetchBuildAsync()` 对 `searchindex.js` / `index.html` 发 `HEAD`，最后才读 `_static/documentation_options.js` 的版本。这条路径现在也有和中文文档一样的进度窗（`BusyForm` 的 `Continuous` 模式，`FetchSiteInfoAsync(site, report)` 每进入一步就 `report(文案, 百分数)`）
- 站点要求浏览器校验时（doc.renpy.cn 的两个中文站都可能），`PollDownload()` 看到进度文件里「浏览器校验」就改用 `DownloadWithBrowser(id)`：`SiteUpdater` 现在接 `(appRoot, docId, siteUrl, …)`，用隐藏 WebView2 抓同一份文档，`build.py` 也按 `docId` 重建（`RunBuilder` 把文档 id 传给 `build.py` 的第三个参数）
- 繁中的侧栏是「8.5.4 說明文件首頁」，日文站标题里的 `Ren&#39;Py` 是 HTML 实体：`fetch_en.crawl()` 判断“是不是挑战页”前要先 `html.unescape(title)`，否则会把正常页面当成校验页拒掉（这次加日文时踩到的）

---

## 5. 搜索索引与构建

`updater/build.py` 的职责：

1. 删除缓存参数、绝对链接改相对、去掉 canonical
2. 注入 `ui_block.html`
3. 插入免责声明（并保证其不进入索引）
4. 给代码块加 `rp-cb-*` 锚点
5. 解析正文 → 分层索引 → 写 `search-index.js`
   - 索引里的词条名做净化：剥掉 Sphinx 生成的 id 前缀（`var-`、`py-`、`class-`、`screen-property-`、`transform-property-` 等）；重复对象的 `_alt` 后缀不会出现在结果里，改用签名文本作为可读名
   - 每个章节除了锚点 `a`，还存一份标题永久链接的 `l`（两者常常不同），收藏页用它来排序
6. 由 `index.html` + `search_body.html` 生成 `search.html`，由 `index.html` + `favorites_body.html` 生成 `favorites.html`
   （两者共用 `build_helper_page(name, title, 面包屑, 模板)`；这两个页面在改名循环里要跳过，也不能进索引）
7. 写 `version.txt`、根目录跳转 `index.html`
   - 同时从 `index.html` 的侧栏 toctree 读出文档顺序，写进索引每篇的 `o` 字段，供搜索页按左侧目录排序

重建命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File app\tools\build.ps1
```

脚本流程：先给运行中的 `RenPyDocs.exe` 窗口发 `WM_CLOSE`，让应用正常退出并把当前标签、活动标签和滚动位置写回 `tabs-*.txt`（5 秒内没退出才强杀）→ 用内嵌/系统 Python 跑 `updater\build.py` → `csc.exe` 编译 `src\*.cs` → 重建快捷方式（**应用目录内 + 桌面各一份**）→ `ie4uinit.exe -show` 刷新图标缓存。

> 之前 `build.ps1` 直接 `Stop-Process -Force`，运行中的标签来不及保存；下次启动就会读回上一次正常关闭时的旧标签。现在先走正常关闭，更新前后看到的标签就是同一套。
>
> 自动化测试一律用 `RENPYDOCS_DATA` 把数据目录指到临时文件夹：`$env:RENPYDOCS_DATA="$env:TEMP\rp-test"; Start-Process RenPyDocs.exe`。标签、设置、收藏、`doc.txt`、WebView2 的 localStorage 都会落在临时目录，不会改动 `%LOCALAPPDATA%\RenPyDocs`。
>
> 测试约定：**不在用户桌面上启动 RenPyDocs.exe 做使用测试**；页面/脚本用 headless Edge 跑，窗体布局用离屏 `DrawToBitmap` 或只读控件位置。真实的“点一遍”使用测试由用户完成。

快捷方式的 `TargetPath` / `IconLocation` 都是绝对路径，**目录一旦被移动或改名就会失效**（图标变空白、双击报错）。构建脚本每次都按当前目录重写这两份 `.lnk`，所以搬动目录后跑一次 `build.ps1` 即可自愈。

注意：`build.ps1` **必须带 UTF-8 BOM 保存**，否则 PowerShell 5.1 按 ANSI 读取，中文快捷方式名会变成乱码。

---

## 6. 踩过的坑（重要）

| 问题 | 结论 |
| --- | --- |
| 抓取全部 468 | 站点有腾讯 EdgeOne + 雷池 WAF，普通 HTTP 被拦；必须用真实浏览器会话（非 headless） |
| `.local` 虚拟主机慢 2 秒/次 | mDNS 解析超时，改用 `renpy.localhost`，单页导航 2036ms → 96ms |
| 页面无滚动条时按钮偏移 15px | `html{scrollbar-gutter:stable}` |
| 宽屏右侧灰带 | `.wy-nav-content-wrap{background:transparent}` + 去掉 max-width |
| 注入块重复 3 份 | 早期清理正则匹配不到，改为“定位含 rp-search-fab 的 `<style>` 块，删到其后的 `</script>`” |
| 免责声明被索引进正文 | 解析前先用正则剔除 `.rp-disclaimer` |
| 搜索结果只给词条名，看不出属于哪一篇 | 改为按文档分组：蓝色文档名（大类）+ 缩进的小类路径，末节加粗 |
| 宽泛关键词下一条结果占一行、看不出归属 | 同文档命中聚成一组，组内按得分排列，`找到 N 条 · M 篇文档` |
| 标题条目与正文条目指向同一锚点、重复出现 | 按 `文档#锚点` 去重，只留得分最高的一条 |
| 单个命中的文档只剩“标题 + 一行” | 单命中文档不出标题行，蓝色文档名直接内联在词条行首 |
| 排序只看得分时，名称里含关键词的条目排在后面 | 先按“名称命中 / 仅正文命中”分档，再按侧栏文档顺序 + 章节先后排 |
| 特性词条显示成 `transform-property-xpos` | `display_name` 的 id 前缀表补 `screen-property-` / `transform-property-`，并把兜底分支改为返回签名文本而不是 id |
| 页面标题与 h1 不一致（如 achievement.html） | 路径首段用 h1，和 `<title>` 不同时再补上文档标题 |
| 搜索结果标签单独占一行 | `.rp-path` 用 `display:inline`，让类型标签跟在路径末尾 |
| 暗色模式下结果之间的分隔线发白 | `.rp-single`（单行大类）和 `.rp-stats` 的 `border-top` 漏了深色覆盖，补 `#333d49`；深色下所有分隔线统一这个值 |
| 折叠三角点了没反应 | 点击取 `closest('.rp-toggle')`（点在 SVG 上时 target 是 `<svg>/<path>`），并给 svg 加 `pointer-events:none` |
| 收起后三角一直挂着 | 去掉 `.collapsed .rp-toggle{opacity:.45}`，与展开态一样只随悬停出现 |
| 搜索页正文替换失败 | 免责声明挡在 `</div></div>` 与 `<footer>` 之间，正则需兼容两种收尾 |
| 搜索页侧栏“文档捉虫首页”点不动 | 搜索页由首页模板生成，`href="#"` 需改成 `index.html` |
| 图片点击后像卡在全屏 | 图片包在 `<a>` 里直接跳图片文件；改为页内灯箱，并补 `Alt+←`/`Backspace` 后退 |
| 复制按钮在无权限环境无反馈 | 剪贴板 Promise 可能永久 pending，加 500ms 超时兜底 |
| 点地球后图标不消失 | 窗口失焦时鼠标仍悬停；点击后主动设 `opacity:0`，`mouseleave` 恢复；并用 `focus`/`visibilitychange` 复位对勾 |
| 桌面版 `:visited` 跨次残留 | 启动时清除浏览历史 |
| 侧栏抽搐（已放弃的方案） | 依次试过：恢复滚动位置、隐藏侧栏、静态边距、拦截 `scrollIntoView`、`overflow-anchor:none`、行内脚本提前恢复——**最终整体回退**，侧栏恢复主题原生行为 |
| 侧栏放导航按钮会牵动布局 | 最终把按钮移到内容区顶部，左侧目录零改动 |
| 中文空间的 logo / 截图全空白 | 重新抓取时文档站对图片回了 200 + 987 字节的 `<script>…_0x…` 挑战页，被当成图片存下来了；爬虫现在校验文件头（二进制资源首字节不能是 `<`）并退避重试，全局限速 60ms/请求 |
| 点下载后进度窗自己关掉、只显示失败 | 抓取时首页请求偶发失败（日志里 `pages 109/109`、`assets 78`、`css refs 0`），`build.py` 在 `site/<id>/index.html` 上 `FileNotFoundError`；旧代码只写「构建失败」，`EndWatch(false)` 又删掉半成品目录并关窗。现在首页重试 3 次、缺首页直接报「没有抓到首页 index.html，请重试」，失败原因进「下载失败」弹窗，构建输出进 `download-<id>.log` |
| 下载 nightly 时「页面全丢、资源全在」 | nightly 站对 HTML 文档回 403、对 `_static` 资源回 200；爬虫把 403 记成普通错误继续跑，最后 build.py 找不到 index.html。现在 403/429/503 退避重试后抛 `BlockedError`，自动改用内嵌浏览器 |
| 新加的日文 / 繁中一抓就失败，说是浏览器校验 | 日文站 `<title>` 里是 HTML 实体 `Ren&#39;Py`，爬虫的“是不是挑战页”检查拿原文去 `IndexOf("Ren'Py")`，永远找不到就整页拒掉；先 `html.unescape(title)` 再比 |
| exe 被占用无法编译 | 构建脚本先 `Stop-Process RenPyDocs` |
| 快捷方式中文乱码 | 脚本必须存为 UTF-8 with BOM |
| 快捷方式图标变空白 / 双击没反应 | 目录被移动或改名后，`.lnk` 里的绝对路径（`TargetPath`、`IconLocation`）指向旧位置；`build.ps1` 现在每次重写桌面和应用目录两份快捷方式，并跑 `ie4uinit.exe -show` 清图标缓存 |
| 把标签栏做进页面里会连环错位 | 第一版把标签栏当页面里的 `fixed` 层，只能靠 `body{padding-top:34px}` + `.wy-nav-side{top:34px;min-height:0}` 把页面顶下去——页面自己的 `top:26px` 导航、侧栏、面包屑全要保持同步，改一处就错一处。最终改为**原生控件 `Dock=Top` + WebView2 `Dock=Fill`**，页面零改动 |
| `Ctrl+T` / `Ctrl+W` / `Ctrl+Tab` 没反应 | 键盘被 WebView2 拿走，`Form.ProcessCmdKey` 收不到；用 `IMessageFilter` 在消息进窗口前拦 `WM_KEYDOWN`（模态对话框打开时让开） |
| 切标签时滚动位置丢失 | 只有一个 WebView2，页面一换就重来；切走前 `ExecuteScriptAsync` 读 `location.href` + `pageYOffset` 存进标签，加载完成后地址对得上才 `scrollTo` |
| 关窗口时存到的是旧位置 | `OnFormClosing` 是同步的，读脚本位置要 await；改成先 `e.Cancel = true`、异步读一次再 `Close()` |
| 深色页面启动时标签栏先白一下 | 页面主题是加载后才知道的；把最后一次主题写进 `theme.txt`，启动时先按它画 |
| 搜索结果跳转被旧位置覆盖 | 从结果新开标签时把该标签的位置清零，让地址里的锚点决定落点 |
| 星插到地球后面、顺序不对 | 插星要用 `insertBefore(s, a)` 插在 `a.headerlink` 之前（`★ ¶ 🌐`）；想插到地球后面时不要用 `a.nextSibling` 兜底——地球刚好是最后一个节点时它正是地球本身，会插到前面 |
| 收藏行高 67px、行距过大 | 主题给 `li` 的直接子元素 12px 上下外边距（`.rst-content .section ul li > *`）；收藏行是 flex 单行，要 `#rp-favorites ul.fv-list > li > * { margin: 0 }` |
| 收藏行出现「文档 › 文档」 | 收藏文档标题本身（h1）时 `d` 和 `t` 相同，内层 section 标题又等于词条名；存的时候去掉与结尾重复的 crumb，渲染时再合并相邻重复段 |
| 五角星点击后看着像“动了一下” | `.rp-star:hover{transform:scale(1.12)}`：点击会让按钮同时获得 hover 和 focus，放大 12% 时包围盒从 18 变成 20.2、左上角偏移约 1px，鼠标移开又缩回去。去掉 `transform`，焦点反馈改成 `:focus-visible` 的外描边（描边不参与布局，盒子永远 18×18） |

---

## 7. 当前状态

- `app\site` 为唯一数据源：文档目录各自一份 `search-index.js` / `search.html` / `favorites.html` / `version.txt`，站点根目录另有 `docs.json`、`version.txt`、空白选择页 `picker.html` 和它的主题资源副本 `_static`（见 4.10）；索引 1.62 MB（104 篇 / 1239 章节 / 1117 代码块 / 1975 定义）
- `app\updater\build.py` 的界面模板已同步为最新（含顶部导航、灯箱、历史、复制链接、免责声明、标签联动、收藏），确保应用内更新不会回退界面
- `app\tools\` 为重建后的打包工具（原 `tools\` 与 `web\` 已被用户删除）
- 搜索结果按「大类分组」显示：蓝色文档名 + 缩进的小类路径，同锚点去重；`search_body.html` 与索引同步更新
- 窗口最顶部是**原生标签栏**（`src\TabStrip.cs`，与页面解耦）：搜索结果点开的每个词条一个标签，普通链接在标签内跳转，切换会恢复各自滚动位置（见 4.8）
- **收藏**：每个词条右侧一颗圆角五角星（地球右边），点了变黄并存进 `localStorage['rp-favorites']`；右下角五角星入口进收藏页，收藏页有自己的筛选框（见 4.9）
- exe 最近一次构建：430 KB，46.2 MB 总计；页面校验通过（`rp-navbar`、`__rpNav`、灯箱、免责声明、`rp-search-history`、`rp-copy-link`、`rp-group`、`rp-star`、`rp-favorites-fab` 均在位；页面里已无任何标签栏代码）

---

## 8. 已知限制 / 可继续做的事

- 浏览器访问时顶部导航按钮无法置灰（没有宿主通道），桌面版才有效果
- 侧栏不跨页面保留滚动位置（有意为之，见第 6 节）
- 标签切换靠重新加载页面实现（只有一个 WebView2），会重新解析页面；切到从没滚动过的标签时按地址里的锚点定位
- 每份文档的标签存在 `%LOCALAPPDATA%\RenPyDocs\tabs-<id>.txt`，会跨重启保留；想从干净状态开始就逐个关掉
- 标签栏是 WinForms 自绘，不跟随系统缩放做逐显示器 DPI 适配（与窗口其余部分一致）
- 收藏存在 WebView2 的 localStorage 里，只在本机、不跨设备同步；文档里被删除的锚点会让那条收藏跳到页面顶部
- 收藏页只有筛选，没有分组/拖拽排序；数量很少时够用
- 主题自带的 `searchindex.js` 已删除，搜索完全走自建索引；文档页数与官方若有增减，需重新抓取
- 应用内更新依赖网络与 WAF 放行，抓取约 3~5 分钟；失败不影响本地
- 若以后要恢复网页版，可从 `app\site` 直接起静态服务器（页面已适配 `file://` 与 `.local` 虚拟主机以外的普通 http）

---

## 9. 多文档（中文 / 英文）

- 目录：`app\site\zh-CN\`（doc.renpy.cn）和 `app\site\en\`（www.renpy.org/doc/html），各自是一份完整的 Sphinx 镜像；`site\docs.json` 由 `build.py` 生成，桌面端启动时读它拿文档列表、首页和版本
- 英文镜像：`python updater/fetch_en.py site/en` 抓取（106 页 + 95 个资源，约 19.8 MB）。除了 HTML 里出现的资源，它还会顺着 CSS 的 `url()` 把字体（FontAwesome / Lato / Roboto Slab）抓下来——只抓 HTML 里的资源会缺字体，所有图标都变成方框
- 构建：`python updater/build.py site updater/search_body.html <lang>`，`build.ps1` 把 `zh-CN` 和 `en` 各跑一遍；每份文档有自己的 `search-index.js` / `search.html` / `favorites.html` / `version.txt`。**空白选择页只有站点根目录一份**（`site/picker.html`），每次构建都重写，并把外壳那份 `_static` 复制到 `site/_static`
- 删除文档：面板里点垃圾桶 → `rp-confirm-delete` → 宿主删 `site/<id>`、重写 `docs.json`。删的是当前文档时先存标签再删，删完直接进空白选择页（`SetPickerTabs()`）；重下载回来后点它，`SwitchDocument()` 发现是同一份就 `LoadTabs()` 把标签恢复出来
- `SaveTabs()` 有个守卫：当前文档不存在、且内存里只剩那一个选择页标签时整段跳过——否则选择页会把删除时保存的标签集覆盖掉（自检 `tools/SelfTest.cs` 里专门验这条）
- 页面身份：每个页面 `<head>` 里注入 `window.RP_DOC_ID`，`FAB` 里的 `DOCS`（`__RP_DOCS_JS__`）由构建时填好，当前那份是 `cur:true`
- 数据隔离（localStorage 键加文档后缀）：收藏 `rp-favorites:<id>`、搜索历史 `rp-search-history:<id>`；老版本的单键在第一次打开时自动搬到 `zh-CN` 那份
- 标签页：`%LOCALAPPDATA%\RenPyDocs\tabs-<id>.txt`，当前文档记在 `doc.txt`；老的 `tabs.txt` 启动时改名成 `tabs-zh-CN.txt`
- 切换：面板里点另一份 → `rp-switch-doc\n<id>` → 宿主**直接切**，不弹确认框：先存下当前标签集，再换成目标文档的标签集，导航到它的首页
- 主题共享：明暗主题存在 `localStorage['theme']` 和 `theme.txt`，与文档无关。英文主题自带 `.theme-switcher`，所以 FAB 的备用主题按钮只在没有自带按钮的页面上出现（等主题自己建好后再把我们的撤掉）
- 图标：**顶部导航用 FontAwesome 字形**（`fa fa-arrow-left` / `fa-home` / `fa-arrow-right`），**右下角悬浮按钮用内联 SVG**（描边 2.6，书/加号/刷新/放大镜/药瓶/月亮/太阳）；英文页最初图标变方框其实是缺字体文件，`fetch_en.py` 把 `fontawesome-webfont.*` 抓下来后 FA 也能用，但那一版 SVG 更受用，所以两边分开放
- 已知取舍：英文 / 日文 / 每夜版文档里我们的功能页（搜索 / 收藏 / 切换面板）文案仍是中文；「检查更新」六份都能用（`zh-CN` 走整包更新，其余走 `CheckDocUpdate(id)` 按文档站点比对版本 + 构建时间）
- `rp-switch-doc` 也是 `BeginInvoke` 里再弹对话框：在消息回调里直接 `ShowDialog` 会把 WebView 的消息泵堵住
- 除中文外的五份文档默认**不打进发布包**（`app\site` 里只有 `zh-CN`，整包约 54 MB）：切换文档面板和空白选择页上，没下载的文档右侧显示下载按钮（箭头落盘图标），点一下 `rp-doc-download\n<id>` → 宿主用内嵌 python 跑 `download_doc.py <site> <id> …`，全程在后台进程里，当前页面不刷新、可继续阅读
  - 下载中行内显示「下载中…」；页面每 2.5 秒拉一次 `../docs.json`（最多 240 次）看 `downloaded` 变没变，宿主完成时也会调 `window.__rpDocReady(id)` / `__rpDocFailed(id)` 立即刷新那一行
  - `docs.json` 的 `downloaded` 由构建时「目录里有没有 index.html」决定；`FAB` 的 `DOCS` 里也带了 `dl` / `version`，所以面板第一次画就有正确状态
  - 未下载的文档点行本身不会切换（只有右边的下载按钮能用），避免切到不存在的页面
  - 浏览器里没有下载器：下载按钮退回直接打开线上文档
- 下载时会弹一个和「检查更新」同款的进度窗（`ProgressForm`，标题「正在下载文档」、按钮「取消下载」、`CenterOn` 居中、任务栏进度同步）：进度来自爬虫 stdout 的 `pages a/b`（5%→60%）、`assets n`（62%→88%）、`css refs`（90%），重建索引 92%，完成 100%
  - 取消：`CancelRequested` → 杀掉 python 子进程 → 删掉 `site/en` 半成品 → 页面那一行退回下载按钮（`__rpDocFailed`）
  - 关掉阅读器时也会 kill 子进程，否则孤儿 python 会在后台继续把 `site/en` 写回来（这次调试就踩到了，en 目录自己长回来）
  - `ProgressForm` 多了 `(title, cancelText)` 构造，更新流程还是用默认的「正在更新文档 / 取消更新」
- 下载现在是**脱离窗口的独立进程**：`updater/download_doc.py`（内部调用 `fetch_en.crawl` + `build.py`），宿主只负责启动它、每 0.5 秒读 `%LOCALAPPDATA%\RenPyDocs\download-<id>.txt`（三行：百分比 / 文案 / running|done|failed）并推给页面；关掉软件、切文档、切标签都不会中断，下次启动 `ResumeDownloadWatch()` 会把还在跑的进度接回来
  - **一份文档一个站点**：`download_doc.py` 先读 `site/docs.json` 里该文档的 `site`（`build.py` 从 `CATALOG` 写进去的，见 4.14），读不到才用文件里的 `DOCS` 兜底表。以前这里写死了 `fetch_en`，所以“重新下载中文文档”抓的是英文站，写进 `site/zh-CN` 就成了英文内容（那一版修掉的）
  - 六份文档都是同一个 Sphinx 结构，所以共用一个爬虫 `fetch_en.crawl(out, report, wait, base)`；`base` 换成对应站点即可（主机和目录不同而已，见 4.14），首页 title 里的 `Ren’Py` 是弯引号、英文站还带 HTML 实体（`Ren&#39;Py`），校验前要 `html.unescape` 再归一化
  - 站点偶尔会返回浏览器校验页：`crawl()` 抓完首页就检查 title，命中就抛 `BlockedError`，`download_doc.py` 以退出码 9 + 文案「网站要求浏览器校验…」写 failed；宿主 `PollDownload()` 看到这段文案就改用内嵌浏览器下载（`DownloadWithBrowser(id)` → `SiteUpdater`），这条路一定能过校验，但**必须开着软件**。`SiteUpdater` 现在接 `(appRoot, docId, siteUrl, …)`，任意文档都能走
  - **403 / 429 / 503 也走这条路**：nightly 站会对 HTML 文档回 403（静态资源照样 200），爬虫以前把它当普通错误记下来继续跑，结果“页面全丢、资源全在”，最后死在 `build.py`。现在 `do_page()` 对这三个状态码退避重试 4 次，仍失败就抛 `BlockedError`（文案带「浏览器校验」和 HTTP 码），交给内嵌浏览器
  - **图片必须校验内容，不能只管 HTTP 200**：抓得太快时文档站会对图片回一个 987 字节的混淆 `<script>…_0x…` 挑战页（状态码还是 200），照存下来就是“logo 不见了、所有截图空白”。所以 `do_asset()` 会检查：二进制扩展名（png/jpg/gif/ico/woff…）的首字节不能是 `<`，css/js 也不行，命中 `looks_like_challenge()` 或首字节异常就退避重试 4 次（2s/4s/6s），仍失败就抛 `BlockedError` 让整次下载明确失败，绝不留下一个“看起来完整”的坏镜像
  - 同时给所有请求加了全局限速（`throttle()`，最小间隔 60ms，约 16 次/秒），一页一页别冲太快；页面请求也校验（必须是 `<html>`），被挑战同样重试/报错
  - 暂停 = `download-<id>.pause` 文件，爬虫每抓完一批页面/一个资源就检查它并 sleep；页面把圆环点成暂停就发 `rp-doc-pause`，宿主写文件，继续就删文件（`rp-doc-resume`）
  - 取消 = `taskkill /T /F /PID`（启动时把 pid 写进 `download-<id>.pid`）+ 删掉 `site/<id>` 半成品
  - **失败不再静默**：`download_doc.py` 把构建器的 stdout/stderr 写进 `download-<id>.log`，构建失败时报「构建失败（详见 download-<id>.log）」；`PollDownload()` 看到 failed 就 `ShowDownloadFailed()` 弹「下载失败」+ 具体原因（原来只 `EndWatch(false)`，进度窗自己消失、什么都不说）
  - **首页是硬要求**：`crawl()` 抓 `index.html` 失败时不再“继续抓别的页、最后死在 build.py 的 `FileNotFoundError`”，而是重试 3 次、仍没有就抛「没有抓到首页 index.html，请重试」；`build.py` 开头也加了 `缺少 index.html` 的保护（退出码 2）
  - 页面抓取失败记在 `page_errors`（致命：报「有 N 个页面没抓到，请重试」，避免留下有洞的镜像）；资源失败只记 `errors`，其中 404 的字体/图片直接跳过（主题引用了但站点没发布，不算下载失败）
- **进度条只能往前走**（以前会从 60% 掉回 5%）：
  - 页面清单先来自 `searchindex.js` 里的 `docnames`（Sphinx 自己记着全部文档，中文站那个没人链接的 `atl` 页只有它知道），再用首页 toctree 补一遍
  - 页面总数从一开始就是准的（这次中文站 106 篇），发现新页时 `total` 只增不减
  - 页面按**每批 6 个**（等于线程池大小）提交，每完成一个就 `note()` 一次，所以 5%→60% 是平滑爬的，不是一跳
  - 资源全部在**页面抓完之后**再统一下载（那时资源清单已经完整，`60%→88%` 有确定的除数）；最后补 CSS 里引用的字体图片（`88%→90%`），重建索引 `92%`，完成 `100%`
  - `note()` 里 `best[0]` 记住历史最大值，任何计算失误都只会卡住、不会倒退
- **替换只换这一份文档**：`SiteUpdater.Swap()` 以前是把整个 `site/` 目录换掉（会把 `site/en` 一起删掉），现在逐个目录合并——staging 里有的文档目录替换同名目录、根目录文件覆盖，其它文档原样保留，最后删掉 staging
  - `WriteDocsJson()` 的版本号改成从 `<id>/version.txt` 现读（`DocVersion()`），下载完立刻写回 `docs.json`，面板上的版本号不会留着旧值
- **「检查更新」两个信号一起看：版本号 + 构建时间**，任一为新就提示更新。doc.renpy.cn 的 `_static/documentation_options.js` 从 2024-12 起就没再更新（写着 `7.8.5.24121401+unofficial`，`LANGUAGE` 还是 `en`），所以那个文件只当最后的兜底
  - 版本：`FetchSiteInfoAsync(site)` 抓线上 `index.html`，用 `(\d+\.\d+\.\d+)[^<]{0,12}(说明文档首页|說明文件首頁)` 取出站点自己写的版本；没有侧栏版本的语言读该站 `_static/documentation_options.js` 的 `VERSION`（doc.renpy.cn 是例外，那份文件太旧，见上）；本地读 `site/<id>/version.txt`
  - 构建时间：同一个响应的 `Last-Modified`；页面取不到时退回 `FetchBuildAsync()` 对 `searchindex.js` / `index.html` 发 `HEAD`（再退 `GET`）读同一个头
  - 本地构建时间：爬虫抓 `searchindex.js` 时把响应的 `Last-Modified` 写成 `site/<id>/build.txt`（ISO UTC，如 `2026-10-09T07:36:52Z`）；内嵌浏览器那条下载路（`SiteUpdater`）在 `Swap()` 之前也写一份，取不到 header 就用下载时刻
  - 判定：`LooksNewer(线上版本, 本地版本)` 或 `线上构建 > 本地构建` → 弹更新框；两者都不新 →「当前已是最新版本」；本地反而更新 →「无需更新」。弹窗固定四行「本地版本 / 本地构建 / 线上版本 / 线上构建」，缺哪项就不显示哪项；「本次更新不再提示」记的是线上构建时间（没有就记版本号）
  - 都没有（老副本没有 `build.txt`、站点也不给 `Last-Modified`）→ 版本号只从 `documentation_options.js` 取，纯兜底
- 英文文档的「检查更新」也接上了：`rp-check-update` 按当前文档分流——`zh-CN` 走 WAF 那条线，其它文档走 `CheckDocUpdate()`：版本取 `https://www.renpy.org/doc/html/_static/documentation_options.js` 的 `VERSION`（如 `8.5.4.26101301+unofficial`），只比前三段（本地 `site/en/version.txt` 是 `8.5.4`）；现在**再加一条构建时间比较**（`site/en/build.txt` vs 线上 `searchindex.js` 的 `Last-Modified`），任一为新就弹确认框，确认后走**同一套下载流程**（后台进程 + 进度窗 + 可暂停）；更新失败不会删掉现有副本（`dlUpdate` 标记）
  - 行内状态从「下载中…」改成**逐渐填满的圆环**（SVG `stroke-dasharray`/`stroke-dashoffset`，r=8），悬停时圆环中心出现暂停符号，暂停后变成继续符号并常显；进度由宿主 `__rpDocProgress(id,pct,paused)` 推送
  - 下载按钮 / 圆环就放在**左边打勾那个槽**（`.rp-doc-ico`，宽 2.05em，里面叠着 ✓、垃圾桶和一个 `.rp-doc-dlc` 容器）；没下载的行名字变灰（`.rp-doc-item.nodl .rp-doc-name`），右边不再有按钮，只留给版本号
  - 点左边的下载箭头开始下载；下载中那个位置变成圆环，点圆环暂停/继续（`rp-doc-pause` / `rp-doc-resume`）
  - **删除模式**（滑块拨到垃圾桶）会把下载箭头**淡出**（`.rp-docs-list.del .rp-doc-dlc .rp-doc-dl{opacity:0;visibility:hidden;pointer-events:none}`，`.2s ease-in-out`，和 √ ↔ 垃圾桶同一时长、同一缓动；拨回来再淡入），否则那个箭头正压在垃圾桶上、点是点不了删除的；**正在下载的圆环不藏**，照样看得见、照样能点暂停/继续。圆环占着这一格时，垃圾桶跟着退场（`.rp-doc-item.busy .rp-doc-del` 透明 + `pointer-events:none`），免得两个图标叠在一起
- 无头自检：`app\tools\SelfTest.cs`（不参与正式构建）用 `/main:RenPyDocs.SelfTest` 单独编译，构造 `MainForm` 但不显示窗口，验证「每份文档一个标签文件、A 存的标签在 B 里读不到、切回 A 原样恢复、HomeUrl 指向各自首页、文档不在磁盘上时 HomeUrl 落到选择页、`SaveTabs()` 不覆盖删除时保存的标签」；删当前文档那段跑在 `%TEMP%` 的临时站点上（用反射临时换掉 `root`），不碰真实 `site`。跑完自己删掉测试用的 `tabs-test-*.txt`：
  ```
  csc /target:exe /platform:x64 /main:RenPyDocs.SelfTest /out:_selftest.exe ^
      /r:System.Windows.Forms.dll /r:System.Drawing.dll              ^
      /r:System.Net.Http.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
      /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll ^
      src\Program.cs src\TabStrip.cs src\Updater.cs src\Integrity.cs src\AppUpdate.cs tools\SelfTest.cs
  _selftest.exe site selftest.txt
  ```
  （少了 WebView2 那两个 `/r:` 会报 CS0234：`Microsoft.Web.WebView2` 命名空间不存在）
  - 第二种模式 `_selftest.exe <空站点目录> out.txt empty`：站点里一份文档都没有时，`LoadStartupTabs()` 应该只留一个选择页标签、`pickerMode=true`（用来验证“删掉正在读的文档后重开软件”落在空白选择页）
  - 第三种模式 `_selftest.exe x out.txt swap`：`SiteUpdater.Swap()` 只替换 staging 里有的那份文档目录 + 根目录文件，`site/en` 必须还在（验证“下载中文不会把英文删掉”）
  - 第四种模式 `_selftest.exe site\zh-CN out.txt check`：完整性校验，见 4.13（坏样本必须报 4 处，真实文档必须 0 处）
