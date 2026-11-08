using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RenPyDocs
{
    // Where tabs, settings, favourites and the WebView2 profile live. Tests can
    // point RENPYDOCS_DATA at a scratch folder so they never touch real state.
    public static class AppPaths
    {
        public static string DataDir
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("RENPYDOCS_DATA");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RenPyDocs");
            }
        }
    }

    public static class Logs
    {
        private static readonly string LogPath =
            Path.Combine(AppPaths.DataDir, "update.log");

        public static void Write(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(LogPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                FileInfo fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 64 * 1024) fi.Delete();
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // logging must never throw
            }
        }
    }

    // The interface language picked in Settings. Every user-visible string in
    // the shell goes through T(); zh-CN is the base text, the other three are
    // inline translations.
    public static class Lang
    {
        private static string id = "zh-CN";

        public static string Id
        {
            get { return id; }
        }

        public static bool IsChinese
        {
            get { return id == "zh-CN" || id == "zh-TW"; }
        }

        public static void Set(string value)
        {
            if (value == "zh-CN" || value == "zh-TW" || value == "en" || value == "ja")
                id = value;
            else
                id = "zh-CN";
        }

        public static string T(string zh, string tw, string en, string ja)
        {
            switch (id)
            {
                case "zh-TW": return tw;
                case "en": return en;
                case "ja": return ja;
                default: return zh;
            }
        }

        // The shell follows the language with a native UI font: a Japanese or
        // Traditional Chinese string drawn through font linking ends up with a
        // different line box, which shows up as off-centre button captions.
        public static string UiFontFamily
        {
            get
            {
                switch (id)
                {
                    case "zh-TW": return "Microsoft JhengHei UI";
                    case "en": return "Segoe UI";
                    case "ja": return "Yu Gothic UI";
                    default: return "Microsoft YaHei UI";
                }
            }
        }

        public static Font UiFont(float size)
        {
            return new Font(UiFontFamily, size);
        }

        public static Font UiFont(float size, FontStyle style)
        {
            return new Font(UiFontFamily, size, style);
        }
    }

    // settings.txt: one key=value per line. Missing or unreadable keys fall
    // back to the defaults, so an old or hand-edited file never breaks startup.
    public class AppSettings
    {
        public string Language = "zh-CN";
        public bool DocUpdateOnStart = true;
        public bool DocUpdateDaily = true;
        public int SearchHistoryMax = 5;
        public bool AppUpdateOnStart = true;
        public bool AppUpdateDaily = true;
        public string AppUpdateUrl = "";
        public bool ClearTabsOnExit = false;
        // per-document download path: "python" (detached crawler) or
        // "browser" (the hidden WebView2). Missing entries use sensible
        // defaults; the Chinese hosts default to the browser because their
        // crawler always runs into the browser check.
        public Dictionary<string, string> DocDownloadMethods =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string DocDownloadMethod(string id)
        {
            string method;
            if (id != null && DocDownloadMethods.TryGetValue(id, out method))
            {
                method = (method ?? "").ToLowerInvariant();
                if (method == "python" || method == "browser") return method;
            }
            if (string.Equals(id, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "zh-TW", StringComparison.OrdinalIgnoreCase))
                return "browser";
            return "python";
        }

        private static readonly string FilePath =
            Path.Combine(AppPaths.DataDir, "settings.txt");

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            bool hasAppDaily = false;
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.StartsWith("doc-dl-", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = key.Substring(7).Trim();
                        string method = val.ToLowerInvariant();
                        if (id.Length > 0 && (method == "python" || method == "browser"))
                            s.DocDownloadMethods[id] = method;
                        continue;
                    }
                    switch (key)
                    {
                        case "language":
                            if (val == "zh-CN" || val == "zh-TW" || val == "en" || val == "ja")
                                s.Language = val;
                            break;
                        case "doc-update": s.DocUpdateOnStart = val == "1"; break;
                        case "doc-update-daily": s.DocUpdateDaily = val == "1"; break;
                        case "search-history-max":
                            int n;
                            if (int.TryParse(val, out n))
                                s.SearchHistoryMax = Math.Max(0, Math.Min(10, n));
                            break;
                        case "app-update": s.AppUpdateOnStart = val == "1"; break;
                        case "app-update-daily":
                            hasAppDaily = true;
                            s.AppUpdateDaily = val == "1";
                            break;
                        case "app-update-url": s.AppUpdateUrl = val; break;
                        case "clear-tabs": s.ClearTabsOnExit = val == "1"; break;
                    }
                }
            }
            catch (Exception) { }

            // Before this version the app-update switch was saved but did
            // nothing; an old 0 was therefore not a meaningful choice. Migrate
            // files without the daily key to the new on/on defaults once; after
            // a save the daily key exists and later choices are respected.
            if (File.Exists(FilePath) && !hasAppDaily)
            {
                s.AppUpdateOnStart = true;
                s.AppUpdateDaily = true;
            }
            return s;
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string nl = Environment.NewLine;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("language=").Append(Language).Append(nl);
                sb.Append("doc-update=").Append(DocUpdateOnStart ? "1" : "0").Append(nl);
                sb.Append("doc-update-daily=").Append(DocUpdateDaily ? "1" : "0").Append(nl);
                sb.Append("search-history-max=").Append(SearchHistoryMax).Append(nl);
                sb.Append("app-update=").Append(AppUpdateOnStart ? "1" : "0").Append(nl);
                sb.Append("app-update-daily=").Append(AppUpdateDaily ? "1" : "0").Append(nl);
                sb.Append("app-update-url=").Append(AppUpdateUrl).Append(nl);
                sb.Append("clear-tabs=").Append(ClearTabsOnExit ? "1" : "0").Append(nl);
                foreach (KeyValuePair<string, string> pair in DocDownloadMethods)
                {
                    if (string.IsNullOrEmpty(pair.Key) ||
                        pair.Key.IndexOfAny(new char[] { '=', '\r', '\n' }) >= 0)
                        continue;
                    string method = (pair.Value ?? "").ToLowerInvariant();
                    if (method != "python" && method != "browser") continue;
                    sb.Append("doc-dl-").Append(pair.Key).Append('=').
                       Append(method).Append(nl);
                }
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception) { }
        }
    }

    public class MainForm : Form
    {
        // "*.localhost" is resolved locally without DNS; a ".local" name made
        // every navigation wait ~2s for the mDNS lookup to time out.
        private const string Host = "renpy.localhost";
        private readonly string root;
        private readonly WebView2 web;
        private readonly TabStrip tabs;

        // a search hit carries its own label; it wins over the page title for
        // the navigation it starts
        private string pendingTitle;
        private bool closing;
        // the blank "pick a document" page is independent: no tab strip
        private bool pickerMode;

        // true while the shell itself loads a page (tab switch, back, forward):
        // such a load is not an in-tab navigation and must not be recorded
        private bool hostNav;

        // interface language + the startup / exit switches from the settings sheet
        private AppSettings settings = new AppSettings();

        // tabs that "back" closed; "forward" brings them back
        private readonly System.Collections.Generic.List<TabInfo> reopen =
            new System.Collections.Generic.List<TabInfo>();

        // window position / size from the last run
        private static readonly string StateFile =
            Path.Combine(AppPaths.DataDir, "window.txt");

        public MainForm(string root)
        {
            this.root = root;

            settings = AppSettings.Load();
            Lang.Set(settings.Language);

            Text = Lang.T("Ren'Py 中文文档", "Ren'Py 中文文件",
                          "Ren'Py Documentation", "Ren'Py ドキュメント");
            MinimumSize = new Size(880, 560);
            BackColor = Color.White;
            LoadDocs();
            ApplySavedGeometry();

            try
            {
                string icon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icon))
                {
                    Icon = new Icon(icon);
                }
                else
                {
                    // A release folder may not carry app.ico; the exe already has
                    // the same icon embedded, so use that instead of a blank one.
                    System.Drawing.Icon embedded =
                        System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (embedded != null) Icon = embedded;
                }
            }
            catch (Exception)
            {
                // a missing icon is never fatal
            }

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.White;
            Controls.Add(web);

            tabs = new TabStrip();
            tabs.Dock = DockStyle.Top;
            tabs.SwitchTo = delegate(int i) { ActivateTab(i); };
            tabs.NewTab = delegate { AddTab(HomeUrl); };
            tabs.CloseTab = delegate(int i) { CloseTab(i); };
            Controls.Add(tabs);
            Application.AddMessageFilter(new TabKeyFilter(this));

            Load += OnLoad;
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                string udf = Path.Combine(AppPaths.DataDir, "WebView2");
                Directory.CreateDirectory(udf);

                CoreWebView2Environment env =
                    await CoreWebView2Environment.CreateAsync(null, udf, null);
                await web.EnsureCoreWebView2Async(env);

                CoreWebView2 core = web.CoreWebView2;

                // ":visited" link colours come from browsing history; start each
                // run with a clean slate so nothing stays purple after a restart
                try
                {
                    await core.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.BrowsingHistory);
                }
                catch (Exception)
                {
                    // cosmetic only
                }

                // serve ./site over a local https origin so the mirror behaves
                // exactly like it does under a normal web server
                core.SetVirtualHostNameToFolderMapping(
                    Host, root, CoreWebView2HostResourceAccessKind.Allow);

                // the page-side UI (floating buttons, search / favourites /
                // picker) reads its settings from here; it must exist before
                // the first page script runs
                await core.AddScriptToExecuteOnDocumentCreatedAsync(PageSettingsScript());

                core.NavigationStarting += (s2, a) =>
                {
                    string uri = a.Uri ?? "";
                    if (uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase))
                        return;

                    // about:blank is used internally when swapping the mirror;
                    // only real web/mail links belong to the system browser
                    if (!IsExternalLink(uri)) return;

                    a.Cancel = true;
                    OpenExternal(uri);
                };

                core.NewWindowRequested += (s2, a) =>
                {
                    a.Handled = true;
                    if (a.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase))
                        core.Navigate(a.Uri);
                    else if (IsExternalLink(a.Uri))
                        OpenExternal(a.Uri);
                };

                core.DocumentTitleChanged += (s2, a) =>
                {
                    if (string.IsNullOrEmpty(core.DocumentTitle)) return;
                    Text = LocalizedWindowTitle(core.DocumentTitle);
                    // a page script may rewrite its own title after load (the
                    // helper pages do, to follow the interface language)
                    TabInfo cur = Current();
                    if (pendingTitle == null && cur != null && !string.IsNullOrEmpty(core.Source))
                    {
                        cur.Title = CleanTitle(core.DocumentTitle, core.Source);
                        tabs.Invalidate();
                    }
                };

                // small messages from the page: update check, back navigation,
                // opening a search hit in a tab, and the colour scheme
                core.WebMessageReceived += (s2, msgArgs) =>
                {
                    string message = null;
                    try { message = msgArgs.TryGetWebMessageAsString(); }
                    catch (Exception) { }

                    if (message == null) return;

                    if (message == "rp-check-update")
                    {
                        if (CurrentDocId == "zh-CN") CheckForUpdates(true);
                        else
                        {
                            string docId = CurrentDocId;
                            BeginInvoke((Action)(() => { if (!IsDisposed) CheckDocUpdate(docId, true); }));
                        }
                    }
                    else if (message == "rp-open-settings")
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (IsDisposed) return;
                            string[] docIds = new string[docs.Count];
                            string[] docNames = new string[docs.Count];
                            for (int i = 0; i < docs.Count; i++)
                            {
                                docIds[i] = docs[i].Id;
                                docNames[i] = docs[i].Name;
                            }
                            using (SettingsDialog dlg = new SettingsDialog(settings, docIds, docNames))
                            {
                                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null)
                                    return;      // 取消: nothing is written
                                string oldLanguage = Lang.Id;
                                int oldHistory = settings.SearchHistoryMax;
                                bool oldAppUpdate = settings.AppUpdateOnStart;
                                string oldAppUrl = settings.AppUpdateUrl;
                                settings = dlg.Result;
                                settings.Save();
                                Lang.Set(settings.Language);
                                if (oldAppUpdate != settings.AppUpdateOnStart ||
                                    !string.Equals(oldAppUrl, settings.AppUpdateUrl,
                                                   StringComparison.Ordinal))
                                {
                                    // a different switch / URL should be checked
                                    // on the next start even if today was used
                                    try
                                    {
                                        if (File.Exists(AppCheckFile))
                                            File.Delete(AppCheckFile);
                                    }
                                    catch (Exception) { }
                                }
                                Log("settings: language=" + settings.Language +
                                    " doc-update=" + settings.DocUpdateOnStart +
                                    " app-update=" + settings.AppUpdateOnStart +
                                    " clear-tabs=" + settings.ClearTabsOnExit +
                                    " search-history-max=" + settings.SearchHistoryMax +
                                    " app-url=" + settings.AppUpdateUrl);
                                bool languageChanged = Lang.Id != oldLanguage;
                                bool historyChanged = settings.SearchHistoryMax != oldHistory;
                                if (languageChanged || historyChanged)
                                    ReloadPageWithSettings(historyChanged ? settings.SearchHistoryMax : -1);
                            }
                        }));
                    }
                    else if (message == "rp-back")
                        GoBack();
                    else if (message == "rp-fwd")
                        GoForward();
                    else if (message.StartsWith("rp-open-tab\n", StringComparison.Ordinal))
                    {
                        string[] parts = message.Split('\n');
                        if (parts.Length >= 3) OpenSearchHit(parts[2], parts[1]);
                    }
                    else if (message.StartsWith("rp-title\n", StringComparison.Ordinal))
                    {
                        TabInfo cur = Current();
                        if (cur != null)
                        {
                            cur.Title = message.Substring(9);
                            tabs.Invalidate();
                        }
                    }
                    else if (message.StartsWith("rp-confirm-delete\n", StringComparison.Ordinal))
                    {
                        // the page asks before dropping a document; the dialog is
                        // the same custom sheet the update prompt uses
                        string[] parts = message.Split('\n');
                        if (parts.Length >= 2)
                        {
                            string id = parts[1];
                            string name = parts.Length >= 3 ? parts[2].Trim() : "";
                            CoreWebView2 owner = core;
                            BeginInvoke((Action)(() =>
                            {
                                if (IsDisposed) return;
                                // the document being read can be deleted too: it
                                // simply ends on the picker page afterwards
                                bool current = id == CurrentDocId;
                                // the name comes from the row that was clicked; an
                                // old page that does not send one still gets a
                                // readable sentence
                                string body = current
                                    ? (name.Length > 0
                                        ? string.Format(Lang.T(
                                            "本文档「{0}」正在使用，是否删除？除标签页外的数据（历史记录、收藏等）会保留",
                                            "本文件「{0}」正在使用，是否刪除？除標籤頁外的資料（歷史記錄、收藏等）會保留",
                                            "This document ({0}) is open. Delete it? Everything except its tabs (history, favourites, etc.) will be kept.",
                                            "「{0}」は使用中です。削除しますか？タブ以外のデータ（履歴、お気に入りなど）は保持されます。"), name)
                                        : Lang.T("本文档正在使用，是否删除？除标签页外的数据（历史记录、收藏等）会保留",
                                                 "本文件正在使用，是否刪除？除標籤頁外的資料（歷史記錄、收藏等）會保留",
                                                 "This document is open. Delete it? Everything except its tabs (history, favourites, etc.) will be kept.",
                                                 "この文書は使用中です。削除しますか？タブ以外のデータ（履歴、お気に入りなど）は保持されます。"))
                                    : (name.Length > 0
                                        ? string.Format(Lang.T(
                                            "是否要删除「{0}」？除标签页外的数据（历史记录、收藏等）会保留",
                                            "是否要刪除「{0}」？除標籤頁外的資料（歷史記錄、收藏等）會保留",
                                            "Delete {0}? Everything except its tabs (history, favourites, etc.) will be kept.",
                                            "「{0}」を削除しますか？タブ以外のデータ（履歴、お気に入りなど）は保持されます。"), name)
                                        : Lang.T("是否要删除此文档？除标签页外的数据（历史记录、收藏等）会保留",
                                                 "是否要刪除此文件？除標籤頁外的資料（歷史記錄、收藏等）會保留",
                                                 "Delete this document? Everything except its tabs (history, favourites, etc.) will be kept.",
                                                 "この文書を削除しますか？タブ以外のデータ（履歴、お気に入りなど）は保持されます。"));
                                bool ok;
                                using (ConfirmDialog dlg = new ConfirmDialog(
                                    Lang.T("删除文档", "刪除文件", "Delete document", "文書を削除"),
                                    body,
                                    Lang.T("删除", "刪除", "Delete", "削除"),
                                    Lang.T("取消", "取消", "Cancel", "キャンセル")))
                                    ok = dlg.ShowDialog(this) == DialogResult.OK;
                                if (!ok) return;

                                // never pull the folder from under a running download
                                if (dlId == id) CancelDownload();
                                // tabs are the only data that goes with the
                                // document; favourites and search history live
                                // in localStorage and are left alone

                                bool gone = DeleteDocFiles(id);
                                if (!gone)
                                {
                                    using (InfoDialog hint = new InfoDialog(
                                        Lang.T("删除失败", "刪除失敗",
                                               "Delete failed", "削除できませんでした"),
                                        Lang.T("请稍后重试……", "請稍後重試……",
                                               "Please try again later…",
                                               "しばらくしてからもう一度お試しください……"),
                                        null,
                                        Lang.T("删除文档", "刪除文件",
                                               "Delete document", "文書を削除")))
                                        hint.ShowDialog(this);
                                    return;
                                }

                                DeleteTabsForDoc(id);

                                if (current)
                                {
                                    SetPickerTabs();
                                    SetPickerMode(true);
                                    Navigate(tabs.Tabs[tabs.Active]);
                                    return;
                                }
                                try
                                {
                                    owner.ExecuteScriptAsync(
                                        "window.__rpDocDeleted&&window.__rpDocDeleted(" +
                                        JsString(id) + ")");
                                }
                                catch (Exception) { }
                            }));
                        }
                    }
                    else if (message.StartsWith("rp-switch-doc\n", StringComparison.Ordinal))
                    {
                        // the dialog is modal: never open it inside the message
                        // handler, or the WebView stops pumping messages
                        string target = message.Substring(14).Trim();
                        BeginInvoke((Action)(() =>
                        {
                            if (!IsDisposed) SwitchDocument(target);
                        }));
                    }
                    else if (message.StartsWith("rp-doc-pause\n", StringComparison.Ordinal))
                    {
                        string docId = message.Substring(13).Trim();
                        BeginInvoke((Action)(() => { if (!IsDisposed) SetDownloadPaused(docId, true); }));
                    }
                    else if (message.StartsWith("rp-doc-resume\n", StringComparison.Ordinal))
                    {
                        string docId = message.Substring(14).Trim();
                        BeginInvoke((Action)(() => { if (!IsDisposed) SetDownloadPaused(docId, false); }));
                    }
                    else if (message.StartsWith("rp-doc-download\n", StringComparison.Ordinal))
                    {
                        // runs in the background; the page keeps reading what it has
                        string docId = message.Substring(16).Trim();
                        BeginInvoke((Action)(() =>
                        {
                            if (!IsDisposed) DownloadDocument(docId);
                        }));
                    }
                    else if (message.StartsWith("rp-theme:", StringComparison.Ordinal))
                    {
                        bool dark = message.IndexOf("dark", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (tabs.Dark != dark)
                        {
                            tabs.Dark = dark;
                            tabs.Invalidate();
                            web.DefaultBackgroundColor = dark
                                ? Color.FromArgb(20, 20, 20) : Color.White;
                            SaveTheme(dark);
                        }
                    }
                };

                // let the page grey out back / forward when they cannot be used
                core.HistoryChanged += (s2, a) => PushNavState(core);
                core.NavigationCompleted += (s2, a) =>
                {
                    SetPickerMode(IsPickerUrl(core.Source));
                    PushNavState(core);
                    OnPageReady(core);
                    if (dlId != null) ReportProgress(dlId, dlLast, dlPaused);
                };

                LoadStartupTabs();
                LoadTheme();
                ResumeDownloadWatch();
                tabs.Invalidate();
                core.Navigate(tabs.Tabs[tabs.Active].Url);
                await StartupChecks();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Lang.T("无法启动内置浏览器组件。\n\n" +
                           "请先安装 Microsoft Edge WebView2 运行时：\n",
                           "無法啟動內建瀏覽器元件。\n\n" +
                           "請先安裝 Microsoft Edge WebView2 執行環境：\n",
                           "The built-in browser component could not start.\n\n" +
                           "Please install the Microsoft Edge WebView2 Runtime:\n",
                           "内蔵ブラウザを起動できませんでした。\n\n" +
                           "Microsoft Edge WebView2 ランタイムをインストールしてください：\n") +
                    "https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" +
                    Lang.T("详细信息：", "詳細資訊：", "Details: ", "詳細：") + ex.Message,
                    Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        // -------------------------------------------------------------- tabs
        // One WebView2 for every tab: switching means loading that address
        // again, so a tab only has to remember where the reader was.
        private static readonly string TabsDir = AppPaths.DataDir;

        // each document keeps its own tab list: switching documents swaps sets
        private string TabsFile
        {
            get { return Path.Combine(TabsDir, "tabs-" + CurrentDocId + ".txt"); }
        }

        // ---------------------------------------------------------- documents
        // site/docs.json is written by updater/build.py
        private class Doc
        {
            public string Id;
            public string Name;
            public string Home;
            public string Version;
            public string Site;      // where this document is downloaded from
        }

        private static readonly string DocFile = Path.Combine(TabsDir, "doc.txt");

        private readonly System.Collections.Generic.List<Doc> docs =
            new System.Collections.Generic.List<Doc>();

        private string CurrentDocId = "zh-CN";

        private Doc FindDoc(string id)
        {
            for (int i = 0; i < docs.Count; i++)
                if (string.Equals(docs[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return docs[i];
            return null;
        }

        private Doc CurrentDoc
        {
            get
            {
                Doc d = FindDoc(CurrentDocId);
                return d ?? (docs.Count > 0 ? docs[0] : null);
            }
        }

        private static string JsonField(string obj, string key)
        {
            Match m = Regex.Match(obj, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private void LoadDocs()
        {
            docs.Clear();
            try
            {
                string json = File.ReadAllText(Path.Combine(root, "docs.json"));
                foreach (Match m in Regex.Matches(json, "\\{[^{}]*\\}"))
                {
                    Doc d = new Doc();
                    d.Id = JsonField(m.Value, "id");
                    d.Name = JsonField(m.Value, "name");
                    d.Home = JsonField(m.Value, "home");
                    d.Version = JsonField(m.Value, "version");
                    d.Site = JsonField(m.Value, "site");
                    if (d.Id.Length > 0) docs.Add(d);
                }
            }
            catch (Exception) { }

            if (docs.Count == 0)
            {
                Doc d = new Doc();
                d.Id = "zh-CN";
                d.Name = Lang.T("Ren'Py 中文文档", "Ren'Py 中文文件",
                                "Ren'Py Documentation", "Ren'Py ドキュメント");
                d.Home = "zh-CN/index.html";
                d.Version = "";
                docs.Add(d);
            }

            try
            {
                if (!Directory.Exists(TabsDir)) Directory.CreateDirectory(TabsDir);
                string old = Path.Combine(TabsDir, "tabs.txt");
                if (File.Exists(old))
                {
                    string moved = Path.Combine(TabsDir, "tabs-zh-CN.txt");
                    if (!File.Exists(moved)) File.Move(old, moved);
                    else File.Delete(old);
                }
            }
            catch (Exception) { }

            try
            {
                string saved = File.ReadAllText(DocFile).Trim();
                if (saved.Length > 0 && FindDoc(saved) != null) CurrentDocId = saved;
            }
            catch (Exception) { }
        }

        private string HomeUrl
        {
            get
            {
                Doc d = CurrentDoc;
                // a document that was deleted has no home page: the picker is
                // the only page left that can be shown
                if (d == null || !CurrentDocAvailable) return PickerUrl;
                return "https://" + Host + "/" + d.Home;
            }
        }

        // the blank "pick a document" page lives at the site root, so deleting
        // a document folder never takes it away
        private static string PickerUrl
        {
            get { return "https://" + Host + "/picker.html"; }
        }

        // false once the document being read has been deleted from disk
        private bool CurrentDocAvailable
        {
            get
            {
                Doc d = FindDoc(CurrentDocId);
                return d != null && File.Exists(Path.Combine(root, d.Id, "index.html"));
            }
        }

        // the picker tab set: one tab, the blank page, nothing else to show
        private void SetPickerTabs()
        {
            reopen.Clear();
            TabInfo tab = new TabInfo();
            tab.Url = PickerUrl;
            tab.Title = Lang.T("请选择文档", "請選擇文件",
                               "Choose a document", "文書を選択");
            tab.History.Add(PickerUrl);
            tabs.Tabs.Clear();
            tabs.Tabs.Add(tab);
            tabs.Active = 0;
            tabs.Invalidate();
        }

        private TabInfo Current()
        {
            if (tabs.Active < 0 || tabs.Active >= tabs.Tabs.Count) return null;
            return tabs.Tabs[tabs.Active];
        }

        private void ActivateTab(int index)
        {
            if (index < 0 || index >= tabs.Tabs.Count || index == tabs.Active) return;
            ActivateTabAsync(index);
        }

        private async void ActivateTabAsync(int index)
        {
            TabInfo previous = Current();
            if (previous != null) await SavePosition(previous);

            tabs.Active = index;
            tabs.Invalidate();
            Navigate(tabs.Tabs[index]);
        }

        // a tab for the address, or a new one when it is not open yet
        private void AddTab(string url)
        {
            StartTab(url, null, false);
        }

        // a search hit: open it in its own tab, labelled with the entry name
        private void OpenSearchHit(string url, string title)
        {
            StartTab(url, title, true);
        }

        private async void StartTab(string url, string title, bool jumpToAnchor)
        {
            TabInfo previous = Current();
            if (previous != null) await SavePosition(previous);

            TabInfo tab = null;
            if (jumpToAnchor)
            {
                for (int i = 0; i < tabs.Tabs.Count; i++)
                {
                    if (SameUrl(tabs.Tabs[i].Url, url)) { tab = tabs.Tabs[i]; break; }
                }
                // an explicit jump: the anchor in the address decides, not the
                // offset this tab happened to remember
                if (tab != null) { tab.Y = 0; tab.YUrl = ""; }
            }

            reopen.Clear();
            if (tab == null)
            {
                tab = new TabInfo();
                tab.Url = url;
                tab.Title = title ?? "";
                tab.History.Add(url);
                if (jumpToAnchor && previous != null && tabs.Tabs.Contains(previous))
                    tab.Opener = previous;
                tabs.Tabs.Add(tab);
                while (tabs.Tabs.Count > 16) tabs.Tabs.RemoveAt(0);
            }
            else if (!string.IsNullOrEmpty(title))
            {
                tab.Title = title;
            }

            tabs.Active = tabs.Tabs.IndexOf(tab);
            if (tabs.Active < 0) tabs.Active = 0;
            pendingTitle = string.IsNullOrEmpty(title) ? null : title;
            tabs.Invalidate();
            Navigate(tab);
        }

        private async void CloseTab(int index)
        {
            if (index < 0 || index >= tabs.Tabs.Count) return;

            TabInfo previous = Current();
            bool wasActive = index == tabs.Active;
            if (previous != null && !wasActive) await SavePosition(previous);

            tabs.Tabs.RemoveAt(index);

            if (tabs.Tabs.Count == 0)
            {
                TabInfo home = new TabInfo();
                home.Url = HomeUrl;
                home.Title = HomeTitle();
                tabs.Tabs.Add(home);
                tabs.Active = 0;
                tabs.Invalidate();
                Navigate(home);
                return;
            }

            if (wasActive)
            {
                tabs.Active = Math.Min(index, tabs.Tabs.Count - 1);
                tabs.Invalidate();
                Navigate(tabs.Tabs[tabs.Active]);
            }
            else
            {
                if (index < tabs.Active) tabs.Active--;
                tabs.Invalidate();
            }
        }

        private void CycleTab(int delta)
        {
            int n = tabs.Tabs.Count;
            if (n < 2) return;
            int a = tabs.Active;
            if (a < 0) a = 0;
            ActivateTab((a + delta + n) % n);
        }

        private void Navigate(TabInfo tab)
        {
            if (tab == null) return;
            Navigate(tab, tab.Url);
        }

        private void Navigate(TabInfo tab, string url)
        {
            try
            {
                CoreWebView2 core = web.CoreWebView2;
                if (core == null || tab == null || string.IsNullOrEmpty(url)) return;

                tab.Url = url;
                if (tab.History.Count == 0) tab.History.Add(url);
                hostNav = true;
                core.Navigate(url);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------ back / forward
        // One WebView2 means the browser's own history would also walk across
        // tabs; the shell keeps its own per-tab history instead, and closing
        // back to the tab a hit was opened from keeps the strip tidy.
        private void GoBack()
        {
            TabInfo tab = Current();
            if (tab == null) return;

            if (tab.History.Count > 1)
            {
                string cur = tab.History[tab.History.Count - 1];
                tab.History.RemoveAt(tab.History.Count - 1);
                tab.Forward.Add(cur);
                Navigate(tab, tab.History[tab.History.Count - 1]);
                PushNavState();
                return;
            }

            // the tab was opened from another one: go back there and drop it
            TabInfo opener = tab.Opener;
            if (opener == null || !tabs.Tabs.Contains(opener)) return;

            reopen.Add(tab);
            tabs.Tabs.Remove(tab);
            tabs.Active = tabs.Tabs.IndexOf(opener);
            if (tabs.Active < 0) tabs.Active = 0;
            tabs.Invalidate();

            TabInfo target = Current();
            if (target != null) Navigate(target);
            PushNavState();
        }

        private void GoForward()
        {
            TabInfo tab = Current();

            if (tab != null && tab.Forward.Count > 0)
            {
                string next = tab.Forward[tab.Forward.Count - 1];
                tab.Forward.RemoveAt(tab.Forward.Count - 1);
                tab.History.Add(next);
                Navigate(tab, next);
                PushNavState();
                return;
            }

            if (reopen.Count == 0) return;

            TabInfo closed = reopen[reopen.Count - 1];
            reopen.RemoveAt(reopen.Count - 1);

            TabInfo again = new TabInfo();
            again.Url = closed.Url;
            again.Title = closed.Title;
            again.Opener = tab;
            again.History.Add(closed.Url);
            tabs.Tabs.Add(again);
            tabs.Active = tabs.Tabs.Count - 1;
            tabs.Invalidate();
            Navigate(again);
            PushNavState();
        }

        // the page says where it is; remember that for the tab it belongs to
        private async Task SavePosition(TabInfo tab)
        {
            try
            {
                CoreWebView2 core = web.CoreWebView2;
                if (core == null || tab == null) return;

                string raw = await core.ExecuteScriptAsync(
                    "location.href+String.fromCharCode(1)+Math.round(window.pageYOffset)");
                string s = JsonText(raw);
                int at = s.IndexOf('\u0001');
                if (at < 0) return;

                double y;
                if (!double.TryParse(s.Substring(at + 1), out y)) return;

                tab.Url = s.Substring(0, at);
                tab.Y = y;
                tab.YUrl = tab.Url;
            }
            catch (Exception) { }
        }

        private void OnPageReady(CoreWebView2 core)
        {
            try
            {
                TabInfo tab = Current();
                if (tab == null)
                {
                    tab = new TabInfo();
                    tabs.Tabs.Add(tab);
                    tabs.Active = tabs.Tabs.Count - 1;
                }

                string src = core.Source ?? "";
                bool fromHost = hostNav;
                hostNav = false;
                bool changed = !SameUrl(tab.Url, src);

                // a page the reader opened by clicking a link counts as history
                // inside this tab; a tab switch / back / forward does not
                if (!fromHost && src.Length > 0)
                {
                    if (tab.History.Count == 0) tab.History.Add(src);
                    else
                    {
                        string last = tab.History[tab.History.Count - 1];
                        if (SameDoc(last, src)) tab.History[tab.History.Count - 1] = src;
                        else
                        {
                            tab.History.Add(src);
                            tab.Forward.Clear();
                        }
                    }
                }

                if (pendingTitle != null)
                {
                    tab.Title = pendingTitle;
                    pendingTitle = null;
                }
                else if (changed || string.IsNullOrEmpty(tab.Title))
                {
                    tab.Title = CleanTitle(core.DocumentTitle, src);
                }

                tab.Url = src;
                tabs.Invalidate();
                RestorePosition(tab, src);
                PushNavState(core);
            }
            catch (Exception) { }
        }

        private void RestorePosition(TabInfo tab, string src)
        {
            if (tab == null || tab.Y <= 0 || !SameUrl(tab.YUrl, src)) return;

            string js =
                "(function(){var y=" + ((int)tab.Y) + ";" +
                "function s(){if(y>=0)window.scrollTo(0,y);}" +
                "s();setTimeout(s,60);setTimeout(s,180);" +
                "['wheel','mousedown','keydown','touchstart'].forEach(function(e){" +
                "window.addEventListener(e,function(){y=-1;},{passive:true,once:true});});})();";
            try { web.CoreWebView2.ExecuteScriptAsync(js); }
            catch (Exception) { }
        }

        private static bool SameUrl(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string NoHash(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int cut = url.IndexOf('#');
            return cut >= 0 ? url.Substring(0, cut) : url;
        }

        // same page, only the fragment differs
        private static bool SameDoc(string a, string b)
        {
            return string.Equals(NoHash(a), NoHash(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string HomeTitle()
        {
            return Lang.T("首页", "首頁", "Home", "ホーム");
        }

        // the helper pages are the app's own UI: their <title> is Chinese text
        // baked into the page, so map it here for the tab label
        private static string KnownPageTitle(string t)
        {
            switch (t)
            {
                case "搜索":
                    return Lang.T("搜索", "搜尋", "Search", "検索");
                case "收藏":
                    return Lang.T("收藏", "收藏", "Favourites", "お気に入り");
                case "请选择文档":
                    return Lang.T("请选择文档", "請選擇文件",
                                  "Choose a document", "文書を選択");
                default:
                    return t;
            }
        }

        // "搜索 — Ren'Py 中文文档": translate the page-name part, keep the
        // document site name that follows the dash
        private static string LocalizedWindowTitle(string title)
        {
            string t = title ?? "";
            int dash = t.LastIndexOf('—');
            if (dash <= 0) return t;
            string head = t.Substring(0, dash).Trim();
            string tail = t.Substring(dash + 1).Trim();
            return KnownPageTitle(head) + " — " + tail;
        }

        private static string CleanTitle(string title, string url)
        {
            string t = (title ?? "").Trim();
            int dash = t.LastIndexOf('—');
            if (dash > 0 && t.IndexOf("Ren", dash, StringComparison.OrdinalIgnoreCase) >= 0)
                t = t.Substring(0, dash).Trim();
            if (IsHome(url)) return HomeTitle();
            if (t.Length == 0)
                return Lang.T("文档", "文件", "Document", "文書");
            return KnownPageTitle(t);
        }

        private static bool IsHome(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string u = url;
            int cut = u.IndexOfAny(new char[] { '?', '#' });
            if (cut >= 0) u = u.Substring(0, cut);
            return u.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) ||
                   u.EndsWith("/zh-CN/", StringComparison.OrdinalIgnoreCase);
        }

        // the page hands back a JSON string; URLs never need a full parse
        private static string JsonText(string js)
        {
            if (string.IsNullOrEmpty(js)) return "";
            if (js.Length >= 2 && js[0] == '"' && js[js.Length - 1] == '"')
            {
                string s = js.Substring(1, js.Length - 2);
                s = s.Replace("\\\"", "\"").Replace("\\\\", "\\");
                return s.Replace("\\/", "/");
            }
            return js;
        }

        // ------------------------------------------------------- tab storage
        private static readonly string ThemeFile =
            Path.Combine(AppPaths.DataDir, "theme.txt");

        // the window chrome should already match the page colour on the very
        // first paint, not only after the page reports its theme
        // What the window opens on: the tabs saved for the current document, or
        // the picker when that document is no longer on disk.
        private void LoadStartupTabs()
        {
            if (!CurrentDocAvailable)
            {
                // the document that was last open has been deleted: start on
                // the picker page instead of a home page that is gone
                SetPickerTabs();
                SetPickerMode(true);
                return;
            }
            if (LoadTabs()) return;
            TabInfo home = new TabInfo();
            home.Url = HomeUrl;
            home.Title = HomeTitle();
            tabs.Tabs.Add(home);
            tabs.Active = 0;
        }

        private void LoadTheme()
        {
            try
            {
                if (!File.Exists(ThemeFile)) return;
                bool dark = File.ReadAllText(ThemeFile).Trim() == "dark";
                tabs.Dark = dark;
                web.DefaultBackgroundColor = dark ? Color.FromArgb(20, 20, 20) : Color.White;
            }
            catch (Exception) { }
        }

        private static void SaveTheme(bool dark)
        {
            try
            {
                string dir = Path.GetDirectoryName(ThemeFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ThemeFile, dark ? "dark" : "light");
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------ delete
        // Removing a document only touches site/<id> and its tab list: the
        // favourites, search history and browsing data of that document stay.
        private bool DeleteDocFiles(string id)
        {
            try
            {
                string folder = Path.Combine(root, id);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Log("doc: deleted " + id);
            }
            catch (Exception ex)
            {
                Log("doc: delete failed " + id + ": " + ex.Message);
                return false;
            }

            WriteDocsJson();
            LoadDocs();
            return true;
        }

        private void DeleteTabsForDoc(string id)
        {
            try
            {
                string file = Path.Combine(TabsDir, "tabs-" + id + ".txt");
                if (File.Exists(file)) File.Delete(file);
                // pre-1.0.0 builds used one global tabs.txt for zh-CN
                if (string.Equals(id, "zh-CN", StringComparison.OrdinalIgnoreCase))
                {
                    string legacy = Path.Combine(TabsDir, "tabs.txt");
                    if (File.Exists(legacy)) File.Delete(legacy);
                }
                Log("tabs: deleted for " + id);
            }
            catch (Exception ex)
            {
                Log("tabs: delete failed for " + id + ": " + ex.Message);
            }
        }

        // "8.5.4" for a document folder that carries version.txt, else its
        // catalogue entry
        private string DocVersion(string id)
        {
            try
            {
                string vp = Path.Combine(root, id, "version.txt");
                if (File.Exists(vp))
                {
                    string v = File.ReadAllText(vp).Trim();
                    Match m = Regex.Match(v, @"^\d+\.\d+\.\d+");
                    if (m.Success) return m.Value;
                    if (v.Length > 0) return v;
                }
            }
            catch (Exception) { }
            Doc d = FindDoc(id);
            return d != null ? d.Version : "";
        }

        private void WriteDocsJson()
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("[\n");
                for (int i = 0; i < docs.Count; i++)
                {
                    Doc d = docs[i];
                    bool downloaded = File.Exists(Path.Combine(root, d.Id, "index.html"));
                    // the version always comes from the document's own folder:
                    // after a download the in-memory list may still be stale
                    string version = downloaded ? DocVersion(d.Id) : "";
                    if (i > 0) sb.Append(",\n");
                    sb.Append("  {\n");
                    sb.Append("    \"id\": ").Append(JsString(d.Id)).Append(",\n");
                    sb.Append("    \"name\": ").Append(JsString(d.Name)).Append(",\n");
                    sb.Append("    \"site\": ").Append(JsString(d.Site)).Append(",\n");
                    sb.Append("    \"home\": ").Append(JsString(d.Home)).Append(",\n");
                    sb.Append("    \"version\": ").Append(JsString(version)).Append(",\n");
                    sb.Append("    \"downloaded\": ").Append(downloaded ? "true" : "false").Append("\n");
                    sb.Append("  }");
                }
                sb.Append("\n]\n");
                File.WriteAllText(Path.Combine(root, "docs.json"), sb.ToString(),
                                  new System.Text.UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------ download
        // The download runs as a separate, detached python process, so closing
        // the reader never stops it. Progress and the pause switch live in two
        // small files, which also lets a fresh window pick the job up again.
        private static readonly string DocStateDir = AppPaths.DataDir;

        private static readonly string PythonExe = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "updater", "python", "python.exe");

        private string dlId;
        private bool dlUpdate;
        private bool dlPaused;
        private int dlLast;
        private int dlTicks;
        private System.Windows.Forms.Timer dlTimer;
        private ProgressForm dlForm;
        // set while the Chinese document is being fetched in-process
        private CancellationTokenSource zhCts;

        private static string ProgressPath(string id)
        {
            return Path.Combine(DocStateDir, "download-" + id + ".txt");
        }

        private static string PausePath(string id)
        {
            return Path.Combine(DocStateDir, "download-" + id + ".pause");
        }

        private static string PidPath(string id)
        {
            return Path.Combine(DocStateDir, "download-" + id + ".pid");
        }

        private static void DeleteDownloadState(string id)
        {
            try
            {
                if (File.Exists(ProgressPath(id))) File.Delete(ProgressPath(id));
                if (File.Exists(PausePath(id))) File.Delete(PausePath(id));
                if (File.Exists(PidPath(id))) File.Delete(PidPath(id));
            }
            catch (Exception) { }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (Exception) { return false; }
        }

        // a download that was running when the reader closed is picked up again
        private void ResumeDownloadWatch()
        {
            try
            {
                foreach (Doc d in docs)
                {
                    if (dlId != null) break;
                    if (File.Exists(Path.Combine(root, d.Id, "index.html"))) continue;
                    if (!File.Exists(ProgressPath(d.Id))) continue;

                    bool alive = false;
                    try
                    {
                        if (File.Exists(PidPath(d.Id)))
                        {
                            int pid;
                            if (int.TryParse(File.ReadAllText(PidPath(d.Id)).Trim(), out pid))
                                alive = IsProcessAlive(pid);
                        }
                    }
                    catch (Exception) { }

                    if (alive)
                    {
                        BeginWatch(d.Id);
                        continue;
                    }

                    // the downloader died with the old window: clean the leftovers
                    try
                    {
                        File.Delete(ProgressPath(d.Id));
                        File.Delete(PausePath(d.Id));
                        File.Delete(PidPath(d.Id));
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private void DownloadDocument(string id)
        {
            DownloadDocument(id, false);
        }

        private bool UseBrowserDownload(Doc d)
        {
            if (d == null || string.IsNullOrEmpty(d.Site)) return false;
            return settings.DocDownloadMethod(d.Id) == "browser";
        }

        private void DownloadDocument(string id, bool update)
        {
            if (dlId != null) return;

            Doc d = FindDoc(id);
            if (d == null) return;
            if (!update && File.Exists(Path.Combine(root, id, "index.html"))) return;

            // per-document setting. The Chinese hosts default to the browser
            // because their crawler always runs into the browser check; any
            // other document can be switched to the browser in Settings too.
            if (UseBrowserDownload(d))
            {
                Log("doc: " + id + " download start (browser direct " + d.Site + ")");
                DownloadWithBrowser(id);
                return;
            }

            // both documents are the same Sphinx build on different hosts, so
            // the detached crawler handles both; only when the Chinese host
            // answers with its browser check does the reader take over
            try
            {
                if (!Directory.Exists(DocStateDir)) Directory.CreateDirectory(DocStateDir);
                DeleteDownloadState(id);
            }
            catch (Exception) { }

            try
            {
                string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                             "updater", "download_doc.py");
                ProcessStartInfo psi = new ProcessStartInfo(PythonExe,
                    "\"" + script + "\" \"" + root + "\" \"" + id + "\" \"" +
                    DocStateDir + "\" \"" + PythonExe + "\"");
                psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = false;
                psi.RedirectStandardError = false;

                Process p = Process.Start(psi);
                Log("doc: download start " + id + " pid=" + (p != null ? p.Id : 0));
                if (p != null) File.WriteAllText(PidPath(id), p.Id.ToString());
            }
            catch (Exception ex)
            {
                Log("doc: download start failed: " + ex.Message);
                return;
            }

            BeginWatch(id, update);
        }

        private void BeginWatch(string id)
        {
            BeginWatch(id, false);
        }

        private void BeginWatch(string id, bool update)
        {
            dlId = id;
            dlUpdate = update;
            dlPaused = File.Exists(PausePath(id));
            dlLast = 0;
            dlTicks = 0;

            dlForm = new ProgressForm(
                Lang.T("正在下载文档", "正在下載文件",
                       "Downloading document", "文書をダウンロード中"),
                Lang.T("取消下载", "取消下載",
                       "Cancel download", "ダウンロードを中止"));
            dlForm.CenterOn(this);
            dlForm.CancelRequested += (s2, e2) => CancelDownload();
            dlForm.Show();

            dlTimer = new System.Windows.Forms.Timer();
            dlTimer.Interval = 500;
            dlTimer.Tick += (s2, e2) => PollDownload();
            dlTimer.Start();
            PollDownload();
        }

        private void PollDownload()
        {
            string id = dlId;
            if (id == null) return;
            dlTicks++;

            int pct = dlLast;
            string text = null;
            string state = null;
            try
            {
                if (File.Exists(ProgressPath(id)))
                {
                    string[] lines = File.ReadAllLines(ProgressPath(id));
                    if (lines.Length >= 2)
                    {
                        int.TryParse(lines[0].Trim(), out pct);
                        text = lines[1];
                        state = lines.Length >= 3 ? lines[2].Trim() : "running";
                    }
                }
            }
            catch (Exception) { }

            if (state == "done")
            {
                EndWatch(true);
                return;
            }
            if (state == "failed" || (state == null && dlTicks > 120))
            {
                // the host answered with its browser check: the crawler cannot
                // get past that, so fetch it here with a real browser
                bool needsBrowser = state == "failed" &&
                                    text != null && text.IndexOf("浏览器校验") >= 0;
                string why = text;
                EndWatch(false);
                if (needsBrowser) DownloadWithBrowser(id);
                else ShowDownloadFailed(why);
                return;
            }

            string label = dlPaused
                ? Lang.T("已暂停", "已暫停", "Paused", "一時停止中")
                : (text ?? Lang.T("正在下载…", "正在下載…",
                                  "Downloading…", "ダウンロード中…"));
            ShowDownloadProgress(dlForm, id, label, pct, dlPaused);
        }

        // a detached download that failed has no window of its own any more, so
        // say why instead of just making the progress window disappear
        private void ShowDownloadFailed(string why)
        {
            string text = (why ?? "").Trim();
            if (text.Length == 0)
                text = Lang.T("没能完成下载，请稍后重试……",
                              "沒能完成下載，請稍後重試……",
                              "The download did not finish. Please try again later…",
                              "ダウンロードを完了できませんでした。しばらくしてからもう一度お試しください……");
            if (text.Length > 600) text = text.Substring(0, 600) + "……";
            Log("doc: download failed: " + text);
            try
            {
                if (IsDisposed) return;
                using (InfoDialog dlg = new InfoDialog(
                    Lang.T("下载失败", "下載失敗", "Download failed", "ダウンロード失敗"),
                    text, null,
                    Lang.T("下载文档", "下載文件", "Download document", "文書をダウンロード")))
                    dlg.ShowDialog(this);
            }
            catch (Exception) { }
        }

        private void ReportProgress(string id, int pct, bool paused)
        {
            try
            {
                CoreWebView2 core = web.CoreWebView2;
                if (core == null) return;
                core.ExecuteScriptAsync("window.__rpDocProgress&&window.__rpDocProgress(" +
                    JsString(id) + "," + pct + "," + (paused ? "true" : "false") + ")");
            }
            catch (Exception) { }
        }

        // the popup and the ring must always show the same value: clamp here,
        // feed both, and never let a restarted crawler pull the UI backwards
        private void ShowDownloadProgress(ProgressForm form, string id,
                                          string text, int pct, bool paused)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            if (pct < dlLast) pct = dlLast;
            dlLast = pct;
            if (form != null) form.Report(text, pct);
            ReportProgress(id, pct, paused);
        }

        private void SetDownloadPaused(string id, bool paused)
        {
            if (dlId == null || id != dlId) return;
            try
            {
                if (paused)
                {
                    if (!File.Exists(PausePath(id))) File.WriteAllText(PausePath(id), "1");
                }
                else if (File.Exists(PausePath(id)))
                {
                    File.Delete(PausePath(id));
                }
            }
            catch (Exception) { }
            dlPaused = paused;
            string label = paused
                ? Lang.T("已暂停", "已暫停", "Paused", "一時停止中")
                : Lang.T("正在继续…", "正在繼續…",
                         "Resuming…", "再開中…");
            ShowDownloadProgress(dlForm, id, label, dlLast, paused);
        }

        private void CancelDownload()
        {
            string id = dlId;
            if (id == null) return;
            if (dlForm != null) dlForm.SetCancelling();
            // the Chinese download runs in-process: cancelling means the token
            if (zhCts != null)
            {
                try { zhCts.Cancel(); } catch (Exception) { }
                return;
            }
            try
            {
                if (File.Exists(PausePath(id))) File.Delete(PausePath(id));
                if (File.Exists(PidPath(id)))
                {
                    int pid;
                    if (int.TryParse(File.ReadAllText(PidPath(id)).Trim(), out pid))
                    {
                        ProcessStartInfo kill = new ProcessStartInfo("taskkill",
                            "/T /F /PID " + pid);
                        kill.UseShellExecute = false;
                        kill.CreateNoWindow = true;
                        kill.RedirectStandardOutput = true;
                        kill.RedirectStandardError = true;
                        using (Process k = Process.Start(kill)) k.WaitForExit(5000);
                    }
                }
            }
            catch (Exception) { }
            EndWatch(false);
        }

        private void EndWatch(bool ok)
        {
            string id = dlId;
            dlId = null;

            if (dlTimer != null)
            {
                try { dlTimer.Stop(); dlTimer.Dispose(); } catch (Exception) { }
                dlTimer = null;
            }
            if (dlForm != null)
            {
                try { dlForm.Close(); dlForm.Dispose(); } catch (Exception) { }
                dlForm = null;
            }

            try
            {
                if (File.Exists(ProgressPath(id))) File.Delete(ProgressPath(id));
                if (File.Exists(PausePath(id))) File.Delete(PausePath(id));
                if (File.Exists(PidPath(id))) File.Delete(PidPath(id));
            }
            catch (Exception) { }

            if (ok)
            {
                LoadDocs();
            }
            else if (!dlUpdate)
            {
                // an update that failed leaves the working copy alone
                try
                {
                    string folder = Path.Combine(root, id);
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
                catch (Exception) { }
            }

            try
            {
                web.CoreWebView2.ExecuteScriptAsync(
                    (ok ? "window.__rpDocReady&&window.__rpDocReady("
                        : "window.__rpDocFailed&&window.__rpDocFailed(") + JsString(id) + ")");
            }
            catch (Exception) { }
        }

        // tell the page how a document download ended
        private void NotifyDoc(string id, bool ok)
        {
            try
            {
                CoreWebView2 core = web.CoreWebView2;
                if (core == null) return;
                core.ExecuteScriptAsync(
                    (ok ? "window.__rpDocReady&&window.__rpDocReady("
                        : "window.__rpDocFailed&&window.__rpDocFailed(") + JsString(id) + ")");
            }
            catch (Exception) { }
        }

        // Used directly for doc.renpy.cn (it always trips its browser check) and
        // as a fallback for other sites that answer the crawler with a check.
        // Fetch it here in-process (SiteUpdater + a hidden WebView2) because only
        // a real browser gets past that check. Pausing still works: the pause
        // file is polled between pages.
        private async void DownloadWithBrowser(string id)
        {
            if (zhCts != null) return;

            Doc doc = FindDoc(id);
            if (doc == null || string.IsNullOrEmpty(doc.Site)) return;

            dlId = id;
            dlPaused = false;
            dlLast = 0;

            CancellationTokenSource cts = new CancellationTokenSource();
            zhCts = cts;
            ProgressForm progress = null;

            try
            {
                DeleteDownloadState(id);

                progress = new ProgressForm(
                    Lang.T("正在下载文档", "正在下載文件",
                           "Downloading document", "文書をダウンロード中"),
                    Lang.T("取消下载", "取消下載",
                           "Cancel download", "ダウンロードを中止"));
                progress.CenterOn(this);
                ProgressForm pf = progress;
                CancellationTokenSource source = cts;
                progress.CancelRequested += delegate
                {
                    pf.SetCancelling();
                    source.Cancel();
                };
                progress.Show(this);

                ProgressForm form = progress;
                Action<string, int> report = delegate(string text, int pct)
                {
                    string label = dlPaused
                        ? Lang.T("已暂停", "已暫停", "Paused", "一時停止中")
                        : text;
                    ShowDownloadProgress(form, id, label, pct, dlPaused);
                };

                Func<Task> tick = async delegate
                {
                    bool paused = false;
                    while (File.Exists(PausePath(id)))
                    {
                        if (!paused)
                        {
                            paused = true;
                            dlPaused = true;
                            ShowDownloadProgress(form, id,
                                Lang.T("已暂停", "已暫停", "Paused", "一時停止中"),
                                dlLast, true);
                        }
                        if (cts.IsCancellationRequested) break;
                        await Task.Delay(250);
                    }
                    if (paused)
                    {
                        dlPaused = false;
                        ShowDownloadProgress(form, id,
                            Lang.T("正在继续…", "正在繼續…",
                                   "Resuming…", "再開中…"), dlLast, false);
                    }
                };

                SiteUpdater updater = new SiteUpdater(
                    AppDomain.CurrentDomain.BaseDirectory, id, doc.Site, report, cts.Token,
                    form.SetFinalizing, tick);
                await updater.RunAsync();

                LoadDocs();
                WriteDocsJson();
                NotifyDoc(id, true);
                Log("doc: " + id + " download done (browser)");
            }
            catch (OperationCanceledException)
            {
                Log("doc: " + id + " download cancelled");
                NotifyDoc(id, false);
            }
            catch (Exception ex)
            {
                Log("doc: " + id + " download failed: " + ex.Message);
                NotifyDoc(id, false);
                MessageBox.Show(this,
                    Lang.T("下载失败。", "下載失敗。", "Download failed.", "ダウンロードに失敗しました。") +
                    "\n\n" + ex.Message,
                    Lang.T("下载失败", "下載失敗", "Download failed", "ダウンロード失敗"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                zhCts = null;
                dlId = null;
                dlPaused = false;
                dlLast = 0;
                DeleteDownloadState(id);
                if (progress != null)
                {
                    try { progress.Close(); progress.Dispose(); } catch (Exception) { }
                }
                cts.Dispose();
            }
        }

        // Switching documents swaps the whole tab set: the two documents keep
        // their tabs, favourites and search history apart.
        private void SwitchDocument(string id)
        {
            Doc target = FindDoc(id);
            if (target == null) return;
            // a document that has not been downloaded has no pages to show
            if (!File.Exists(Path.Combine(root, target.Id, "index.html"))) return;

            if (target.Id == CurrentDocId)
            {
                // the document already in use: nothing to swap. Coming back to
                // it from the picker (deleted, then downloaded again) restores
                // the tabs that were saved when it was deleted.
                if (IsPickerUrl(Current() != null ? Current().Url : "") && LoadTabs())
                {
                    tabs.Invalidate();
                    Navigate(tabs.Tabs[tabs.Active]);
                    return;
                }
                TabInfo tab = Current();
                if (tab == null) AddTab(HomeUrl);
                else Navigate(tab, HomeUrl);
                return;
            }

            // switching is immediate: no prompt, the per-document tab set and
            // favourites come back on their own when switching back
            SaveTabs();                       // keep the set we are leaving
            CurrentDocId = target.Id;
            try { File.WriteAllText(DocFile, CurrentDocId); } catch (Exception) { }

            reopen.Clear();
            tabs.Tabs.Clear();
            tabs.Active = -1;
            if (!LoadTabs())
            {
                TabInfo home = new TabInfo();
                home.Url = HomeUrl;
                home.Title = HomeTitle();
                tabs.Tabs.Add(home);
                tabs.Active = 0;
            }
            tabs.Invalidate();
            Navigate(tabs.Tabs[tabs.Active]);
        }

        // "关闭后清除所有标签页": drop every document's tab list; doc.txt stays,
        // so the next start opens that document's home page (LoadStartupTabs
        // falls back to the home tab when no list exists).
        private void ClearSavedTabs()
        {
            try
            {
                if (!Directory.Exists(TabsDir)) return;
                foreach (string path in Directory.GetFiles(TabsDir, "tabs-*.txt"))
                    File.Delete(path);
                Log("tabs: cleared on exit");
            }
            catch (Exception ex)
            {
                Log("tabs: clear failed " + ex.Message);
            }
        }

        private void SaveTabs()
        {
            // the one-tab picker page is never a document's tab set: do not
            // write it back, or a deleted/re-downloaded document would get a
            // stale picker tab instead of opening its home page
            if (tabs.Tabs.Count > 0 && IsPickerUrl(tabs.Tabs[0].Url))
                return;

            try
            {
                if (tabs.Tabs.Count == 0) return;

                string dir = Path.GetDirectoryName(TabsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append(tabs.Active).Append('\n');
                for (int i = 0; i < tabs.Tabs.Count; i++)
                {
                    TabInfo t = tabs.Tabs[i];
                    sb.Append(t.Url).Append('\t')
                      .Append((t.Title ?? "").Replace('\t', ' ')).Append('\t')
                      .Append((int)t.Y).Append('\t')
                      .Append(t.YUrl ?? "").Append('\n');
                }
                File.WriteAllText(TabsFile, sb.ToString());
            }
            catch (Exception) { }
        }

        private bool LoadTabs()
        {
            try
            {
                if (!File.Exists(TabsFile)) return false;

                string[] lines = File.ReadAllLines(TabsFile);
                if (lines.Length < 2) return false;

                int act;
                if (!int.TryParse(lines[0].Trim(), out act)) act = 0;

                System.Collections.Generic.List<TabInfo> list =
                    new System.Collections.Generic.List<TabInfo>();
                for (int i = 1; i < lines.Length && list.Count < 16; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length < 2 || parts[0].Length == 0) continue;

                    TabInfo t = new TabInfo();
                    t.Url = parts[0];
                    t.Title = parts[1];
                    t.History.Add(parts[0]);
                    double y = 0;
                    if (parts.Length > 2) double.TryParse(parts[2], out y);
                    t.Y = y;
                    if (parts.Length > 3) t.YUrl = parts[3];
                    list.Add(t);
                }
                if (list.Count == 0) return false;

                tabs.Tabs.Clear();
                tabs.Tabs.AddRange(list);
                if (act < 0 || act >= tabs.Tabs.Count) act = 0;
                tabs.Active = act;
                return true;
            }
            catch (Exception) { return false; }
        }

        // Ctrl+T / Ctrl+W / Ctrl+Tab / Ctrl+1..9: the web view owns the keyboard,
        // so the message filter catches them before the page sees them
        internal bool HandleTabKey(int vk)
        {
            if (pickerMode) return false;
            if ((ModifierKeys & Keys.Control) != Keys.Control) return false;
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;

            if (vk == 0x54) { AddTab(HomeUrl); return true; }        // T
            if (vk == 0x57) { CloseTab(tabs.Active); return true; }  // W
            if (vk == 0x09) { CycleTab(shift ? -1 : 1); return true; }  // Tab

            if (vk >= 0x31 && vk <= 0x39)                            // 1..9
            {
                int idx = vk == 0x39 ? tabs.Tabs.Count - 1 : vk - 0x31;
                if (idx >= 0 && idx < tabs.Tabs.Count) ActivateTab(idx);
                return true;
            }
            return false;
        }

        // Restore the last window rectangle; fall back to centring on the
        // primary screen (the OS default may land on a secondary monitor).
        private void ApplySavedGeometry()
        {
            Rectangle saved;
            int state;

            if (TryLoadGeometry(out saved, out state))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = saved;
                if (state == 1) WindowState = FormWindowState.Maximized;
                return;
            }

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min(1280, wa.Width);
            int h = Math.Min(860, wa.Height);
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
        }

        private static bool TryLoadGeometry(out Rectangle rect, out int state)
        {
            rect = Rectangle.Empty;
            state = 0;
            try
            {
                if (!File.Exists(StateFile)) return false;

                string[] p = File.ReadAllText(StateFile).Trim().Split(',');
                if (p.Length < 5) return false;

                int left, top, width, height;
                if (!int.TryParse(p[0], out left) || !int.TryParse(p[1], out top) ||
                    !int.TryParse(p[2], out width) || !int.TryParse(p[3], out height) ||
                    !int.TryParse(p[4], out state))
                    return false;

                if (width < 400 || height < 300) return false;

                rect = new Rectangle(left, top, width, height);
                // the monitor may have been unplugged since last time
                return VisibleOnAnyScreen(rect);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool VisibleOnAnyScreen(Rectangle r)
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle hit = Rectangle.Intersect(r, s.WorkingArea);
                if (hit.Width >= 120 && hit.Height >= 80) return true;
            }
            return false;
        }

        private void SaveGeometry()
        {
            try
            {
                int state = WindowState == FormWindowState.Maximized ? 1 : 0;
                Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (r.Width < 200 || r.Height < 150) return;

                string dir = Path.GetDirectoryName(StateFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(StateFile,
                    r.Left + "," + r.Top + "," + r.Width + "," + r.Height + "," + state);
            }
            catch (Exception)
            {
                // never block closing because of a failed write
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // read the scroll offset first, otherwise the tab that is open
            // right now would forget where the reader was
            if (!closing)
            {
                e.Cancel = true;
                closing = true;
                CloseAndSave();
                return;
            }

            SaveGeometry();
            SaveTabs();
            if (settings != null && settings.ClearTabsOnExit) ClearSavedTabs();
            base.OnFormClosing(e);
        }

        private async void CloseAndSave()
        {
            try
            {
                TabInfo cur = Current();
                if (cur != null) await SavePosition(cur);
            }
            catch (Exception) { }

            SaveTabs();
            Close();
        }

        // ----------------------------------------------------------- update
        private async void StartUpdate()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            ProgressForm progress = null;
            bool exitAfter = false;

            try
            {
                Log("update: start");
                // release the mirror so its folder can be swapped
                if (web.CoreWebView2 != null) web.CoreWebView2.Navigate("about:blank");
                await Task.Delay(300);

                progress = new ProgressForm();
                progress.CenterOn(this);

                ProgressForm pf = progress;
                CancellationTokenSource source = cts;
                progress.CancelRequested += (s, e) =>
                {
                    Log("update: cancel requested");
                    pf.SetCancelling();
                    source.Cancel();
                };

                // hide the reader: the update window is all that stays visible
                this.Hide();
                progress.Show();

                SiteUpdater updater = new SiteUpdater(
                    AppDomain.CurrentDomain.BaseDirectory, progress.Report,
                    cts.Token, progress.SetFinalizing);
                await updater.RunAsync();
                Log("update: done");

                // the fresh mirror changed docs.json: refresh the catalogue so
                // the switcher knows what is on disk now
                LoadDocs();
                WriteDocsJson();

                bool startNow = await progress.AskRestartAsync();
                Log("update: restart=" + startNow);

                if (startNow)
                {
                    progress.Close();
                    progress.Dispose();
                    progress = null;
                    ShowReader();
                }
                else
                {
                    exitAfter = true;
                    Application.Exit();
                }
            }
            catch (OperationCanceledException)
            {
                // staging copy already deleted; the live mirror was untouched
                Log("update: cancelled");
            }
            catch (Exception ex)
            {
                Log("update: failed " + ex.Message);
                ShowReader();
                MessageBox.Show(this,
                    Lang.T("更新失败。", "更新失敗。", "Update failed.", "アップデートに失敗しました。") +
                    "\n\n" + ex.Message,
                    Lang.T("更新失败", "更新失敗", "Update failed", "アップデート失敗"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (!exitAfter)
                {
                    if (progress != null)
                    {
                        progress.Close();
                        progress.Dispose();
                    }
                    if (!Visible) ShowReader();
                }
                cts.Dispose();
            }
        }

        // ---------------------------------------------------- english update
        private static string VersionPrefix(string v)
        {
            Match m = Regex.Match(v ?? "", @"^\d+\.\d+\.\d+");
            return m.Success ? m.Value : (v ?? "").Trim();
        }

        private async void CheckDocUpdate(string id, bool manual)
        {
            Doc d = FindDoc(id);
            if (d == null) return;
            if (!File.Exists(Path.Combine(root, id, "index.html")))
            {
                if (manual) DownloadDocument(id, false);
                return;
            }

            if (checkBusy) return;
            checkBusy = true;

            string site = d.Site;
            BusyForm busy = null;
            try
            {
                if (manual && !string.IsNullOrEmpty(site))
                {
                    busy = new BusyForm(Lang.T("正在检查更新…", "正在檢查更新…",
                                               "Checking for updates…",
                                               "アップデートを確認中…"), true);
                    busy.CenterOn(this);
                    busy.Show(this);
                }

                RemoteInfo remote = string.IsNullOrEmpty(site)
                    ? new RemoteInfo()
                    : await FetchSiteInfoAsync(site,
                        delegate(string text, int pct)
                        {
                            if (busy != null) busy.Report(text, pct);
                        });

                if (busy != null)
                    busy.Report(Lang.T("正在比较版本…", "正在比較版本…",
                                       "Comparing versions…",
                                       "バージョンを比較中…"), 95);
                CloseBusy(ref busy);

                string local = ReadDocVersion(id);
                DateTime? localBuild = ReadLocalBuild(id);

                Log("doc(" + id + "): check local=" + local + " build=" + Stamp(localBuild) +
                    " remote=" + remote.Version + " build=" + Stamp(remote.Build));

                if (remote.Version.Length == 0 && !remote.Build.HasValue)
                {
                    if (!manual) return;
                    string host = "";
                    try { host = new Uri(site).Authority; }
                    catch (Exception) { }
                    MessageBox.Show(this,
                        string.Format(Lang.T("无法连接 {0}，请稍后再试。",
                                             "無法連線 {0}，請稍後再試。",
                                             "Cannot connect to {0}. Please try again later.",
                                             "{0} に接続できません。しばらくしてからもう一度お試しください。"),
                                      host.Length > 0 ? host
                                          : Lang.T("文档站", "文件站",
                                                   "the docs site", "ドキュメントサイト")),
                        Lang.T("无法检查更新", "無法檢查更新",
                               "Can't check for updates", "アップデートを確認できません"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string lv = VersionPrefix(local);
                string rv = VersionPrefix(remote.Version);
                // both signals again: a new version number or a newer build
                bool versionNewer = lv.Length > 0 && rv.Length > 0 && rv != lv &&
                                    LooksNewer(rv, lv);
                bool buildNewer = localBuild.HasValue && remote.Build.HasValue &&
                                  remote.Build.Value > localBuild.Value;
                string details = UpdateDetails(lv, localBuild, rv, remote.Build);

                if (!versionNewer && !buildNewer)
                {
                    if (!manual) return;
                    using (InfoDialog dlg = new InfoDialog(
                        Lang.T("当前已是最新版本", "目前為最新版本",
                               "Up to date", "最新バージョンです"),
                        details,
                        Lang.T("校验完整性", "校驗完整性",
                               "Verify files", "ファイルを検証")))
                    {
                        if (dlg.ShowDialog(this) == DialogResult.Retry) StartIntegrityCheck();
                    }
                    return;
                }

                using (ConfirmDialog dlg = new ConfirmDialog(
                    Lang.T("更新文档", "更新文件", "Update document", "文書を更新"),
                    details + "\r\n\r\n" + Lang.T("是否重新下载？", "是否重新下載？",
                                                  "Download it again?", "もう一度ダウンロードしますか？"),
                    Lang.T("更新", "更新", "Update", "アップデート"),
                    Lang.T("取消", "取消", "Cancel", "キャンセル")))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                }
                DownloadDocument(id, true);
            }
            catch (Exception ex)
            {
                Log("doc(" + id + "): check exception " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                checkBusy = false;
                CloseBusy(ref busy);
            }
        }

        // startup check: only the document that is actually open, and only when
        // the settings sheet has it switched on. It uses the same progress
        // window and result prompts as the page's "check for updates" button.
        private void AutoCheckDocumentUpdates()
        {
            if (settings == null || !settings.DocUpdateOnStart) return;
            string id = CurrentDocId;
            if (!File.Exists(Path.Combine(root, id, "index.html"))) return;
            if (settings.DocUpdateDaily)
            {
                if (CheckedToday())
                {
                    Log("check: startup skipped (already checked today)");
                    return;
                }
                MarkCheckedToday();
            }
            Log("check: startup (" + id + ")");
            if (id == "zh-CN") CheckForUpdates(true);
            else CheckDocUpdate(id, true);
        }

        // App update first, then the document check: when both have updates,
        // the app-update sheet is the one that appears first. A quiet result
        // (no update / no release) adds no window at all.
        private async Task StartupChecks()
        {
            if (settings != null && settings.AppUpdateOnStart)
            {
                if (settings.AppUpdateDaily && AppCheckedToday())
                {
                    Log("app-update: startup skipped (already checked today)");
                }
                else
                {
                    try
                    {
                        await Task.Delay(800);
                        if (!IsDisposed)
                        {
                            Log("app-update: startup");
                            bool checkedOk = await AppUpdateUI.CheckAndShowAsync(
                                this, false, settings.AppUpdateUrl);
                            if (AppUpdateInstaller.RestartPending) return;
                            // a failed lookup (offline / no release yet) must not
                            // consume today's one attempt: retry on the next start
                            if (checkedOk && settings.AppUpdateDaily)
                                MarkAppCheckedToday();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("app-update: " + ex.Message);
                    }
                }
            }

            if (!IsDisposed) AutoCheckDocumentUpdates();
        }

        // "每天最多检查一次" for the app itself: its own date marker, so the
        // document check and the app check do not consume each other's day.
        private static readonly string AppCheckFile = Path.Combine(
            TabsDir, "app-check.txt");

        private static bool AppCheckedToday()
        {
            try
            {
                if (!File.Exists(AppCheckFile)) return false;
                return File.ReadAllText(AppCheckFile).Trim() ==
                       DateTime.Now.ToString("yyyy-MM-dd");
            }
            catch (Exception) { return false; }
        }

        private static void MarkAppCheckedToday()
        {
            try
            {
                File.WriteAllText(AppCheckFile, DateTime.Now.ToString("yyyy-MM-dd"));
            }
            catch (Exception) { }
        }

        // "每天最多检查一次": the date of the last startup check, local time
        private static readonly string DocCheckFile = Path.Combine(
            TabsDir, "doc-check.txt");

        private static bool CheckedToday()
        {
            try
            {
                if (!File.Exists(DocCheckFile)) return false;
                return File.ReadAllText(DocCheckFile).Trim() ==
                       DateTime.Now.ToString("yyyy-MM-dd");
            }
            catch (Exception) { return false; }
        }

        private static void MarkCheckedToday()
        {
            try
            {
                File.WriteAllText(DocCheckFile, DateTime.Now.ToString("yyyy-MM-dd"));
            }
            catch (Exception) { }
        }

        private static bool IsPickerUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string u = url;
            int cut = u.IndexOfAny(new char[] { '?', '#' });
            if (cut >= 0) u = u.Substring(0, cut);
            return u.EndsWith("/picker.html", StringComparison.OrdinalIgnoreCase);
        }

        private void SetPickerMode(bool on)
        {
            if (pickerMode == on) return;
            pickerMode = on;
            tabs.Visible = !on;
        }

        private void ShowReader()
        {
            if (IsDisposed) return;
            Enabled = true;
            if (!Visible) Show();
            Activate();
            try
            {
                if (web.CoreWebView2 != null)
                    web.CoreWebView2.Navigate("https://" + Host + "/zh-CN/index.html?t=" +
                                              DateTime.Now.Ticks);
            }
            catch (Exception) { }
        }
        // The docs site is behind a WAF that blocks HTML; every version check
        // now reads the site named in docs.json, this constant is the built-in
        // document's root.
        private const string DocsRoot = "https://doc.renpy.cn/zh-CN/";

        private static readonly string IgnoreFile =
            Path.Combine(AppPaths.DataDir, "update-ignore.txt");

        private static void Log(string message)
        {
            Logs.Write(message);
        }

        private async void CheckForUpdates(bool manual)
        {
            if (manual && checkBusy) return;
            if (manual) checkBusy = true;
            BusyForm busy = null;
            try
            {
                Log("check: start" + (manual ? " (manual)" : ""));

                if (manual)
                {
                    busy = new BusyForm(Lang.T("正在检查更新…", "正在檢查更新…",
                                               "Checking for updates…",
                                               "アップデートを確認中…"), true);
                    busy.CenterOn(this);
                    busy.Show(this);
                }
                else
                {
                    await Task.Delay(800);
                }

                // Two signals, either one means "there is something newer":
                //  · version — the number the site shows on its own front page
                //    (`8.5.4 说明文档首页`, the same line build.py reads for
                //    version.txt). Its `_static/documentation_options.js` is
                //    years stale (still 7.8.5 from 2024), so that file is only
                //    the last resort for a site that offers nothing else.
                //  · build time — searchindex.js is rewritten by Sphinx on every
                //    rebuild, and the server reports when in Last-Modified.
                string localVersion = ReadDocVersion("zh-CN");
                DateTime? localBuild = ReadLocalBuild("zh-CN");

                Action<string, int> progress = null;
                if (busy != null)
                {
                    progress = delegate(string text, int pct)
                    {
                        if (busy != null) busy.Report(text, pct);
                    };
                }

                RemoteInfo remote = await FetchSiteInfoAsync(DocsRoot, progress);
                string remoteVersion = remote.Version;

                Log("check: local version=" + localVersion + " build=" + Stamp(localBuild));
                Log("check: remote version=" + remoteVersion +
                    " build=" + Stamp(remote.Build));

                if (busy != null)
                    busy.Report(Lang.T("正在比较版本…", "正在比較版本…",
                                       "Comparing versions…",
                                       "バージョンを比較中…"), 95);
                CloseBusy(ref busy);

                bool hasLocal = localVersion.Length > 0 || localBuild.HasValue;
                bool hasRemote = remoteVersion.Length > 0 || remote.Build.HasValue;
                if (!hasLocal)
                {
                    Log("check: no local version or build stamp");
                    if (manual) ShowCheckMessage(
                        Lang.T("无法检查更新", "無法檢查更新",
                               "Can't check for updates", "アップデートを確認できません"),
                        Lang.T("版本文件异常，请重新安装。", "版本檔案異常，請重新安裝。",
                               "The version file is damaged. Please reinstall.",
                               "バージョンファイルが異常です。再インストールしてください。"));
                    return;
                }
                if (!hasRemote)
                {
                    Log("check: nothing came back from the site");
                    if (manual) ShowCheckMessage(
                        Lang.T("无法检查更新", "無法檢查更新",
                               "Can't check for updates", "アップデートを確認できません"),
                        Lang.T("连接错误，请稍后重试……", "連線錯誤，請稍後重試……",
                               "Connection error. Please try again later…",
                               "接続エラーです。しばらくしてからもう一度お試しください……"));
                    return;
                }

                bool versionNewer = remoteVersion.Length > 0 && localVersion.Length > 0 &&
                                    remoteVersion != localVersion &&
                                    LooksNewer(remoteVersion, localVersion);
                bool buildNewer = localBuild.HasValue && remote.Build.HasValue &&
                                  remote.Build.Value > localBuild.Value;
                bool versionOlder = remoteVersion.Length > 0 && localVersion.Length > 0 &&
                                    remoteVersion != localVersion &&
                                    !LooksNewer(remoteVersion, localVersion);
                bool buildOlder = localBuild.HasValue && remote.Build.HasValue &&
                                  remote.Build.Value < localBuild.Value;

                string details = UpdateDetails(localVersion, localBuild,
                                               remoteVersion, remote.Build);
                // what "本次更新不再提示" remembers: the build, else the version
                string buildKey = remote.Build.HasValue
                    ? remote.Build.Value.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    : remoteVersion;

                if (versionNewer || buildNewer)
                {
                    if (!manual && ReadIgnoredVersion() == buildKey)
                    {
                        Log("check: ignored");
                        return;
                    }

                    Log("check: prompting (versionNewer=" + versionNewer +
                        " buildNewer=" + buildNewer + ")");
                    BeginInvoke((Action)(() =>
                    {
                        using (UpdateDialog dlg = new UpdateDialog(details))
                        {
                            DialogResult result = dlg.ShowDialog(this);
                            Log("check: dialog result=" + result + " ignore=" + dlg.IgnoreThisVersion);

                            if (dlg.IgnoreThisVersion) RememberIgnoredVersion(buildKey);
                            if (result == DialogResult.OK) StartUpdate();
                        }
                    }));
                    return;
                }

                Log("check: up to date");
                if (manual)
                {
                    bool ahead = versionOlder || buildOlder;
                    ShowCheckMessage(
                        ahead
                            ? Lang.T("无需更新", "無需更新", "No update needed", "アップデートは不要です")
                            : Lang.T("当前已是最新版本", "目前為最新版本",
                                     "Up to date", "最新バージョンです"),
                        details,
                        Lang.T("校验完整性", "校驗完整性", "Verify files", "ファイルを検証"));
                }
            }
            catch (Exception ex)
            {
                // a failed check must never disturb normal use
                Log("check: exception " + ex.GetType().Name + ": " + ex.Message);
                CloseBusy(ref busy);
                if (manual) ShowCheckMessage(
                    Lang.T("检查更新时出错", "檢查更新時發生錯誤",
                           "Error while checking for updates",
                           "アップデート確認中にエラーが発生しました"), ex.Message);
            }
            finally
            {
                if (manual) checkBusy = false;
                CloseBusy(ref busy);
            }
        }

        // what every page needs to know about the current settings, injected
        // before the page's own scripts run
        private string PageSettingsScript()
        {
            return "window.__rpUiLang=" + JsString(Lang.Id) + ";" +
                   "window.__rpSearchHistoryMax=" + settings.SearchHistoryMax + ";";
        }

        // interface language or the search-history size changed: make the next
        // document use the new values and reload the page on screen
        private async void ReloadPageWithSettings(int trimHistoryTo)
        {
            try
            {
                CoreWebView2 core = web.CoreWebView2;
                if (core == null) return;
                if (trimHistoryTo >= 0) await TrimSearchHistoryAsync(trimHistoryTo);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(PageSettingsScript());
                core.Reload();
            }
            catch (Exception) { }
        }

        // shrink the stored history of every document to the new limit (0 clears it)
        private async Task TrimSearchHistoryAsync(int max)
        {
            try
            {
                CoreWebView2 core = web.CoreWebView2;
                if (core == null) return;
                string js =
                    "(function(m){try{for(var i=localStorage.length-1;i>=0;i--){" +
                    "var k=localStorage.key(i);" +
                    "if(k&&k.indexOf('rp-search-history')===0){" +
                    "var v=JSON.parse(localStorage.getItem(k)||'[]');" +
                    "if(Object.prototype.toString.call(v)==='[object Array]'&&v.length>m){" +
                    "if(m<=0){localStorage.removeItem(k);}" +
                    "else{localStorage.setItem(k,JSON.stringify(v.slice(0,m)));}" +
                    "}}}}catch(e){}})(" + max + ")";
                await core.ExecuteScriptAsync(js);
            }
            catch (Exception) { }
        }

        private void CloseBusy(ref BusyForm busy)
        {
            if (busy == null) return;
            try
            {
                busy.Close();
                busy.Dispose();
            }
            catch (Exception) { }
            busy = null;
        }

        // quotes a value for ExecuteScriptAsync without pulling in a JSON library
        private static string JsString(string s)
        {
            if (s == null) s = "";
            s = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                 .Replace("\r", "").Replace("\n", "\\n");
            return "\"" + s + "\"";
        }

        // tells the page whether back / forward are possible right now, so the
        // buttons can be greyed out instead of doing nothing
        private void PushNavState()
        {
            try
            {
                CoreWebView2 core = web.CoreWebView2;
                if (core != null) PushNavState(core);
            }
            catch (Exception) { }
        }

        private void PushNavState(CoreWebView2 core)
        {
            try
            {
                TabInfo tab = Current();
                bool back = tab != null &&
                    (tab.History.Count > 1 || (tab.Opener != null && tabs.Tabs.Contains(tab.Opener)));
                bool fwd = (tab != null && tab.Forward.Count > 0) || reopen.Count > 0;

                string js = "window.__rpNav&&window.__rpNav(" +
                            (back ? "1" : "0") + "," +
                            (fwd ? "1" : "0") + ")";
                core.ExecuteScriptAsync(js);
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------- integrity
        // "check the files": walks the downloaded document and proves every page
        // and (above all) every image can actually be shown. A mirror that was
        // filled by the site's challenge page looks complete in a listing, so
        // the bytes are checked, not just the names.
        private bool integrityBusy;

        // one update check at a time: the "check" button lives in the page and
        // a second click while the progress window is up would stack dialogs
        private bool checkBusy;

        private void StartIntegrityCheck()
        {
            if (integrityBusy) return;
            Doc d = CurrentDoc;
            if (d == null) return;
            integrityBusy = true;

            string dir = Path.Combine(root, d.Id);
            string name = d.Name;
            string marker = d.Id == "zh-CN" ? "中文" : null;

            BusyForm busy = new BusyForm(
                Lang.T("正在校验文件完整性…", "正在校驗檔案完整性…",
                       "Verifying files…", "ファイルを検証中…"), true);
            busy.CenterOn(this);
            busy.Show(this);

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                IntegrityReport report;
                try
                {
                    report = Integrity.Check(dir, name, marker,
                        delegate(string rel, int pct)
                        {
                            busy.Report(string.Format(Lang.T("正在校验 {0}%…", "正在校驗 {0}%…",
                                                              "Verifying {0}%…", "検証中 {0}%…"), pct), pct);
                        });
                    // leave the bar visibly full for a moment before the result
                    // window takes over
                    busy.Report(Lang.T("校验完成", "校驗完成",
                                       "Verification finished", "検証完了"), 100);
                    System.Threading.Thread.Sleep(300);
                }
                catch (Exception ex)
                {
                    report = new IntegrityReport();
                    report.DocName = name;
                    report.Bad(dir, "读取失败",
                               Lang.T("校验出错：", "校驗出錯：",
                                      "Verification error: ", "検証エラー：") + ex.Message);
                }

                try
                {
                    BeginInvoke((Action)(() => FinishIntegrity(report, busy)));
                }
                catch (Exception) { }
            });
        }

        private void FinishIntegrity(IntegrityReport report, BusyForm busy)
        {
            integrityBusy = false;
            if (busy != null)
            {
                try { busy.Close(); busy.Dispose(); } catch (Exception) { }
            }
            if (IsDisposed) return;

            if (report == null)
            {
                using (InfoDialog bad = new InfoDialog(
                    Lang.T("校验失败", "校驗失敗", "Verification failed", "検証失敗"),
                    Lang.T("请重试……", "請重試……",
                           "Please try again…", "もう一度お試しください……"),
                    null,
                    Lang.T("文件校验", "檔案校驗", "File check", "ファイル検証")))
                    bad.ShowDialog(this);
                return;
            }

            if (report.Problems.Count == 0)
            {
                using (InfoDialog ok = new InfoDialog(
                    Lang.T("文件完整", "檔案完整", "Files are intact", "ファイルは正常です"),
                    string.Format(Lang.T("共 {0} 个文件、{1} 张图片\r\n内容无误，文件无缺",
                                         "共 {0} 個檔案、{1} 張圖片\r\n內容無誤，檔案無缺",
                                         "{0} files, {1} images\r\nNo problems found.",
                                         "{0} ファイル、{1} 画像\r\n問題は見つかりませんでした。"),
                                  report.Files, report.Images),
                    null,
                    Lang.T("文件校验", "檔案校驗", "File check", "ファイル検証")))
                    ok.ShowDialog(this);
                return;
            }

            // only the counts and the kinds: a list of file paths is noise for
            // the reader, "2 张图片坏了、1 个页面不对" is the news
            using (InfoDialog dlg = new InfoDialog(
                Lang.T("发现文件损坏", "發現檔案損壞",
                       "Damaged files found", "破損ファイルが見つかりました"),
                IntegritySummary(report),
                Lang.T("重新下载", "重新下載", "Download again", "再ダウンロード"),
                Lang.T("文件校验", "檔案校驗", "File check", "ファイル検証")))
            {
                if (dlg.ShowDialog(this) == DialogResult.Retry)
                    DownloadDocument(CurrentDocId, true);
            }
        }

        // "「文档」发现 5 处问题（共检查 210 个文件）：· 图片损坏：2 处 …"
        private static string KindName(string kind)
        {
            switch (kind)
            {
                case "图片损坏":
                    return Lang.T("图片损坏", "圖片損壞", "Damaged image", "画像破損");
                case "文件缺失":
                    return Lang.T("文件缺失", "檔案缺失", "Missing file", "ファイル欠落");
                case "页面异常":
                    return Lang.T("页面异常", "頁面異常", "Page problem", "ページ異常");
                case "资源异常":
                    return Lang.T("资源异常", "資源異常", "Asset problem", "リソース異常");
                case "空文件":
                    return Lang.T("空文件", "空檔案", "Empty file", "空ファイル");
                case "内容不符":
                    return Lang.T("内容不符", "內容不符", "Content mismatch", "内容不一致");
                case "读取失败":
                    return Lang.T("读取失败", "讀取失敗", "Read failed", "読み取り失敗");
                case "目录异常":
                    return Lang.T("目录异常", "目錄異常", "Folder problem", "フォルダ異常");
                case "其他":
                    return Lang.T("其他", "其他", "Other", "その他");
                default:
                    return kind;
            }
        }

        private static string IntegritySummary(IntegrityReport report)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(string.Format(Lang.T(
                "「{0}」发现 {1} 处问题（共检查 {2} 个文件）：\r\n\r\n",
                "「{0}」發現 {1} 處問題（共檢查 {2} 個檔案）：\r\n\r\n",
                "{0}: {1} problem(s) found ({2} files checked):\r\n\r\n",
                "「{0}」で {1} 件の問題（{2} ファイルを確認）：\r\n\r\n"),
                report.DocName, report.Problems.Count, report.Files));

            Dictionary<string, int> counts = report.Kinds();
            string[] order = new string[]
            {
                "图片损坏", "文件缺失", "页面异常", "资源异常",
                "空文件", "内容不符", "读取失败", "目录异常"
            };
            for (int i = 0; i < order.Length; i++)
            {
                int n;
                if (!counts.TryGetValue(order[i], out n)) continue;
                sb.Append(string.Format(Lang.T("· {0}：{1} 处\r\n", "· {0}：{1} 處\r\n",
                                               "· {0}: {1}\r\n", "・{0}：{1} 件\r\n"),
                                        KindName(order[i]), n));
                counts.Remove(order[i]);
            }
            foreach (KeyValuePair<string, int> rest in counts)
                sb.Append(string.Format(Lang.T("· {0}：{1} 处\r\n", "· {0}：{1} 處\r\n",
                                               "· {0}: {1}\r\n", "・{0}：{1} 件\r\n"),
                                        KindName(rest.Key), rest.Value));

            return sb.ToString();
        }

        private void ShowCheckMessage(string heading, string text)
        {
            ShowCheckMessage(heading, text, null);
        }

        private void ShowCheckMessage(string heading, string text, string extraText)
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                DialogResult result;
                using (InfoDialog dlg = new InfoDialog(heading, text, extraText))
                    result = dlg.ShowDialog(this);
                if (result == DialogResult.Retry) StartIntegrityCheck();
            }));
        }

        private static string ParseVersion(string js)
        {
            Match m = Regex.Match(js ?? "", @"VERSION\s*:\s*'([^']+)'");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ------------------------------------------------------ build time
        // "Which build is this?" — build.txt is written by the crawler from the
        // site's own Last-Modified (fetch_en.py), and the same header answers on
        // the live site, so local and remote stamps are directly comparable.

        private DateTime? ReadLocalBuild(string id)
        {
            try
            {
                string path = Path.Combine(root, id, "build.txt");
                if (!File.Exists(path)) return null;
                return ParseStamp(File.ReadAllText(path).Trim());
            }
            catch (Exception) { return null; }
        }

        private static DateTime? ParseStamp(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            DateTime when;
            if (DateTime.TryParse(text,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.AdjustToUniversal |
                                  System.Globalization.DateTimeStyles.AssumeUniversal,
                                  out when))
                return when;
            return null;
        }

        private static string Stamp(DateTime? when)
        {
            return when.HasValue ? when.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                                 : Lang.T("未知", "未知", "unknown", "不明");
        }

        // Everything "检查更新" needs from the live site: the version the site
        // shows, and when its files were last rebuilt.
        private class RemoteInfo
        {
            public string Version = "";        // sidebar line, else options file
            public DateTime? Build;
        }

        private static HttpClient NewHttp(int seconds)
        {
            HttpClient http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(seconds);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return http;
        }

        // the two Chinese builds put the version in the sidebar, simplified and
        // traditional respectively
        private static string ParseSiteVersion(string html)
        {
            Match m = Regex.Match(html ?? "",
                @"(\d+\.\d+\.\d+)[^<]{0,12}(?:说明文档首页|說明文件首頁)");
            return m.Success ? m.Groups[1].Value : "";
        }

        // Version + build time of one live document. The sidebar line is the
        // best version source where it exists; every other language only has
        // the Sphinx options file, and that file is years stale on
        // doc.renpy.cn — hence the host check before trusting it.
        private static async Task<RemoteInfo> FetchSiteInfoAsync(string site,
                                                                  Action<string, int> report)
        {
            RemoteInfo info = new RemoteInfo();
            if (string.IsNullOrEmpty(site)) return info;
            string root = site.EndsWith("/") ? site : site + "/";

            if (report != null) report(Lang.T("正在连接文档站…", "正在連線文件站…",
                                              "Connecting to the docs site…",
                                              "ドキュメントサイトに接続中…"), 8);
            try
            {
                if (report != null) report(Lang.T("正在读取线上版本…", "正在讀取線上版本…",
                                                  "Reading the online version…",
                                                  "オンラインバージョンを読み取り中…"), 20);
                using (HttpClient http = NewHttp(8))
                using (HttpResponseMessage response = await http.GetAsync(root + "index.html"))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        string html = await response.Content.ReadAsStringAsync();
                        string version = ParseSiteVersion(html);
                        if (version.Length > 0)
                        {
                            info.Version = version;
                            if (response.Content.Headers.LastModified.HasValue)
                                info.Build = response.Content.Headers.LastModified.Value.UtcDateTime;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("check: index page failed: " + ex.Message);
            }

            if (!info.Build.HasValue)
            {
                if (report != null) report(Lang.T("正在获取构建时间…", "正在取得建置時間…",
                                                  "Getting the build time…",
                                                  "ビルド日時を取得中…"), 52);
                info.Build = await FetchBuildAsync(root);
            }

            if (info.Version.Length == 0 && site.IndexOf("doc.renpy.cn") < 0)
            {
                if (report != null) report(Lang.T("正在读取版本文件…", "正在讀取版本檔案…",
                                                  "Reading the version file…",
                                                  "バージョンファイルを読み取り中…"), 78);
                try
                {
                    using (HttpClient http = NewHttp(8))
                    {
                        string js = await http.GetStringAsync(
                            root + "_static/documentation_options.js");
                        info.Version = ParseVersion(js);
                    }
                }
                catch (Exception ex)
                {
                    Log("check: options file failed: " + ex.Message);
                }
            }

            if (report != null) report(Lang.T("正在比较版本…", "正在比較版本…",
                                              "Comparing versions…",
                                              "バージョンを比較中…"), 92);
            return info;
        }

        // searchindex.js is rewritten by Sphinx on every rebuild, so its
        // Last-Modified is the build time; index.html is the second choice.
        private static async Task<DateTime?> FetchBuildAsync(string root)
        {
            string[] urls = new string[] { root + "searchindex.js", root + "index.html" };
            foreach (string url in urls)
            {
                DateTime? stamp = await ProbeBuildAsync(url, HttpMethod.Head);
                if (stamp.HasValue) return stamp;
            }
            // an edge that answers HEAD without headers can still answer GET
            foreach (string url in urls)
            {
                DateTime? stamp = await ProbeBuildAsync(url, HttpMethod.Get);
                if (stamp.HasValue) return stamp;
            }
            return null;
        }

        private static async Task<DateTime?> ProbeBuildAsync(string url, HttpMethod method)
        {
            try
            {
                using (HttpClient http = NewHttp(6))
                using (HttpRequestMessage request = new HttpRequestMessage(method, url))
                using (HttpResponseMessage response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    if (response.Content != null &&
                        response.Content.Headers.LastModified.HasValue)
                        return response.Content.Headers.LastModified.Value.UtcDateTime;
                }
            }
            catch (Exception ex)
            {
                Log("check: build time " + method + " " + url + " failed: " + ex.Message);
            }
            return null;
        }

        private string ReadDocVersion(string id)
        {
            try
            {
                string path = Path.Combine(root, id, "version.txt");
                if (File.Exists(path))
                {
                    string v = File.ReadAllText(path).Trim();
                    if (v.Length > 0) return v;
                }

                // no version.txt (the English mirror never gets one, its page
                // has no "说明文档首页" line): the site's own options file is
                // accurate there, so read the version out of it
                string js = Path.Combine(root, id, "_static", "documentation_options.js");
                if (File.Exists(js)) return ParseVersion(File.ReadAllText(js));
            }
            catch (Exception) { }
            return "";
        }

        // 本地版本 / 本地构建 / 线上版本 / 线上构建 — only the lines we have
        private static string UpdateDetails(string localVersion, DateTime? localBuild,
                                            string remoteVersion, DateTime? remoteBuild)
        {
            List<string> lines = new List<string>();
            if (!string.IsNullOrEmpty(localVersion))
                lines.Add(Lang.T("本地版本：", "本地版本：", "Local version: ", "ローカルバージョン：") + localVersion);
            if (localBuild.HasValue)
                lines.Add(Lang.T("本地构建：", "本地建置：", "Local build: ", "ローカルビルド：") + Stamp(localBuild));
            if (!string.IsNullOrEmpty(remoteVersion))
                lines.Add(Lang.T("线上版本：", "線上版本：", "Online version: ", "オンラインバージョン：") + remoteVersion);
            if (remoteBuild.HasValue)
                lines.Add(Lang.T("线上构建：", "線上建置：", "Online build: ", "オンラインビルド：") + Stamp(remoteBuild));
            return string.Join("\r\n", lines.ToArray());
        }

        private static int[] VersionNumbers(string version)
        {
            Match m = Regex.Match(version, @"\d+(\.\d+)*");
            if (!m.Success) return null;

            string[] parts = m.Value.Split('.');
            int[] numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                if (!int.TryParse(parts[i], out n)) return null;
                numbers[i] = n;
            }
            return numbers;
        }

        private static bool LooksNewer(string remote, string local)
        {
            int[] a = VersionNumbers(remote);
            int[] b = VersionNumbers(local);
            if (a == null || b == null) return true;   // unknown phrasing: treat as changed

            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return true;   // same numbers, different build stamp
        }

        private static string ReadIgnoredVersion()
        {
            try
            {
                return File.Exists(IgnoreFile) ? File.ReadAllText(IgnoreFile).Trim() : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static void RememberIgnoredVersion(string version)
        {
            try
            {
                string dir = Path.GetDirectoryName(IgnoreFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(IgnoreFile, version);
            }
            catch (Exception)
            {
                // not being able to remember the choice is harmless
            }
        }

        // Only real web / mail links are handed to the shell; internal schemes
        // such as about: must stay inside WebView2.
        private static bool IsExternalLink(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            return uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        }

        private static void OpenExternal(string uri)
        {
            if (!IsExternalLink(uri)) return;
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // ignore malformed or blocked links
            }
        }
    }

    // The web view owns the keyboard, so Ctrl+T / Ctrl+W / Ctrl+Tab never reach
    // the form; a message filter sees them before the page does.
    internal class TabKeyFilter : IMessageFilter
    {
        private readonly MainForm form;

        public TabKeyFilter(MainForm f)
        {
            form = f;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x100 && m.Msg != 0x104) return false;   // KEYDOWN / SYSKEYDOWN
            if (form == null || form.IsDisposed) return false;
            // a modal dialog owns the keyboard while it is open
            if (Form.ActiveForm != form) return false;
            return form.HandleTabKey(m.WParam.ToInt32());
        }
    }

    // Delete confirmation, drawn like the update prompt: white sheet, big bold
    // title, big buttons. The page opens it and gets __rpDocDelete(id) back.
    public class ConfirmDialog : Form
    {
        public ConfirmDialog(string title, string text, string okText, string cancelText)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = Lang.UiFont(10F);
            ClientSize = new Size(660, 330);

            Label head = new Label();
            head.Text = title;
            head.Font = Lang.UiFont(18F, FontStyle.Bold);
            head.AutoSize = true;
            head.Location = new Point(34, 28);
            Controls.Add(head);

            Label body = new Label();
            body.Text = text;
            body.Font = Lang.UiFont(11.5F);
            body.AutoSize = true;
            body.MaximumSize = new Size(592, 0);
            body.Location = new Point(36, 88);
            Controls.Add(body);

            Button ok = new Button();
            ok.Text = okText;
            ok.Font = Lang.UiFont(11F);
            ok.Size = new Size(124, 44);
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(ClientSize.Width - 316, 250);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = cancelText;
            cancel.Font = Lang.UiFont(11F);
            cancel.Size = new Size(124, 44);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(ClientSize.Width - 172, 250);
            Controls.Add(cancel);

            // Enter and Esc both go to the safe button: the left one never fires
            // from a stray key press
            AcceptButton = cancel;
            CancelButton = cancel;
        }
    }

    // Settings sheet: same white page, bold title and big buttons as the other
    // dialogs. Changing a control does nothing until 保存 is pressed; 取消
    // throws the edits away.
    public class SettingsDialog : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static readonly string[] LanguageIds =
            new string[] { "zh-CN", "zh-TW", "en", "ja" };

        private readonly ComboBox language;
        private readonly CheckBox docUpdate;
        private readonly CheckBox docDaily;
        private readonly TextBox appUrl;
        private readonly CheckBox appUpdate;
        private readonly CheckBox appDaily;
        private readonly TrackBar searchMax;
        private readonly CheckBox clearTabs;
        private readonly string[] docIds;
        private readonly string[] docNames;
        private readonly Dictionary<string, string> downloadMethods;

        // filled in when 保存 is pressed; null when the sheet was cancelled
        public AppSettings Result;

        public SettingsDialog(AppSettings current, string[] ids, string[] names)
        {
            docIds = ids ?? new string[0];
            docNames = names ?? new string[0];
            downloadMethods = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < docIds.Length; i++)
                downloadMethods[docIds[i]] = current.DocDownloadMethod(docIds[i]);

            Text = Lang.T("设置", "設定", "Settings", "設定");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = Lang.UiFont(10F);
            ClientSize = new Size(660, 773);

            Label title = new Label();
            title.Text = Text;
            title.Font = Lang.UiFont(18F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(34, 28);
            Controls.Add(title);

            Section(Lang.T("系统语言设置 (Language)", "系統語言設定 (Language)",
                           "Interface language", "表示言語"), 86);
            language = new ComboBox();
            language.DropDownStyle = ComboBoxStyle.DropDownList;
            language.Font = Lang.UiFont(11F);
            language.Size = new Size(260, 30);
            language.Location = new Point(36, 114);
            language.Items.AddRange(new object[] { "简体中文", "繁體中文", "English", "日本語" });
            language.SelectedIndex = LanguageIndex(current.Language);
            Controls.Add(language);

            Section(Lang.T("文档更新检查", "文件更新檢查",
                           "Document updates", "文書の更新確認"), 168);
            docUpdate = Check(Lang.T("启动时检查文档更新", "啟動時檢查文件更新",
                                     "Check for document updates at startup",
                                     "起動時に文書の更新を確認する"), 196);
            docUpdate.Checked = current.DocUpdateOnStart;
            docDaily = Check(Lang.T("每天最多检查一次", "每天最多檢查一次",
                                    "Check at most once a day",
                                    "チェックは 1 日 1 回まで"), 224);
            docDaily.Checked = current.DocUpdateDaily;

            Section(Lang.T("关闭清除标签", "關閉時清除標籤",
                           "Tabs on close", "終了時のタブ"), 272);
            clearTabs = Check(Lang.T("关闭后清除所有标签页", "關閉後清除所有標籤頁",
                                     "Clear all tabs when closing",
                                     "終了時にすべてのタブを閉じる"), 300);
            clearTabs.Checked = current.ClearTabsOnExit;

            Section(Lang.T("搜索记录数量", "搜尋記錄數量",
                           "Search history", "検索履歴の件数"), 348);
            searchMax = new TrackBar();
            searchMax.Minimum = 0;
            searchMax.Maximum = 10;
            searchMax.TickFrequency = 1;
            searchMax.SmallChange = 1;
            searchMax.LargeChange = 1;
            searchMax.AutoSize = false;
            searchMax.Size = new Size(400, 45);
            searchMax.Location = new Point(36, 376);
            int initialHistory = Math.Max(0, Math.Min(10, current.SearchHistoryMax));
            searchMax.Value = initialHistory;
            Controls.Add(searchMax);
            Label historyValue = new Label();
            historyValue.Text = initialHistory.ToString();
            historyValue.Font = Lang.UiFont(11.5F);
            historyValue.AutoSize = true;
            historyValue.Location = new Point(456, 386);
            Controls.Add(historyValue);
            searchMax.ValueChanged += delegate
            {
                historyValue.Text = searchMax.Value.ToString();
            };

            Section(Lang.T("文档下载方法", "文件下載方法",
                           "Document download method",
                           "文書のダウンロード方法"), 427);
            Button configureDownloads = new Button();
            configureDownloads.Text = Lang.T("点击配置", "點擊配置",
                                             "Configure…", "設定…");
            configureDownloads.Font = Lang.UiFont(10.5F);
            configureDownloads.Size = new Size(160, 30);
            configureDownloads.Location = new Point(36, 455);
            configureDownloads.Click += delegate
            {
                using (DownloadMethodsDialog dlg = new DownloadMethodsDialog(
                    downloadMethods, docIds, docNames))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK &&
                        dlg.Methods != null)
                    {
                        downloadMethods.Clear();
                        foreach (KeyValuePair<string, string> pair in dlg.Methods)
                            downloadMethods[pair.Key] = pair.Value;
                    }
                }
            };
            Controls.Add(configureDownloads);

            Section(Lang.T("软件更新检查", "軟體更新檢查",
                           "App updates", "アプリの更新確認"), 513);
            appUrl = new TextBox();
            appUrl.Font = Lang.UiFont(11F);
            appUrl.Size = new Size(588, 30);
            appUrl.Location = new Point(36, 541);
            appUrl.Text = current.AppUpdateUrl;
            Controls.Add(appUrl);

            appUpdate = Check(Lang.T("启动时检查软件更新", "啟動時檢查軟體更新",
                                     "Check for app updates at startup",
                                     "起動時にアプリの更新を確認する"), 579);
            appUpdate.Checked = current.AppUpdateOnStart;
            appDaily = Check(Lang.T("每天最多检查一次", "每天最多檢查一次",
                                    "Check at most once a day",
                                    "チェックは 1 日 1 回まで"), 607);
            appDaily.Checked = current.AppUpdateDaily;

            Button appCheck = new Button();
            appCheck.Text = Lang.T("检查更新", "檢查更新",
                                   "Check now", "今すぐ確認");
            appCheck.Font = Lang.UiFont(10.5F);
            appCheck.Size = new Size(160, 30);
            appCheck.Location = new Point(36, 643);
            appCheck.Click += async delegate
            {
                if (!appCheck.Enabled) return;
                appCheck.Enabled = false;
                string oldText = appCheck.Text;
                appCheck.Text = Lang.T("检查中…", "檢查中…",
                                       "Checking…", "確認中…");
                try
                {
                    Form appForm = Owner as Form;
                    await AppUpdateUI.CheckAndShowAsync(
                        this, true, appUrl.Text.Trim(), appForm);
                }
                finally
                {
                    if (!IsDisposed)
                    {
                        appCheck.Text = oldText;
                        appCheck.Enabled = true;
                    }
                }
            };
            Controls.Add(appCheck);

            Button cancel = new Button();
            cancel.Text = Lang.T("取消", "取消", "Cancel", "キャンセル");
            cancel.Font = Lang.UiFont(11F);
            cancel.Size = new Size(124, 44);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(ClientSize.Width - 316, 699);
            Controls.Add(cancel);

            Button save = new Button();
            save.Text = Lang.T("保存", "儲存", "Save", "保存");
            save.Font = Lang.UiFont(11F);
            save.Size = new Size(124, 44);
            save.DialogResult = DialogResult.OK;
            save.Location = new Point(ClientSize.Width - 172, 699);
            save.Click += delegate { Result = Collect(); };
            Controls.Add(save);

            AcceptButton = save;
            CancelButton = cancel;
        }

        private Label Section(string text, int y)
        {
            Label head = new Label();
            head.Text = text;
            head.Font = Lang.UiFont(12.5F, FontStyle.Bold);
            head.AutoSize = true;
            head.Location = new Point(36, y);
            Controls.Add(head);
            return head;
        }

        private CheckBox Check(string text, int y)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Font = Lang.UiFont(11.5F);
            box.AutoSize = true;
            box.Location = new Point(36, y);
            Controls.Add(box);
            return box;
        }

        private static int LanguageIndex(string id)
        {
            for (int i = 0; i < LanguageIds.Length; i++)
                if (LanguageIds[i] == id) return i;
            return 0;
        }

        private AppSettings Collect()
        {
            AppSettings s = new AppSettings();
            s.Language = LanguageIds[language.SelectedIndex < 0 ? 0 : language.SelectedIndex];
            s.DocUpdateOnStart = docUpdate.Checked;
            s.DocUpdateDaily = docDaily.Checked;
            s.AppUpdateOnStart = appUpdate.Checked;
            s.AppUpdateDaily = appDaily.Checked;
            s.AppUpdateUrl = appUrl.Text.Trim();
            s.SearchHistoryMax = searchMax.Value;
            s.ClearTabsOnExit = clearTabs.Checked;
            foreach (KeyValuePair<string, string> pair in downloadMethods)
                s.DocDownloadMethods[pair.Key] = pair.Value;
            return s;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // grey cue inside the empty URL box
            SendMessage(appUrl.Handle, 0x1501, (IntPtr)1, AppUpdater.DefaultUrl);
        }
    }

    // Per-document download path picker: one row per document, two radio
    // columns. The choices are held here until the main Settings sheet is saved.
    public class DownloadMethodsDialog : Form
    {
        private readonly string[] ids;
        private readonly Dictionary<string, RadioButton> python =
            new Dictionary<string, RadioButton>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RadioButton> browser =
            new Dictionary<string, RadioButton>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Methods;

        public DownloadMethodsDialog(Dictionary<string, string> current,
                                     string[] docIds, string[] docNames)
        {
            ids = docIds ?? new string[0];

            Text = Lang.T("文档下载方法", "文件下載方法",
                          "Document download method",
                          "文書のダウンロード方法");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = Lang.UiFont(10F);

            int rowTop = 88;
            int rowHeight = 42;
            int buttonsY = Math.Max(370, rowTop + ids.Length * rowHeight + 24);
            ClientSize = new Size(660, buttonsY + 74);

            Label title = new Label();
            title.Text = Text;
            title.Font = Lang.UiFont(18F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(34, 28);
            Controls.Add(title);

            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                if (string.IsNullOrEmpty(id)) continue;
                string name = (docNames != null && i < docNames.Length &&
                               !string.IsNullOrEmpty(docNames[i]))
                    ? docNames[i] : id;
                int y = rowTop + i * rowHeight;

                Label rowName = new Label();
                rowName.Text = name;
                rowName.Font = Lang.UiFont(11F);
                rowName.AutoSize = false;
                rowName.AutoEllipsis = true;
                rowName.UseMnemonic = false;
                rowName.TextAlign = ContentAlignment.MiddleLeft;
                rowName.Size = new Size(300, 26);
                rowName.Location = new Point(36, y + 8);
                Controls.Add(rowName);

                string method = null;
                if (current != null) current.TryGetValue(id, out method);
                bool useBrowser = string.Equals(method, "browser",
                    StringComparison.OrdinalIgnoreCase);

                RadioButton py = new RadioButton();
                py.Text = Lang.T("独立 Python", "獨立 Python",
                                 "Standalone Python", "独立 Python");
                py.Font = Lang.UiFont(10.5F);
                py.AutoSize = true;
                py.Location = new Point(10, 4);
                py.Checked = !useBrowser;

                RadioButton br = new RadioButton();
                br.Text = Lang.T("内嵌浏览器", "內嵌瀏覽器",
                                 "Embedded browser", "内蔵ブラウザー");
                br.Font = Lang.UiFont(10.5F);
                br.AutoSize = true;
                br.Location = new Point(150, 4);
                br.Checked = useBrowser;

                // one panel per row: WinForms groups radio buttons by their
                // container, so each document gets its own pair
                Panel choice = new Panel();
                choice.Location = new Point(350, y + 3);
                choice.Size = new Size(280, 30);
                choice.BackColor = Color.White;
                choice.Controls.Add(py);
                choice.Controls.Add(br);
                Controls.Add(choice);
                python[id] = py;
                browser[id] = br;
            }

            Button cancel = new Button();
            cancel.Text = Lang.T("取消", "取消", "Cancel", "キャンセル");
            cancel.Font = Lang.UiFont(11F);
            cancel.Size = new Size(124, 44);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(ClientSize.Width - 316, buttonsY);
            Controls.Add(cancel);

            Button save = new Button();
            save.Text = Lang.T("保存", "儲存", "Save", "保存");
            save.Font = Lang.UiFont(11F);
            save.Size = new Size(124, 44);
            save.Location = new Point(ClientSize.Width - 172, buttonsY);
            save.Click += delegate
            {
                Methods = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    RadioButton br;
                    Methods[id] = browser.TryGetValue(id, out br) && br.Checked
                        ? "browser" : "python";
                }
                DialogResult = DialogResult.OK;
            };
            Controls.Add(save);

            AcceptButton = save;
            CancelButton = cancel;
        }
    }

    // Uses the same visual language as the update prompt, for "already up to
    // date" and other check results. A plain Form, so no system sound.
    public class InfoDialog : Form
    {
        // an optional extra button, left of "确定". It answers DialogResult.Retry
        // so the caller can tell which one was pressed.
        public InfoDialog(string heading, string text)
            : this(heading, text, null, null) { }

        public InfoDialog(string heading, string text, string extraText)
            : this(heading, text, extraText, null) { }

        public InfoDialog(string heading, string text, string extraText, string windowTitle)
        {
            Text = string.IsNullOrEmpty(windowTitle)
                ? Lang.T("检查更新", "檢查更新", "Check for updates", "アップデートを確認")
                : windowTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = Lang.UiFont(10F);
            ClientSize = new Size(660, 330);

            Label title = new Label();
            title.Text = heading;
            title.Font = Lang.UiFont(18F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(34, 28);
            Controls.Add(title);

            Label body = new Label();
            body.Text = text;
            body.Font = Lang.UiFont(11.5F);
            body.AutoSize = true;
            body.MaximumSize = new Size(592, 0);
            body.Location = new Point(36, 88);
            Controls.Add(body);

            // grow to fit the body: a problem list is longer than two lines
            int height = 88 + body.PreferredHeight + 24 + 44 + 26;
            if (height > ClientSize.Height) ClientSize = new Size(660, height);
            int rowY = ClientSize.Height - 44 - 26;

            Button ok = new Button();
            ok.Text = Lang.T("确定", "確定", "OK", "OK");
            ok.Font = Lang.UiFont(11F);
            ok.Size = new Size(124, 44);
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(ClientSize.Width - 172, rowY);
            Controls.Add(ok);

            if (!string.IsNullOrEmpty(extraText))
            {
                Button extra = new Button();
                extra.Text = extraText;
                extra.Font = Lang.UiFont(11F);
                extra.Size = new Size(124, 44);
                extra.DialogResult = DialogResult.Retry;
                extra.Location = new Point(ClientSize.Width - 172 - 140, rowY);
                Controls.Add(extra);
            }

            AcceptButton = ok;
            CancelButton = ok;
        }
    }

    // Custom modal dialog: bigger type, no system beep, OK / Cancel plus a
    // centred "don't remind me about this update" checkbox.
    public class UpdateDialog : Form
    {
        private readonly CheckBox ignore;

        public bool IgnoreThisVersion
        {
            get { return ignore.Checked; }
        }

        public UpdateDialog(string details)
        {
            Text = Lang.T("文档更新", "文件更新", "Document update", "文書の更新");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = Lang.UiFont(10F);
            ClientSize = new Size(660, 420);

            Label title = new Label();
            title.Text = Lang.T("线上文档已更新", "線上文件已更新",
                                "The online document has been updated",
                                "オンライン文書が更新されました");
            title.Font = Lang.UiFont(18F, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(34, 28);
            Controls.Add(title);

            Label body = new Label();
            body.Text = details + "\r\n\r\n" +
                Lang.T("需 3~5 分钟，受网速与站点影响\r\n请耐心等待切勿关闭！",
                       "需 3~5 分鐘，受網速與站點影響\r\n請耐心等待切勿關閉！",
                       "Takes 3–5 minutes depending on the connection.\r\nPlease keep the app open.",
                       "3～5 分かかります（回線とサイトの状況によります）。\r\nアプリを閉じずにお待ちください。");
            body.Font = Lang.UiFont(11.5F);
            body.AutoSize = true;
            body.MaximumSize = new Size(592, 0);
            body.Location = new Point(36, 88);
            Controls.Add(body);

            // four version/build lines plus the wait note: grow the sheet when
            // the text would run into the checkbox instead of overlapping it
            int shift = 0;
            int bodyBottom = 88 + body.PreferredHeight;
            if (bodyBottom > 250)
            {
                shift = bodyBottom - 250;
                ClientSize = new Size(ClientSize.Width, ClientSize.Height + shift);
            }

            ignore = new CheckBox();
            ignore.Text = Lang.T("本次更新不再提示", "本次更新不再提示",
                                 "Don't remind me about this update",
                                 "このアップデートを今後通知しない");
            ignore.Font = Lang.UiFont(10.5F);
            ignore.AutoSize = true;
            ignore.Location = new Point(250, 268 + shift);
            Controls.Add(ignore);

            Button ok = new Button();
            ok.Text = Lang.T("确定", "確定", "OK", "OK");
            ok.Font = Lang.UiFont(11F);
            ok.Size = new Size(124, 44);
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(ClientSize.Width - 316, 340 + shift);
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = Lang.T("取消", "取消", "Cancel", "キャンセル");
            cancel.Font = Lang.UiFont(11F);
            cancel.Size = new Size(124, 44);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(ClientSize.Width - 172, 340 + shift);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ignore.Left = (ClientSize.Width - ignore.Width) / 2;
        }
    }

    // Windows taskbar progress (green fill behind the task button).
    public static class TaskbarProgress
    {
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int state);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"),
         ClassInterface(ClassInterfaceType.None)]
        private class TaskbarList
        {
        }

        private static ITaskbarList3 list;

        private static ITaskbarList3 List
        {
            get
            {
                if (list == null)
                {
                    list = (ITaskbarList3)new TaskbarList();
                    list.HrInit();
                }
                return list;
            }
        }

        public static void Set(IntPtr hwnd, int percent)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                List.SetProgressState(hwnd, 2);              // TBPF_NORMAL
                List.SetProgressValue(hwnd, (ulong)percent, 100);
            }
            catch (Exception)
            {
                // taskbar integration is cosmetic
            }
        }

        public static void Clear(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || list == null) return;
                list.SetProgressState(hwnd, 0);              // TBPF_NOPROGRESS
            }
            catch (Exception) { }
        }
    }

    // Tiny "checking…" window shown while a manual version check runs.
    public class BusyForm : Form
    {
        private readonly Label label;
        private readonly ProgressBar bar;
        private readonly bool determinate;

        public BusyForm(string text) : this(text, false) { }

        // determinate=true gives a real 0..100 bar instead of the endless
        // marquee: a check that reports "100%" has to show a full bar
        public BusyForm(string text, bool determinate)
        {
            this.determinate = determinate;

            Text = Lang.T("检查更新", "檢查更新", "Check for updates", "アップデートを確認");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.Manual;
            ControlBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = Lang.UiFont(10F);
            ClientSize = new Size(360, 118);

            label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Location = new Point(24, 22);
            label.Size = new Size(312, 26);
            label.Font = Lang.UiFont(11F);
            Controls.Add(label);

            bar = new ProgressBar();
            if (determinate)
            {
                bar.Style = ProgressBarStyle.Continuous;
                bar.Minimum = 0;
                bar.Maximum = 100;
                bar.Value = 0;
            }
            else
            {
                bar.Style = ProgressBarStyle.Marquee;
                bar.MarqueeAnimationSpeed = 30;
            }
            bar.Location = new Point(24, 64);
            bar.Size = new Size(312, 16);
            Controls.Add(bar);
        }

        public void SetText(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => SetText(text))); return; }
            label.Text = text;
        }

        // text + fill together, for the determinate mode
        public void Report(string text, int percent)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => Report(text, percent))); return; }
            label.Text = text;
            if (!determinate) return;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (bar.Value != percent) bar.Value = percent;
        }

        public void CenterOn(Form owner)
        {
            Screen screen = owner != null
                ? Screen.FromControl(owner)
                : Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            Location = new Point(
                area.X + (area.Width - Width) / 2,
                area.Y + (area.Height - Height) / 2);
        }
    }

    // Small modal-less window shown while the mirror is being rebuilt. Once
    // the update finishes it turns into the "start now?" prompt.
    public class ProgressForm : Form
    {
        private readonly Label status;
        private readonly ProgressBar bar;
        private readonly Button cancel;
        private readonly Button startNow;
        private readonly Button later;
        private TaskCompletionSource<bool> choice;

        public event EventHandler CancelRequested;

        public ProgressForm()
            : this(Lang.T("正在更新文档", "正在更新文件",
                          "Updating document", "文書を更新中"),
                   Lang.T("取消更新", "取消更新",
                          "Cancel update", "アップデートを中止")) { }

        public ProgressForm(string title, string cancelText)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            // positioned explicitly by CenterOn(): CenterParent only applies
            // to modal dialogs, and this one is shown modelessly
            StartPosition = FormStartPosition.Manual;
            ControlBox = false;
            ShowInTaskbar = true;
            BackColor = Color.White;
            Font = Lang.UiFont(10F);
            ClientSize = new Size(520, 180);

            status = new Label();
            status.Text = Lang.T("准备中…", "準備中…", "Preparing…", "準備中…");
            status.AutoSize = false;
            status.Location = new Point(26, 24);
            status.Size = new Size(468, 30);
            status.Font = Lang.UiFont(10.5F);
            Controls.Add(status);

            bar = new ProgressBar();
            bar.Style = ProgressBarStyle.Continuous;
            bar.Maximum = 100;
            bar.Location = new Point(26, 68);
            bar.Size = new Size(468, 20);
            Controls.Add(bar);

            cancel = new Button();
            cancel.Text = cancelText;
            cancel.Font = Lang.UiFont(10.5F);
            cancel.Size = new Size(132, 38);
            cancel.Location = new Point(ClientSize.Width - 158, 118);
            cancel.Click += (s, e) =>
            {
                if (CancelRequested != null) CancelRequested(this, EventArgs.Empty);
            };
            Controls.Add(cancel);

            startNow = new Button();
            startNow.Text = Lang.T("立即启动", "立即啟動", "Start now", "今すぐ起動");
            startNow.Font = Lang.UiFont(10.5F);
            startNow.Size = new Size(132, 38);
            startNow.Location = new Point(ClientSize.Width - 158, 118);
            startNow.Visible = false;
            startNow.Click += (s, e) => Answer(true);
            Controls.Add(startNow);

            later = new Button();
            later.Text = Lang.T("稍后", "稍後", "Later", "あとで");
            later.Font = Lang.UiFont(10.5F);
            later.Size = new Size(100, 38);
            later.Location = new Point(ClientSize.Width - 270, 118);
            later.Visible = false;
            later.Click += (s, e) => Answer(false);
            Controls.Add(later);
        }

        private void Answer(bool start)
        {
            if (choice != null) choice.TrySetResult(start);
        }

        // centre on the screen that currently holds the reader window
        public void CenterOn(Form owner)
        {
            Screen screen = owner != null
                ? Screen.FromControl(owner)
                : Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;
            Location = new Point(
                area.X + (area.Width - Width) / 2,
                area.Y + (area.Height - Height) / 2);
        }

        public void Report(string text, int percent)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)(() => Report(text, percent))); return; }

            status.Text = text;
            if (percent < 0) return;

            if (percent > 100) percent = 100;
            bar.Value = percent;
            TaskbarProgress.Set(Handle, percent);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            TaskbarProgress.Clear(Handle);
            base.OnFormClosing(e);
        }

        public void SetCancelling()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)(SetCancelling)); return; }
            status.Text = Lang.T("正在取消下载……", "正在取消下載……",
                                 "Cancelling the download…",
                                 "ダウンロードを中止しています……");
            cancel.Enabled = false;
            TaskbarProgress.Clear(Handle);
        }

        // called once the downloads are done: the last steps cannot be stopped
        public void SetFinalizing()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)(SetFinalizing)); return; }
            cancel.Enabled = false;
            cancel.Text = Lang.T("即将完成", "即將完成", "Finishing…", "まもなく完了");
        }

        // switch from "progress" to "finished, start now?" and wait for a choice
        public Task<bool> AskRestartAsync()
        {
            if (InvokeRequired)
                return (Task<bool>)Invoke(new Func<Task<bool>>(AskRestartAsync));

            choice = new TaskCompletionSource<bool>();
            Text = Lang.T("更新完成", "更新完成", "Update complete", "アップデート完了");
            status.Text = Lang.T("文档已更新完成，是否立即启动？",
                                 "文件已更新完成，是否立即啟動？",
                                 "The document has been updated. Start now?",
                                 "文書の更新が完了しました。今すぐ起動しますか？");
            status.Font = Lang.UiFont(12F, FontStyle.Bold);
            bar.Visible = false;
            cancel.Visible = false;
            startNow.Visible = true;
            later.Visible = true;
            startNow.Focus();
            TaskbarProgress.Clear(Handle);
            return choice.Task;
        }
    }

    // Message-pump host for the headless "--update" mode.
    public class HeadlessUpdate : Form
    {
        public HeadlessUpdate()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(10, 10);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                SiteUpdater updater = new SiteUpdater(
                    AppDomain.CurrentDomain.BaseDirectory, (m, p) => Logs.Write("update[" + p + "%]: " + m));
                await updater.RunAsync();
                Logs.Write("update: finished");
            }
            catch (Exception ex)
            {
                Logs.Write("update: failed " + ex);
                Environment.ExitCode = 1;
            }
            Close();
        }
    }

    // Message-pump host for the "--check" diagnostic mode.
    public class HeadlessCheck : Form
    {
        public HeadlessCheck()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(10, 10);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "site", "zh-CN", "_static", "documentation_options.js");
                string local = File.Exists(file)
                    ? Regex.Match(File.ReadAllText(file), "VERSION\\s*:\\s*'([^']+)'").Groups[1].Value
                    : "";

                string remote = await SiteUpdater.ProbeVersionAsync();
                Logs.Write("check-only: local=" + local + " remote=" + remote);
            }
            catch (Exception ex)
            {
                Logs.Write("check-only: failed " + ex.Message);
                Environment.ExitCode = 1;
            }
            Close();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppSettings startup = AppSettings.Load();
            Lang.Set(startup.Language);

            // maintenance mode: crawl + rebuild without showing the reader
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--update", StringComparison.OrdinalIgnoreCase))
            {
                Application.Run(new HeadlessUpdate());
                return;
            }

            // diagnostics: probe the online version without opening the reader
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--check", StringComparison.OrdinalIgnoreCase))
            {
                Application.Run(new HeadlessCheck());
                return;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string root = Path.Combine(baseDir, "site");

            if (!Directory.Exists(root))
            {
                MessageBox.Show(
                    Lang.T("未找到文档目录：\n", "找不到文件目錄：\n",
                           "Document folder not found:\n",
                           "文書フォルダが見つかりません：\n") + root,
                    Lang.T("Ren'Py 中文文档", "Ren'Py 中文文件",
                           "Ren'Py Documentation", "Ren'Py ドキュメント"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RemoveOldBackup(root);
            Application.Run(new MainForm(root));
        }

        // A finished update leaves the previous mirror in site.bak so the swap
        // can be undone; once the new copy has been opened successfully there
        // is no reason to keep it around, so it goes on the next start.
        private static void RemoveOldBackup(string root)
        {
            try
            {
                string backup = Path.Combine(Path.GetDirectoryName(root), "site.bak");
                if (!Directory.Exists(backup)) return;

                // only drop it when the live copy looks complete
                if (!File.Exists(Path.Combine(root, "zh-CN", "index.html"))) return;
                if (!File.Exists(Path.Combine(root, "zh-CN", "search-index.js"))) return;

                Directory.Delete(backup, true);
                Logs.Write("startup: removed site.bak");
            }
            catch (Exception ex)
            {
                Logs.Write("startup: could not remove site.bak: " + ex.Message);
            }
        }
    }
}
