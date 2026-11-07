using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RenPyDocs
{
    // Downloads the live docs with a hidden WebView2 (it is the only thing that
    // can pass the site's WAF challenge), rebuilds the mirror with the bundled
    // Python builder, then swaps it in after keeping the old copy as site.bak.
    public class SiteUpdater
    {
        private const string ZhRoot = "https://doc.renpy.cn/zh-CN/";

        private readonly string appRoot;
        private readonly Action<string, int> report;
        private readonly CancellationToken token;
        private readonly Action onFinalize;
        // called between items: the reader uses it to hold the download while
        // the user has it paused
        private readonly Func<Task> tick;
        private readonly Dictionary<string, TaskCompletionSource<string>> pending =
            new Dictionary<string, TaskCompletionSource<string>>();
        private int seq;

        // which document and which site this run belongs to; every document is
        // the same Sphinx build, only the host and the folder differ
        private readonly string docId;
        private readonly string baseUrl;    // always ends with "/"
        private readonly string origin;     // scheme + host
        private readonly string authority;  // host[:port], for same-site checks
        private readonly string rootPath;   // "/zh-CN/", "/doc/html/", …

        private Form host;
        private WebView2 web;

        public SiteUpdater(string appRoot, Action<string, int> report,
                           CancellationToken token = default(CancellationToken),
                           Action onFinalize = null,
                           Func<Task> tick = null)
            : this(appRoot, "zh-CN", ZhRoot, report, token, onFinalize, tick) { }

        public SiteUpdater(string appRoot, string docId, string siteUrl,
                           Action<string, int> report,
                           CancellationToken token = default(CancellationToken),
                           Action onFinalize = null,
                           Func<Task> tick = null)
        {
            this.appRoot = appRoot;
            this.report = report;
            this.token = token;
            this.onFinalize = onFinalize;
            this.tick = tick;

            this.docId = docId;
            this.baseUrl = siteUrl.EndsWith("/") ? siteUrl : siteUrl + "/";
            Uri uri = new Uri(this.baseUrl);
            this.origin = uri.Scheme + "://" + uri.Authority;
            this.authority = uri.Authority;
            this.rootPath = uri.AbsolutePath;
        }

        // pause switch / progress of the caller, awaited between items so it
        // never blocks the window
        private async Task TickAsync()
        {
            if (tick == null) return;
            await tick();
        }

        public async Task RunAsync()
        {
            string work = Path.Combine(AppPaths.DataDir, "update");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            string staging = Path.Combine(work, "site");
            Directory.CreateDirectory(Path.Combine(staging, docId));

            try
            {
                OpenHiddenBrowser();
                await LoadRemoteAsync();
                token.ThrowIfCancellationRequested();

                List<string> pages = await GetDocNamesAsync();
                report(string.Format(Lang.T("共 {0} 个页面，开始下载…",
                                            "共 {0} 個頁面，開始下載…",
                                            "{0} pages, starting the download…",
                                            "{0} ページ、ダウンロードを開始します…"),
                                     pages.Count), 5);

                HashSet<string> assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int done = 0;
                foreach (string name in pages)
                {
                    token.ThrowIfCancellationRequested();
                    await TickAsync();
                    token.ThrowIfCancellationRequested();

                    string path = rootPath + name + ".html";
                    string html = await FetchTextAsync(path);
                    if (html == null) continue;

                    string file = Path.Combine(staging, docId, name + ".html");
                    File.WriteAllText(file, html, new UTF8Encoding(false));

                    CollectAssets(html, path, assets);
                    done++;
                    if (done % 5 == 0 || done == pages.Count)
                        report(Lang.T("页面 ", "頁面 ", "Page ", "ページ ") +
                               done + "/" + pages.Count + "…",
                               5 + done * 55 / pages.Count);
                    await Task.Delay(120);   // stay under the site's rate limit
                }
                report(Lang.T("页面下载完成，开始下载静态资源…",
                              "頁面下載完成，開始下載靜態資源…",
                              "Pages done, downloading styles and images…",
                              "ページ完了、静的ファイルをダウンロードします…"), 60);

                int a = 0;
                foreach (string url in assets)
                {
                    token.ThrowIfCancellationRequested();
                    await TickAsync();
                    token.ThrowIfCancellationRequested();

                    await DownloadAssetAsync(url, staging);
                    a++;
                    if (a % 5 == 0 || a == assets.Count)
                        report(Lang.T("资源 ", "資源 ", "Assets ", "リソース ") +
                               a + "/" + assets.Count + "…",
                               60 + a * 30 / Math.Max(assets.Count, 1));
                    await Task.Delay(40);
                }

                CloseHiddenBrowser();
                token.ThrowIfCancellationRequested();

                // past this point the work is short and cannot be interrupted
                if (onFinalize != null) onFinalize();
                report(Lang.T("资源下载完成，正在重建索引…",
                              "資源下載完成，正在重建索引…",
                              "Assets done, rebuilding the index…",
                              "リソース完了、索引を再構築しています…"), 92);
                RunBuilder(staging);
                report(Lang.T("正在替换本地文档…", "正在替換本機文件…",
                              "Replacing the local copy…",
                              "ローカルの文書を置き換えています…"), 97);
                // stamp first: the check compares this against the site's
                // Last-Modified, and Swap() carries root files across
                await WriteBuildStampAsync(staging);
                Swap(staging);
                report(Lang.T("更新完成", "更新完成",
                              "Update complete", "アップデート完了"), 100);
            }
            finally
            {
                CloseHiddenBrowser();
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (Exception) { }
            }
        }

        // Same meaning as the build.txt fetch_en.py writes: when the site was
        // last built, or the download time when the server does not say. Keeps a
        // copy fetched through this browser path from looking permanently old.
        private async Task WriteBuildStampAsync(string staging)
        {
            DateTime when = DateTime.UtcNow;
            try
            {
                using (HttpClient http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(6);
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    using (HttpRequestMessage request = new HttpRequestMessage(
                        HttpMethod.Head, baseUrl + "searchindex.js"))
                    using (HttpResponseMessage response = await http.SendAsync(request))
                    {
                        if (response.IsSuccessStatusCode && response.Content != null &&
                            response.Content.Headers.LastModified.HasValue)
                            when = response.Content.Headers.LastModified.Value.UtcDateTime;
                    }
                }
            }
            catch (Exception) { }

            try
            {
                File.WriteAllText(Path.Combine(staging, docId, "build.txt"),
                                  when.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\n",
                                  new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // Used by the version check when plain HTTP is challenged by the WAF:
        // the hidden browser passes the challenge, then reads the version file.
        public static async Task<string> ProbeVersionAsync()
        {
            SiteUpdater probe = new SiteUpdater(AppDomain.CurrentDomain.BaseDirectory, (m, p) => { });
            try
            {
                probe.OpenHiddenBrowser();
                await probe.LoadRemoteAsync();
                string js = await probe.FetchTextAsync(probe.rootPath + "_static/documentation_options.js");
                Match m = Regex.Match(js ?? "", "VERSION\\s*:\\s*'([^']+)'");
                return m.Success ? m.Groups[1].Value : "";
            }
            finally
            {
                probe.CloseHiddenBrowser();
            }
        }

        // ------------------------------------------------------------ browser
        // an off-screen browser for the site's challenge: it must never take
        // the focus away from the reader
        private class HiddenHost : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        private void OpenHiddenBrowser()
        {
            host = new HiddenHost();
            host.FormBorderStyle = FormBorderStyle.None;
            host.StartPosition = FormStartPosition.Manual;
            host.Location = new Point(-32000, -32000);
            host.Size = new Size(1200, 900);
            host.ShowInTaskbar = false;
            host.Text = "RenPyDocsUpdater";

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            host.Controls.Add(web);
            host.Show();
        }

        private async Task LoadRemoteAsync()
        {
            string profile = Path.Combine(AppPaths.DataDir, "WebView2-worker");
            Directory.CreateDirectory(profile);

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, profile, null);
            await web.EnsureCoreWebView2Async(env);

            CoreWebView2 core = web.CoreWebView2;
            core.WebMessageReceived += OnMessage;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Navigate(baseUrl);

            // wait for the WAF challenge to finish (page title becomes the docs')
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                string title = "";
                try
                {
                    string raw = await core.ExecuteScriptAsync("document.title");
                    title = DecodeJsString(raw);
                }
                catch (Exception) { }

                // the Chinese pages spell it "Ren’Py" with a curly apostrophe
                string flat = title.Replace("\u2019", "'").Replace("\u2018", "'");
                if (flat.Length > 0 && flat.IndexOf("Ren'Py", StringComparison.Ordinal) >= 0
                    && title.IndexOf("安全检测", StringComparison.Ordinal) < 0)
                {
                    await core.ExecuteScriptAsync(InjectHelper());
                    return;
                }
            }
            throw new Exception(Lang.T("无法通过文档站的访问校验（30 秒超时）。",
                                       "無法通過文件站的存取驗證（30 秒逾時）。",
                                       "Could not pass the docs site's browser check (30 s timeout).",
                                       "ドキュメントサイトの確認を通過できませんでした（30 秒でタイムアウト）。"));
        }

        private static string DecodeJsString(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\/", "/");
        }

        private static string InjectHelper()
        {
            return
                "window.__rpFetch=async function(id,url,b64){function send(k,d){chrome.webview.postMessage(id+'\\u0001'+k+'\\u0001'+d);}" +
                "try{var r=await fetch(url,{credentials:'include',cache:'no-store'});" +
                "if(!r.ok){return send('E',String(r.status));}" +
                "if(b64){var buf=new Uint8Array(await r.arrayBuffer());var s='';" +
                "for(var i=0;i<buf.length;i+=0x8000){s+=String.fromCharCode.apply(null,buf.subarray(i,i+0x8000));}" +
                "return send('B',btoa(s));}" +
                "return send('T',await r.text());}catch(e){send('X',String(e));}};'ok'";
        }

        private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg = null;
            try { msg = e.TryGetWebMessageAsString(); }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(msg)) return;

            string[] parts = msg.Split(new char[] { '\u0001' }, 3);
            if (parts.Length < 3) return;

            TaskCompletionSource<string> tcs;
            if (!pending.TryGetValue(parts[0], out tcs)) return;
            tcs.TrySetResult(parts[1] + parts[2]);
        }

        private void CloseHiddenBrowser()
        {
            try
            {
                if (host != null && !host.IsDisposed) host.Close();
                if (host != null) host.Dispose();
            }
            catch (Exception) { }
            host = null;
            web = null;
        }

        // ------------------------------------------------------------- fetch
        private async Task<string> FetchAsync(string path, bool binary, int timeoutMs)
        {
            // the helper can be lost if the WAF reloads the page under us
            string probe = await web.CoreWebView2.ExecuteScriptAsync("typeof window.__rpFetch");
            if (probe.IndexOf("function", StringComparison.Ordinal) < 0)
                await web.CoreWebView2.ExecuteScriptAsync(InjectHelper());

            string id = (++seq).ToString();
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            pending[id] = tcs;

            string js = "__rpFetch(" + JsString(id) + "," + JsString(path) + "," +
                        (binary ? "true" : "false") + ")";
            await web.CoreWebView2.ExecuteScriptAsync(js);

            Task finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            pending.Remove(id);
            if (finished != tcs.Task)
            {
                Logs.Write("fetch timeout（" + timeoutMs + "ms）：" + path);
                throw new Exception(Lang.T("下载超时：", "下載逾時：",
                                            "Download timed out: ",
                                            "ダウンロードがタイムアウトしました：") + path);
            }
            return await tcs.Task;
        }

        private static string JsString(string s)
        {
            return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        private async Task<string> FetchRetryAsync(string path, bool binary)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string r = await FetchAsync(path, binary, binary ? 60000 : 30000);
                if (r.Length > 0 && (r[0] == 'T' || r[0] == 'B')) return r;

                if (r.Length > 0 && r[0] == 'E' && r.Substring(1) == "404") return null;

                // 468 is this site's "too fast" block; back off and try again
                await Task.Delay(1500 + attempt * 1500);
            }
            throw new Exception(Lang.T("下载失败（多次重试）：", "下載失敗（多次重試）：",
                                       "Download failed after several retries: ",
                                       "ダウンロードに失敗しました（再試行後）：") + path);
        }

        private async Task<string> FetchTextAsync(string path)
        {
            string r = await FetchRetryAsync(path, false);
            return r == null ? null : r.Substring(1);
        }

        private async Task<byte[]> FetchBytesAsync(string path)
        {
            string r = await FetchRetryAsync(path, true);
            return r == null ? null : Convert.FromBase64String(r.Substring(1));
        }

        private async Task<List<string>> GetDocNamesAsync()
        {
            string js = await FetchTextAsync(rootPath + "searchindex.js");
            if (js == null)
                throw new Exception(Lang.T("无法读取 searchindex.js",
                                           "無法讀取 searchindex.js",
                                           "Could not read searchindex.js",
                                           "searchindex.js を読み取れません"));

            Match m = Regex.Match(js, "\"docnames\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success)
                throw new Exception(Lang.T("searchindex.js 格式无法识别",
                                           "searchindex.js 格式無法辨識",
                                           "Unrecognised searchindex.js format",
                                           "searchindex.js の形式を認識できません"));

            List<string> names = new List<string>();
            foreach (Match n in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\""))
                names.Add(n.Groups[1].Value);
            if (names.Count == 0)
                throw new Exception(Lang.T("没有解析到任何页面",
                                           "沒有解析到任何頁面",
                                           "No pages found in the index",
                                           "ページを 1 つも取得できませんでした"));

            // searchindex.js only lists real documents; the generated index
            // pages are plain files that the sidebar links to as well
            string[] extra = {
                "genindex",
                "std-var-index",
                "std-style-property-index",
                "std-transform-property-index",
                "py-function-class-index"
            };
            foreach (string name in extra)
                if (!names.Contains(name)) names.Add(name);

            return names;
        }

        // ------------------------------------------------------------ assets
        private static readonly Regex RefRx = new Regex(
            "(?:href|src)=\"([^\"]+)\"", RegexOptions.IgnoreCase);

        private void CollectAssets(string html, string pagePath, HashSet<string> into)
        {
            foreach (Match m in RefRx.Matches(html))
            {
                string url = Resolve(pagePath, m.Groups[1].Value);
                if (url == null) continue;
                if (url.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
                if (url.StartsWith(rootPath + "_static/", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith(rootPath + "_images/", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith(rootPath + "_downloads/", StringComparison.OrdinalIgnoreCase))
                    into.Add(url);
            }
        }

        private string Resolve(string pagePath, string link)
        {
            if (string.IsNullOrEmpty(link)) return null;
            if (link.StartsWith("#") || link.StartsWith("mailto:") || link.StartsWith("javascript:") ||
                link.StartsWith("data:") || link.StartsWith("http://") || link.StartsWith("https://") ||
                link.StartsWith("//"))
                return null;

            string clean = link.Split('#')[0].Split('?')[0];
            if (clean.Length == 0) return null;

            try
            {
                Uri pageUri = new Uri(origin + pagePath);
                Uri abs = new Uri(pageUri, clean);
                if (abs.Authority != authority) return null;
                return abs.AbsolutePath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task DownloadAssetAsync(string url, string staging)
        {
            // rootPath is the site's document root ("/doc/html/"); locally every
            // document lives in its own folder named after its id
            string rel = url.StartsWith(rootPath) ? url.Substring(rootPath.Length) : url.TrimStart('/');
            string file = Path.Combine(staging, docId, rel.Replace('/', '\\'));
            if (File.Exists(file)) return;

            report(Lang.T("资源：", "資源：", "Asset: ", "リソース：") + url, -1);
            byte[] data = await FetchBytesAsync(url);
            if (data == null) return;

            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, data);

            // fonts / images referenced from the stylesheet
            if (url.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                string css = Encoding.UTF8.GetString(data);
                foreach (Match m in Regex.Matches(css, "url\\(\\s*['\"]?([^'\")]+)['\"]?\\s*\\)"))
                {
                    string dep = Resolve(url, m.Groups[1].Value);
                    if (dep == null || dep.StartsWith("data:")) continue;
                    await DownloadAssetAsync(dep, staging);
                }
            }
        }

        // ------------------------------------------------------------ builder
        private void RunBuilder(string staging)
        {
            string python = Path.Combine(appRoot, "updater", "python", "python.exe");
            string script = Path.Combine(appRoot, "updater", "build.py");
            string template = Path.Combine(appRoot, "updater", "search_body.html");

            if (!File.Exists(python))
                throw new Exception(Lang.T("缺少内嵌 Python 运行时：", "缺少內嵌 Python 執行環境：",
                                           "Missing the bundled Python runtime: ",
                                           "内蔵 Python ランタイムがありません：") + python);
            if (!File.Exists(script))
                throw new Exception(Lang.T("缺少构建脚本：", "缺少建置腳本：",
                                           "Missing the build script: ",
                                           "ビルドスクリプトがありません：") + script);

            ProcessStartInfo psi = new ProcessStartInfo(python,
                "\"" + script + "\" \"" + staging + "\" \"" + template + "\" \"" + docId + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new Exception(Lang.T("重建索引失败：\n", "重建索引失敗：\n",
                                               "Rebuilding the index failed:\n",
                                               "索引の再構築に失敗しました：\n") +
                                        (error.Length > 0 ? error : output));
            }

            if (!File.Exists(Path.Combine(staging, docId, "search-index.js")))
                throw new Exception(Lang.T("重建结果不完整（缺少 search-index.js）",
                                           "重建結果不完整（缺少 search-index.js）",
                                           "The rebuild is incomplete (search-index.js is missing)",
                                           "再構築の結果が不完全です（search-index.js がありません）"));
        }

        // -------------------------------------------------------------- swap
        // Replace only what this download produced: the document folder it
        // fetched and the generated root pages. Other documents stay where they
        // are — swapping the whole site/ folder used to throw them away.
        private void Swap(string staging)
        {
            string site = Path.Combine(appRoot, "site");
            Directory.CreateDirectory(site);

            string backup = Path.Combine(appRoot, "site.bak");
            try
            {
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            catch (Exception) { }

            foreach (string dir in Directory.GetDirectories(staging))
            {
                string name = Path.GetFileName(dir);
                string target = Path.Combine(site, name);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                else if (File.Exists(target)) File.Delete(target);
                Directory.Move(dir, target);
            }
            foreach (string file in Directory.GetFiles(staging))
                File.Copy(file, Path.Combine(site, Path.GetFileName(file)), true);

            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception) { }

            MirrorToWeb(site);
        }

        // keep the standalone web copy in step with the app
        private void MirrorToWeb(string site)
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(appRoot.TrimEnd(Path.DirectorySeparatorChar));
                if (parent == null) return;

                string web = Path.Combine(parent.FullName, "web", "site");
                if (!Directory.Exists(web)) return;

                Directory.Delete(web, true);
                CopyDirectory(site, web);
            }
            catch (Exception)
            {
                // the app copy is what matters; a failed mirror is harmless
            }
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(from.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(to, rel));
            }
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(from.Length).TrimStart(Path.DirectorySeparatorChar);
                File.Copy(file, Path.Combine(to, rel), true);
            }
        }
    }
}
