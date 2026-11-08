using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RenPyDocs
{
    // Result of one app-release lookup. GitHub releases and a small JSON
    // manifest both end up in this shape.
    public class AppUpdateInfo
    {
        public string Version = "";
        public string Url = "";
        public string Name = "";
        public string DownloadUrl = "";
        public string Sha256 = "";
        public bool PresetDownload;
    }

    // Checks the project's GitHub releases by default. The settings field can
    // also point at another GitHub repository or at a JSON manifest:
    //   {"version":"1.0.1","url":"https://example.com/download"}
    // A plain text file whose first version-looking number is 1.0.1 works too.
    public static class AppUpdater
    {
        // Bump this for every published build; the GitHub tag is compared to it.
        public const string CurrentVersion = "1.0.2";
        public const string DefaultUrl =
            "https://github.com/llfseik/RenPy-Docs-Offline-Reader";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
                // older Windows: leave the default alone
            }

            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RenPyDocs/" + CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static string EffectiveUrl(string configured)
        {
            string url = (configured ?? "").Trim();
            if (url.Length == 0) return DefaultUrl;
            if (url.IndexOf("://", StringComparison.Ordinal) < 0)
                url = "https://" + url;
            return url;
        }

        public static async Task<AppUpdateInfo> CheckAsync(string configured)
        {
            string raw = EffectiveUrl(configured);
            Uri uri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(Lang.T(
                    "更新网址无效。",
                    "更新網址無效。",
                    "The update URL is not valid.",
                    "更新 URL が無効です。"));
            }

            string api = GitHubApiUrl(uri);
            if (api != null)
            {
                AppUpdateInfo info = ParseGitHub(await GetAsync(api));
                if (info.Version.Length == 0)
                {
                    throw new InvalidOperationException(Lang.T(
                        "更新信息里没有版本号。",
                        "更新資訊裡沒有版本號。",
                        "The update information has no version number.",
                        "更新情報にバージョン番号がありません。"));
                }
                if (info.Url.Length == 0) info.Url = raw;
                return info;
            }

            AppUpdateInfo manifest = ParseManifest(await GetAsync(raw), uri);
            if (manifest.Version.Length == 0)
            {
                throw new InvalidOperationException(Lang.T(
                    "更新信息里没有版本号。",
                    "更新資訊裡沒有版本號。",
                    "The update information has no version number.",
                    "更新情報にバージョン番号がありません。"));
            }
            return manifest;
        }

        // github.com/owner/repo (and .../releases, .../releases/latest) become
        // the GitHub API URL. An explicit api.github.com URL is left alone.
        private static string GitHubApiUrl(Uri uri)
        {
            string host = uri.Host.ToLowerInvariant();
            if (host == "api.github.com") return uri.ToString();
            if (host != "github.com" && host != "www.github.com") return null;

            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2) return null;

            string owner = parts[0];
            string repo = parts[1];
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                repo = repo.Substring(0, repo.Length - 4);
            if (owner.Length == 0 || repo.Length == 0) return null;

            return "https://api.github.com/repos/" +
                   Uri.EscapeDataString(owner) + "/" +
                   Uri.EscapeDataString(repo) + "/releases/latest";
        }

        private static async Task<string> GetAsync(string url)
        {
            using (HttpResponseMessage response = await Http.GetAsync(url))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(Lang.T(
                        "还没有发布可用的更新。",
                        "還沒有發佈可用的更新。",
                        "No update has been published yet.",
                        "公開されている更新はまだありません。"));
                }
                if (response.StatusCode == (HttpStatusCode)403)
                {
                    throw new InvalidOperationException(Lang.T(
                        "更新检查太频繁，请稍后再试。",
                        "更新檢查太頻繁，請稍後再試。",
                        "The update check is rate-limited. Try again later.",
                        "更新確認の回数が多すぎます。しばらくしてからお試しください。"));
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(string.Format(
                        Lang.T("更新服务器返回错误（{0}）。",
                               "更新伺服器傳回錯誤（{0}）。",
                               "The update server returned an error ({0}).",
                               "更新サーバーがエラーを返しました（{0}）。"),
                        (int)response.StatusCode));
                }
                return await response.Content.ReadAsStringAsync();
            }
        }

        private static AppUpdateInfo ParseGitHub(string json)
        {
            AppUpdateInfo info = new AppUpdateInfo();
            info.Version = CleanVersion(Field(json, "tag_name"));
            if (info.Version.Length == 0)
                info.Version = CleanVersion(Field(json, "name"));
            info.Url = Field(json, "html_url");
            info.Name = Field(json, "name");
            info.DownloadUrl = FirstZipAsset(json);
            // The API puts one digest on each asset; only use it when there is
            // a single browser_download_url so it cannot belong to another file.
            if (Regex.Matches(json ?? "",
                    "\"browser_download_url\"\\s*:").Count == 1)
            {
                info.Sha256 = CleanSha(Field(json, "digest"));
            }
            return info;
        }

        // GitHub release assets: prefer the Windows/offline ZIP, never the
        // auto-generated source archive (which is not an asset anyway).
        private static string FirstZipAsset(string json)
        {
            string best = "";
            int bestScore = -1;
            foreach (Match match in Regex.Matches(json ?? "",
                "\"browser_download_url\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
            {
                string url = Unescape(match.Groups[1].Value);
                string lower = url.ToLowerInvariant();
                if (lower.IndexOf(".zip", StringComparison.Ordinal) < 0) continue;

                int score = 0;
                if (lower.IndexOf("renpy", StringComparison.Ordinal) >= 0) score += 2;
                if (lower.IndexOf("offline", StringComparison.Ordinal) >= 0) score += 2;
                if (lower.IndexOf("windows", StringComparison.Ordinal) >= 0) score += 1;
                if (lower.IndexOf("source", StringComparison.Ordinal) >= 0) score -= 5;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = url;
                }
            }
            return bestScore >= 0 ? best : "";
        }

        private static AppUpdateInfo ParseManifest(string text, Uri baseUri)
        {
            string body = (text ?? "").Trim();
            string version = "";
            string url = "";
            string download = "";
            string name = "";

            if (body.Length > 0 && body[0] == '{')
            {
                version = Field(body, "version");
                if (version.Length == 0) version = Field(body, "tag_name");
                url = Field(body, "url");
                if (url.Length == 0) url = Field(body, "html_url");
                download = Field(body, "download_url");
                if (download.Length == 0) download = Field(body, "download");
                name = Field(body, "name");
            }

            if (CleanVersion(version).Length == 0)
            {
                Match match = Regex.Match(body, @"\b\d+\.\d+(?:\.\d+)*\b");
                if (match.Success) version = match.Value;
            }
            version = CleanVersion(version);

            url = ResolveHttpUrl(baseUri, url);
            download = ResolveHttpUrl(baseUri, download);
            if (download.Length == 0 &&
                url.IndexOf(".zip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                download = url;
            }
            if (url.Length == 0) url = baseUri.ToString();

            AppUpdateInfo info = new AppUpdateInfo();
            info.Version = version;
            info.Url = url;
            info.Name = name;
            info.DownloadUrl = download;
            info.PresetDownload = download.Length > 0;
            info.Sha256 = CleanSha(Field(body, "sha256"));
            if (info.Sha256.Length == 0)
                info.Sha256 = CleanSha(Field(body, "sha"));
            return info;
        }

        private static string CleanSha(string value)
        {
            string sha = (value ?? "").Trim().ToLowerInvariant();
            if (sha.StartsWith("sha256:", StringComparison.Ordinal))
                sha = sha.Substring(7);
            if (sha.Length != 64) return "";
            for (int i = 0; i < sha.Length; i++)
            {
                char c = sha[i];
                bool hex = (c >= '0' && c <= '9') ||
                           (c >= 'a' && c <= 'f');
                if (!hex) return "";
            }
            return sha;
        }

        private static string ResolveHttpUrl(Uri baseUri, string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            Uri resolved;
            if (!Uri.TryCreate(baseUri, value, out resolved) ||
                (resolved.Scheme != Uri.UriSchemeHttp &&
                 resolved.Scheme != Uri.UriSchemeHttps))
            {
                return "";
            }
            return resolved.ToString();
        }

        // v1.2.3 / V1.2.3 -> 1.2.3
        private static string CleanVersion(string version)
        {
            string value = (version ?? "").Trim();
            if (value.Length > 1 &&
                (value[0] == 'v' || value[0] == 'V') &&
                char.IsDigit(value[1]))
            {
                value = value.Substring(1);
            }
            return value;
        }

        public static bool IsNewer(string remote, string local)
        {
            int[] a = VersionNumbers(remote);
            int[] b = VersionNumbers(local);
            if (a == null || b == null) return false;

            int length = Math.Max(a.Length, b.Length);
            for (int i = 0; i < length; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        private static int[] VersionNumbers(string version)
        {
            Match match = Regex.Match(version ?? "", @"\d+(?:\.\d+)*");
            if (!match.Success) return null;

            string[] parts = match.Value.Split('.');
            int[] numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int number;
                if (!int.TryParse(parts[i], out number)) return null;
                numbers[i] = number;
            }
            return numbers;
        }

        // Pull one string out of GitHub's release JSON. Only the three fields
        // above are needed, so no JSON library has to be added to the build.
        private static string Field(string json, string key)
        {
            Match match = Regex.Match(json ?? "",
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            return match.Success ? Unescape(match.Groups[1].Value) : "";
        }

        private static string Unescape(string value)
        {
            if (value == null || value.IndexOf('\\') < 0) return value ?? "";

            StringBuilder output = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    output.Append(c);
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case '"': output.Append('"'); break;
                    case '\\': output.Append('\\'); break;
                    case '/': output.Append('/'); break;
                    case 'b': output.Append('\b'); break;
                    case 'f': output.Append('\f'); break;
                    case 'n': output.Append('\n'); break;
                    case 'r': output.Append('\r'); break;
                    case 't': output.Append('\t'); break;
                    case 'u':
                        int code;
                        if (i + 4 < value.Length &&
                            int.TryParse(value.Substring(i + 1, 4),
                                         NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture,
                                         out code))
                        {
                            output.Append((char)code);
                            i += 4;
                        }
                        else
                        {
                            output.Append('u');
                        }
                        break;
                    default:
                        output.Append(next);
                        break;
                }
            }
            return output.ToString();
        }
    }

    // Downloads the release ZIP, unpacks it and lets a tiny helper copy the
    // files over the current install after this process has exited. Local
    // documents are preserved: only site/docs.json is skipped.
    public static class AppUpdateInstaller
    {
        public static bool RestartPending { get; private set; }

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = 16;
                ServicePointManager.Expect100Continue = false;
            }
            catch (Exception) { }

            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "RenPyDocs/" + AppUpdater.CurrentVersion);
            return client;
        }

        public static async Task<bool> InstallAsync(IWin32Window owner, AppUpdateInfo info)
        {
            Form ownerForm = owner as Form;
            BusyForm busy = null;
            string work = "";
            try
            {
                if (string.IsNullOrEmpty(info.DownloadUrl)) return false;
                if (ownerForm != null && ownerForm.IsDisposed) return false;

                busy = new BusyForm(Lang.T(
                    "正在下载更新…", "正在下載更新…",
                    "Downloading update…",
                    "アップデートをダウンロード中…"), true);
                busy.Text = Lang.T("软件更新", "軟體更新",
                                   "App update", "アプリの更新");
                if (ownerForm != null) busy.CenterOn(ownerForm);
                if (owner != null) busy.Show(owner); else busy.Show();

                work = Path.Combine(Path.GetTempPath(),
                    "RenPyDocs-update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                string cache = Path.Combine(AppPaths.DataDir, "update-cache");
                string zip = Path.Combine(cache, "update.zip");
                string payload = Path.Combine(work, "payload");

                await EnsureDownloadedAsync(info, cache, delegate(string text, int percent)
                {
                    if (busy != null) busy.Report(text, percent);
                });

                if (busy != null)
                    busy.Report(Lang.T("正在解压更新…", "正在解壓縮更新…",
                                       "Extracting update…", "アップデートを展開中…"), 100);

                Directory.CreateDirectory(payload);
                ZipFile.ExtractToDirectory(zip, payload);

                string[] found = Directory.GetFiles(payload, "RenPyDocs.exe",
                                                     SearchOption.AllDirectories);
                if (found.Length != 1)
                {
                    throw new InvalidOperationException(Lang.T(
                        "更新包里没有找到 RenPyDocs.exe。",
                        "更新包裡沒有找到 RenPyDocs.exe。",
                        "The update package does not contain RenPyDocs.exe.",
                        "アップデートに RenPyDocs.exe が含まれていません。"));
                }
                string srcRoot = Path.GetDirectoryName(found[0]);
                if (string.IsNullOrEmpty(srcRoot))
                    throw new InvalidOperationException("Invalid update package.");

                string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string exePath = Path.Combine(appDir, "RenPyDocs.exe");
                if (!File.Exists(exePath)) exePath = Application.ExecutablePath;

                // Prove we can write next to the executable before closing the
                // app; a read-only install would otherwise just disappear.
                string probe = Path.Combine(appDir, ".renpydocs-update-probe");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);

                if (busy != null)
                    busy.Report(Lang.T("正在准备重启…", "正在準備重新啟動…",
                                       "Preparing restart…", "再起動を準備中…"), 100);

                string script = Path.Combine(work, "apply-update.ps1");
                File.WriteAllText(script, UpdateScript, new UTF8Encoding(true));

                StartHelper(script, srcRoot, appDir, exePath, work);
                RestartPending = true;
                TryDeleteCache(Path.Combine(AppPaths.DataDir, "update-cache"));

                Close(busy);
                busy = null;
                return true;
            }
            catch (Exception ex)
            {
                Close(busy);
                busy = null;
                try
                {
                    if (work.Length > 0) Directory.Delete(work, true);
                }
                catch (Exception) { }

                Logs.Write("app-update: install failed " + ex.Message);
                if (ownerForm == null || !ownerForm.IsDisposed)
                {
                    string text = Lang.T("更新失败。", "更新失敗。",
                                         "Update failed.",
                                         "アップデートに失敗しました。") +
                                  "\r\n\r\n" + ex.Message;
                    string title = Lang.T("更新失败", "更新失敗",
                                          "Update failed", "アップデート失敗");
                    if (owner == null)
                        MessageBox.Show(text, title,
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else
                        MessageBox.Show(owner, text, title,
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return false;
            }
        }

        private const int MaxParts = 8;
        private const long MinPartSize = 1024L * 1024L;
        private const int BufferSize = 262144;

        private class UpdateState
        {
            public string Version = "";
            public string Sha = "";
            public string Etag = "";
            public long Total;
            public int Parts;
            public bool Done;
        }

        private class ProbeResult
        {
            public long Total;
            public string Etag = "";
            public bool Range;
        }

        private class RangeIgnoredException : Exception { }

        private static string StatePath(string cache)
        {
            return Path.Combine(cache, "state.txt");
        }

        private static string PartPath(string cache, int index)
        {
            return Path.Combine(cache, "part-" + index.ToString());
        }

        private static UpdateState LoadState(string cache)
        {
            try
            {
                string path = StatePath(cache);
                if (!File.Exists(path)) return null;

                UpdateState state = new UpdateState();
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "version": state.Version = value; break;
                        case "sha": state.Sha = value; break;
                        case "etag": state.Etag = value; break;
                        case "total":
                            long total;
                            if (long.TryParse(value, out total)) state.Total = total;
                            break;
                        case "parts":
                            int parts;
                            if (int.TryParse(value, out parts)) state.Parts = parts;
                            break;
                        case "done": state.Done = value == "1"; break;
                    }
                }
                return state.Version.Length > 0 ? state : null;
            }
            catch (Exception) { return null; }
        }

        private static void SaveState(string cache, UpdateState state)
        {
            try
            {
                string nl = Environment.NewLine;
                File.WriteAllText(StatePath(cache),
                    "version=" + state.Version + nl +
                    "sha=" + state.Sha + nl +
                    "etag=" + state.Etag + nl +
                    "total=" + state.Total + nl +
                    "parts=" + state.Parts + nl +
                    "done=" + (state.Done ? "1" : "0") + nl);
            }
            catch (Exception) { }
        }

        private static void TryDeleteCache(string cache)
        {
            try
            {
                if (Directory.Exists(cache)) Directory.Delete(cache, true);
            }
            catch (Exception) { }
        }

        private static void DeleteParts(string cache, int parts)
        {
            for (int i = 0; i < parts; i++)
            {
                try
                {
                    string path = PartPath(cache, i);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception) { }
            }
        }

        private static int ComputeParts(long total)
        {
            if (total <= MinPartSize) return 1;
            long count = (total + MinPartSize - 1) / MinPartSize;
            return (int)Math.Min((long)MaxParts, Math.Max(1L, count));
        }

        private static async Task EnsureDownloadedAsync(
            AppUpdateInfo info, string cache, Action<string, int> report)
        {
            Directory.CreateDirectory(cache);
            string zip = Path.Combine(cache, "update.zip");
            UpdateState state = LoadState(cache);

            if (state != null)
            {
                bool wrongVersion = state.Version != info.Version;
                bool wrongSha = info.Sha256.Length > 0 &&
                                state.Sha.Length > 0 &&
                                state.Sha != info.Sha256;
                if (wrongVersion || wrongSha)
                {
                    TryDeleteCache(cache);
                    Directory.CreateDirectory(cache);
                    state = null;
                }
            }

            if (state == null)
            {
                // no usable state: stale parts cannot be trusted
                TryDeleteCache(cache);
                Directory.CreateDirectory(cache);
            }

            if (state != null && state.Done)
            {
                if (File.Exists(zip) &&
                    (state.Total <= 0 || new FileInfo(zip).Length == state.Total))
                {
                    try
                    {
                        VerifyZip(zip, state.Total, state.Sha);
                        return;
                    }
                    catch (Exception)
                    {
                        TryDeleteCache(cache);
                        Directory.CreateDirectory(cache);
                        state = null;
                    }
                }
                else
                {
                    TryDeleteCache(cache);
                    Directory.CreateDirectory(cache);
                    state = null;
                }
            }

            if (state != null && !state.Done && File.Exists(zip))
            {
                try { File.Delete(zip); }
                catch (Exception) { }
            }

            List<string> candidates = Candidates(info);
            if (!info.PresetDownload && candidates.Count > 1)
            {
                if (report != null)
                    report(Lang.T("正在测试下载速度…", "正在測試下載速度…",
                                  "Testing download speed…",
                                  "ダウンロード速度を確認中…"), 0);
                candidates = await RankCandidatesAsync(candidates);
            }
            Exception last = null;

            foreach (string url in candidates)
            {
                try
                {
                    ProbeResult probe = await ProbeAsync(url);

                    if (state != null && state.Total > 0 &&
                        state.Total != probe.Total)
                    {
                        TryDeleteCache(cache);
                        Directory.CreateDirectory(cache);
                        state = null;
                    }
                    if (state != null && info.Sha256.Length == 0 &&
                        state.Etag.Length > 0 && probe.Etag.Length > 0 &&
                        state.Etag != probe.Etag)
                    {
                        TryDeleteCache(cache);
                        Directory.CreateDirectory(cache);
                        state = null;
                    }

                    if (state == null)
                    {
                        state = new UpdateState();
                        state.Version = info.Version;
                        state.Sha = info.Sha256;
                        state.Total = probe.Total;
                        state.Parts = ComputeParts(probe.Total);
                        state.Etag = probe.Etag;
                    }

                    state.Total = probe.Total;
                    if (state.Sha.Length == 0) state.Sha = info.Sha256;
                    if (state.Etag.Length == 0) state.Etag = probe.Etag;
                    if (state.Parts <= 0) state.Parts = ComputeParts(state.Total);
                    SaveState(cache, state);

                    if (probe.Range)
                    {
                        bool rangeIgnored = false;
                        try
                        {
                            await MultiPartAsync(url, cache, state, report);
                        }
                        catch (RangeIgnoredException)
                        {
                            rangeIgnored = true;
                        }

                        if (rangeIgnored)
                        {
                            DeleteParts(cache, state.Parts);
                            await SingleAsync(url, zip, report);
                        }
                    }
                    else
                    {
                        await SingleAsync(url, zip, report);
                    }

                    try
                    {
                        VerifyZip(zip, state.Total, state.Sha);
                    }
                    catch (Exception)
                    {
                        TryDeleteCache(cache);
                        Directory.CreateDirectory(cache);
                        state = null;
                        throw;
                    }

                    state.Done = true;
                    SaveState(cache, state);
                    DeleteParts(cache, state.Parts);
                    ReportProgress(report, state.Total, state.Total);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Logs.Write("app-update: download attempt failed (" +
                               url + "): " + ex.Message);
                }
            }

            throw new InvalidOperationException(
                Lang.T("下载更新失败。", "下載更新失敗。",
                       "Could not download the update.",
                       "アップデートをダウンロードできません。") +
                (last != null ? " " + last.Message : ""));
        }

        private static List<string> Candidates(AppUpdateInfo info)
        {
            List<string> list = new List<string>();
            list.Add(info.DownloadUrl);

            // A download URL that came from the custom manifest is a deliberate
            // choice: use it first and do not race it against public mirrors.
            if (info.PresetDownload) return list;

            if (info.Sha256.Length == 64 && IsGithubRelease(info.DownloadUrl))
            {
                string[] mirrors = new string[]
                {
                    "https://ghproxy.net/",
                    "https://gh-proxy.com/",
                    "https://ghfast.top/",
                    "https://github.moeyy.xyz/"
                };
                for (int i = 0; i < mirrors.Length; i++)
                    list.Add(mirrors[i] + info.DownloadUrl);
            }
            return list;
        }

        private static bool IsGithubRelease(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return false;
            return uri.AbsolutePath.IndexOf("/releases/download/",
                                            StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private class RankedCandidate
        {
            public string Url;
            public double Speed;      // bytes per second, negative when failed
        }

        private const int SpeedSample = 262144;

        private static async Task<List<string>> RankCandidatesAsync(List<string> urls)
        {
            List<Task<RankedCandidate>> tasks = new List<Task<RankedCandidate>>();
            for (int i = 0; i < urls.Count; i++)
            {
                string target = urls[i];
                tasks.Add(Task.Run(() => MeasureAsync(target)));
            }

            RankedCandidate[] measured = await Task.WhenAll(tasks);
            List<RankedCandidate> good = new List<RankedCandidate>();
            List<RankedCandidate> bad = new List<RankedCandidate>();
            for (int i = 0; i < measured.Length; i++)
            {
                if (measured[i].Speed > 0) good.Add(measured[i]);
                else bad.Add(measured[i]);
            }

            good.Sort(delegate(RankedCandidate a, RankedCandidate b)
            {
                return b.Speed.CompareTo(a.Speed);
            });

            List<string> ordered = new List<string>();
            for (int i = 0; i < good.Count; i++)
            {
                ordered.Add(good[i].Url);
                Logs.Write("app-update: speed " +
                           (good[i].Speed / 1024.0).ToString("0") +
                           " KB/s " + good[i].Url);
            }
            for (int i = 0; i < bad.Count; i++)
            {
                ordered.Add(bad[i].Url);
                Logs.Write("app-update: speed failed " + bad[i].Url);
            }
            return ordered;
        }

        private static async Task<RankedCandidate> MeasureAsync(string url)
        {
            RankedCandidate result = new RankedCandidate();
            result.Url = url;
            result.Speed = -1;

            try
            {
                using (CancellationTokenSource cts =
                    new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                using (HttpRequestMessage request =
                    new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Range = new RangeHeaderValue(0, SpeedSample - 1);
                    Stopwatch watch = Stopwatch.StartNew();

                    using (HttpResponseMessage response = await Http.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        if (response.StatusCode != HttpStatusCode.OK &&
                            response.StatusCode != HttpStatusCode.PartialContent)
                        {
                            return result;
                        }

                        long bytes = 0;
                        bool zip = false;
                        using (Stream input = await response.Content.ReadAsStreamAsync())
                        {
                            byte[] buffer = new byte[65536];
                            while (bytes < SpeedSample)
                            {
                                int want = (int)Math.Min(
                                    (long)buffer.Length, SpeedSample - bytes);
                                int read = await input.ReadAsync(
                                    buffer, 0, want, cts.Token);
                                if (read <= 0) break;
                                if (bytes == 0 && read >= 2)
                                    zip = buffer[0] == 0x50 && buffer[1] == 0x4B;
                                bytes += read;
                            }
                        }

                        watch.Stop();
                        if (!zip || bytes < 1024) return result;
                        result.Speed = bytes / watch.Elapsed.TotalSeconds;
                    }
                }
            }
            catch (Exception)
            {
                result.Speed = -1;
            }
            return result;
        }

        private static async Task<ProbeResult> ProbeAsync(string url)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using (HttpResponseMessage response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        long total = 0;
                        if (response.Content.Headers.ContentRange != null &&
                            response.Content.Headers.ContentRange.Length.HasValue)
                        {
                            total = response.Content.Headers.ContentRange.Length.Value;
                        }
                        if (total <= 0)
                            throw new InvalidOperationException("No content length.");
                        return new ProbeResult
                        {
                            Total = total,
                            Range = true,
                            Etag = response.Headers.ETag != null
                                ? response.Headers.ETag.Tag.Trim('"') : ""
                        };
                    }

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        long total = response.Content.Headers.ContentLength ?? 0;
                        if (total <= 0)
                            throw new InvalidOperationException("No content length.");
                        return new ProbeResult
                        {
                            Total = total,
                            Range = false,
                            Etag = response.Headers.ETag != null
                                ? response.Headers.ETag.Tag.Trim('"') : ""
                        };
                    }

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                        response.Content.Headers.ContentRange != null &&
                        response.Content.Headers.ContentRange.Length.HasValue)
                    {
                        return new ProbeResult
                        {
                            Total = response.Content.Headers.ContentRange.Length.Value,
                            Range = true,
                            Etag = response.Headers.ETag != null
                                ? response.Headers.ETag.Tag.Trim('"') : ""
                        };
                    }

                    throw new InvalidOperationException(string.Format(
                        Lang.T("更新服务器返回错误（{0}）。",
                               "更新伺服器傳回錯誤（{0}）。",
                               "The update server returned an error ({0}).",
                               "更新サーバーがエラーを返しました（{0}）。"),
                        (int)response.StatusCode));
                }
            }
        }

        private static async Task MultiPartAsync(
            string url, string cache, UpdateState state, Action<string, int> report)
        {
            int parts = state.Parts;
            long partSize = (state.Total + parts - 1) / parts;
            long done = 0;

            long[] starts = new long[parts];
            long[] ends = new long[parts];
            string[] paths = new string[parts];
            int count = 0;

            for (int i = 0; i < parts; i++)
            {
                long start = (long)i * partSize;
                if (start >= state.Total) break;
                long end = Math.Min(start + partSize - 1, state.Total - 1);
                starts[count] = start;
                ends[count] = end;
                paths[count] = PartPath(cache, i);

                long expected = end - start + 1;
                long existing = 0;
                try
                {
                    if (File.Exists(paths[count]))
                        existing = new FileInfo(paths[count]).Length;
                }
                catch (Exception) { }
                if (existing > expected)
                {
                    try { File.Delete(paths[count]); }
                    catch (Exception) { }
                    existing = 0;
                }
                done += existing;
                count++;
            }

            ReportProgress(report, state.Total, done);

            CancellationTokenSource cts = new CancellationTokenSource();
            Task[] tasks = new Task[count];
            Action<long> add = delegate(long amount)
            {
                long now = Interlocked.Add(ref done, amount);
                ReportProgress(report, state.Total, now);
            };

            for (int i = 0; i < count; i++)
            {
                int index = i;
                long start = starts[i];
                long end = ends[i];
                string path = paths[i];
                tasks[i] = Task.Run(() => DownloadPartAsync(
                    url, path, start, end, cts.Token, add));
            }

            bool rangeIgnored = false;
            try
            {
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (RangeIgnoredException)
                {
                    cts.Cancel();
                    rangeIgnored = true;
                }

                if (rangeIgnored)
                {
                    try { await Task.WhenAll(tasks); }
                    catch (Exception) { }
                    throw new RangeIgnoredException();
                }
            }
            finally
            {
                cts.Dispose();
            }

            CombineParts(cache, state, partSize);
            ReportProgress(report, state.Total, state.Total);
        }

        private static async Task DownloadPartAsync(
            string url, string path, long start, long end,
            CancellationToken token, Action<long> add)
        {
            long expected = end - start + 1;
            long existing = 0;
            try
            {
                if (File.Exists(path)) existing = new FileInfo(path).Length;
            }
            catch (Exception) { }
            if (existing > expected)
            {
                try { File.Delete(path); }
                catch (Exception) { }
                existing = 0;
            }
            if (existing == expected) return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                long from = start + existing;
                Exception error = null;
                try
                {
                    using (HttpRequestMessage request =
                        new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.Range = new RangeHeaderValue(from, end);
                        using (HttpResponseMessage response = await Http.SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, token))
                        {
                            if (response.StatusCode == HttpStatusCode.PartialContent)
                            {
                                using (Stream input =
                                    await response.Content.ReadAsStreamAsync())
                                using (FileStream output = new FileStream(
                                    path, FileMode.Append, FileAccess.Write,
                                    FileShare.None, BufferSize,
                                    FileOptions.SequentialScan))
                                {
                                    byte[] buffer = new byte[BufferSize];
                                    int read;
                                    while ((read = await input.ReadAsync(
                                        buffer, 0, buffer.Length, token)) > 0)
                                    {
                                        await output.WriteAsync(
                                            buffer, 0, read, token);
                                        existing += read;
                                        if (add != null) add(read);
                                    }
                                }

                                if (existing == expected) return;
                                continue;
                            }

                            if (response.StatusCode == HttpStatusCode.OK)
                                throw new RangeIgnoredException();

                            if (response.StatusCode ==
                                HttpStatusCode.RequestedRangeNotSatisfiable)
                            {
                                if (existing == expected) return;
                                existing = 0;
                                try { File.Delete(path); }
                                catch (Exception) { }
                                continue;
                            }

                            throw new InvalidOperationException(string.Format(
                                Lang.T("更新服务器返回错误（{0}）。",
                                       "更新伺服器傳回錯誤（{0}）。",
                                       "The update server returned an error ({0}).",
                                       "更新サーバーがエラーを返しました（{0}）。"),
                                (int)response.StatusCode));
                        }
                    }
                }
                catch (RangeIgnoredException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { error = ex; }

                if (error == null) continue;
                if (attempt == 2)
                    throw new InvalidOperationException(error.Message, error);

                await Task.Delay(300 * (attempt + 1));
                try
                {
                    existing = File.Exists(path)
                        ? new FileInfo(path).Length : 0;
                }
                catch (Exception) { existing = 0; }
                if (existing > expected)
                {
                    try { File.Delete(path); }
                    catch (Exception) { }
                    existing = 0;
                }
            }

            if (existing != expected)
            {
                throw new InvalidOperationException(Lang.T(
                    "更新文件下载不完整。", "更新檔案下載不完整。",
                    "The update download is incomplete.",
                    "アップデートのダウンロードが不完全です。"));
            }
        }

        private static void CombineParts(string cache, UpdateState state, long partSize)
        {
            using (FileStream output = new FileStream(
                Path.Combine(cache, "update.zip"), FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize))
            {
                for (int i = 0; i < state.Parts; i++)
                {
                    long start = (long)i * partSize;
                    if (start >= state.Total) break;
                    long end = Math.Min(start + partSize - 1, state.Total - 1);
                    long expected = end - start + 1;
                    string part = PartPath(cache, i);
                    if (!File.Exists(part) ||
                        new FileInfo(part).Length != expected)
                    {
                        throw new InvalidOperationException(Lang.T(
                            "更新文件分片不完整。", "更新檔案分片不完整。",
                            "An update part is incomplete.",
                            "アップデートの分割ファイルが不完全です。"));
                    }

                    using (FileStream input = new FileStream(
                        part, FileMode.Open, FileAccess.Read, FileShare.Read,
                        BufferSize, FileOptions.SequentialScan))
                    {
                        byte[] buffer = new byte[BufferSize];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            output.Write(buffer, 0, read);
                    }
                }
            }
        }

        private static async Task SingleAsync(
            string url, string zip, Action<string, int> report)
        {
            using (HttpResponseMessage response = await Http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;

                using (Stream input = await response.Content.ReadAsStreamAsync())
                using (FileStream output = new FileStream(
                    zip, FileMode.Create, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[BufferSize];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(
                        buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        done += read;
                        if (report == null) continue;
                        if (total > 0)
                        {
                            ReportProgress(report, total, done);
                        }
                        else
                        {
                            report(string.Format(Lang.T(
                                "正在下载更新… {0} MB", "正在下載更新… {0} MB",
                                "Downloading update… {0} MB",
                                "アップデートをダウンロード中… {0} MB"),
                                (done / 1048576.0).ToString("0.0")), 0);
                        }
                    }
                    if (total > 0) ReportProgress(report, total, total);
                }
            }
        }

        private static void ReportProgress(
            Action<string, int> report, long total, long done)
        {
            if (report == null) return;
            if (total <= 0) return;
            int percent = (int)Math.Min(100L, done * 100 / total);
            if (done < total && percent >= 100) percent = 99;
            report(string.Format(Lang.T(
                "正在下载更新… {0}%", "正在下載更新… {0}%",
                "Downloading update… {0}%",
                "アップデートをダウンロード中… {0}%"), percent), percent);
        }

        private static void VerifyZip(string zip, long total, string sha)
        {
            long length = new FileInfo(zip).Length;
            if (total > 0 && length != total)
            {
                throw new InvalidOperationException(Lang.T(
                    "更新文件大小不正确。", "更新檔案大小不正確。",
                    "The update file has the wrong size.",
                    "アップデートファイルのサイズが正しくありません。"));
            }

            using (ZipFile.OpenRead(zip)) { }

            if (sha.Length == 64)
            {
                string actual = HashFile(zip);
                if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(Lang.T(
                        "更新文件校验失败。", "更新檔案校驗失敗。",
                        "The update file failed verification.",
                        "アップデートファイルの検証に失敗しました。"));
                }
            }
        }

        private static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream input = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan))
            {
                byte[] hash = sha.ComputeHash(input);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        private static void StartHelper(string script, string src, string dst,
                                        string exe, string work)
        {
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell)) powershell = "powershell.exe";

            int pid;
            using (Process me = Process.GetCurrentProcess())
                pid = me.Id;

            ProcessStartInfo psi = new ProcessStartInfo(powershell);
            psi.Arguments =
                "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) +
                " -waitPid " + pid +
                " -src " + Quote(src) +
                " -dst " + Quote(dst) +
                " -exe " + Quote(exe);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.WorkingDirectory = work;

            Process helper = Process.Start(psi);
            if (helper == null)
                throw new InvalidOperationException("Could not start the update helper.");
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static void Close(BusyForm busy)
        {
            if (busy == null) return;
            try
            {
                busy.Close();
                busy.Dispose();
            }
            catch (Exception) { }
        }

        private const string UpdateScript = @"param(
    [int]$waitPid,
    [string]$src,
    [string]$dst,
    [string]$exe
)

$log = Join-Path $env:TEMP 'RenPyDocs-update.log'
function Log($m) {
    try {
        Add-Content -LiteralPath $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $m)
    } catch { }
}

try { Wait-Process -Id $waitPid -ErrorAction SilentlyContinue } catch { }
Start-Sleep -Milliseconds 800
Log ('apply start: ' + $src + ' -> ' + $dst)

while ($src.EndsWith('\')) {
    $src = $src.Substring(0, $src.Length - 1)
}
$prefix = $src + '\'
$script:copyErrors = 0
try {
    Get-ChildItem -LiteralPath $src -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($prefix.Length)
        if ($rel -ieq 'site\docs.json') { return }
        $target = Join-Path $dst $rel
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        $ok = $false
        for ($i = 0; $i -lt 3; $i++) {
            try {
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force -ErrorAction Stop
                $ok = $true
                break
            } catch {
                Start-Sleep -Milliseconds 300
            }
        }
        if (-not $ok) {
            $script:copyErrors++
            Log ('copy failed: ' + $rel)
        }
    }
} catch {
    Log ('walk failed: ' + $_.Exception.Message)
}

Log ('copy errors: ' + $script:copyErrors)
try { Start-Process -FilePath $exe -WorkingDirectory $dst } catch { Log ('start failed: ' + $_.Exception.Message) }
try { Remove-Item -LiteralPath $src -Recurse -Force -ErrorAction SilentlyContinue } catch { }
";
    }

    // One place for the busy sheet, the "new version" prompt and the error
    // message, so the startup check (and a future manual entry point) behaves
    // the same way.
    public static class AppUpdateUI
    {
        public static async Task<bool> CheckAndShowAsync(
            IWin32Window owner, bool manual, string configuredUrl,
            Form appForm = null)
        {
            Form ownerForm = owner as Form;
            Form closeForm = appForm != null ? appForm : ownerForm;
            BusyForm busy = null;
            try
            {
                if (manual)
                {
                    busy = new BusyForm(Lang.T(
                        "正在检查软件更新…", "正在檢查軟體更新…",
                        "Checking for app updates…",
                        "アプリの更新を確認中…"));
                    if (ownerForm != null) busy.CenterOn(ownerForm);
                    busy.Show(owner);
                }

                AppUpdateInfo info = await AppUpdater.CheckAsync(configuredUrl);
                Close(busy);
                busy = null;
                Logs.Write("app-update: current=" + AppUpdater.CurrentVersion +
                           " remote=" + info.Version);

                if (ownerForm != null && ownerForm.IsDisposed) return false;

                if (!AppUpdater.IsNewer(info.Version, AppUpdater.CurrentVersion))
                {
                    if (manual)
                    {
                        using (InfoDialog dialog = new InfoDialog(
                            Lang.T("当前已是最新版本", "目前為最新版本",
                                   "Up to date", "最新バージョンです"),
                            Lang.T("当前版本：", "目前版本：",
                                   "Current version: ", "現在のバージョン：") +
                                AppUpdater.CurrentVersion,
                            null,
                            Lang.T("软件更新", "軟體更新",
                                   "App update", "アプリの更新")))
                        {
                            dialog.ShowDialog(owner);
                        }
                    }
                    return true;
                }

                string details =
                    Lang.T("当前版本：", "目前版本：",
                           "Current version: ", "現在のバージョン：") +
                    AppUpdater.CurrentVersion + "\r\n" +
                    Lang.T("最新版本：", "最新版本：",
                           "Latest version: ", "最新バージョン：") +
                    info.Version;

                bool hasDownload = info.DownloadUrl.Length > 0;
                if (hasDownload)
                {
                    details += "\r\n\r\n" + Lang.T(
                        "将下载并合并到当前目录，然后重启软件。",
                        "將下載並合併到目前目錄，然後重新啟動軟體。",
                        "The update will be downloaded, merged into this folder, then the app will restart.",
                        "アップデートをダウンロードして現在のフォルダーにマージし、アプリを再起動します。");
                }

                string extra = hasDownload
                    ? Lang.T("立即更新", "立即更新", "Update now", "今すぐ更新")
                    : (info.Url.Length > 0
                        ? Lang.T("下载页面", "下載頁面", "Download", "ダウンロード")
                        : null);

                using (InfoDialog dialog = new InfoDialog(
                    string.Format(Lang.T("发现新版本 {0}", "發現新版本 {0}",
                                         "Version {0} is available",
                                         "新しいバージョン {0} があります"),
                                  info.Version),
                    details, extra,
                    Lang.T("软件更新", "軟體更新", "App update", "アプリの更新")))
                {
                    if (dialog.ShowDialog(owner) == DialogResult.Retry)
                    {
                        if (hasDownload)
                        {
                            bool installed = await AppUpdateInstaller.InstallAsync(owner, info);
                            if (installed) CloseForRestart(closeForm, ownerForm);
                            return installed;
                        }
                        if (info.Url.Length > 0) OpenUrl(info.Url);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Close(busy);
                busy = null;
                Logs.Write("app-update: failed " + ex.Message);

                if (manual && (ownerForm == null || !ownerForm.IsDisposed))
                {
                    MessageBox.Show(owner,
                        Lang.T("无法检查软件更新。", "無法檢查軟體更新。",
                               "Could not check for app updates.",
                               "アプリの更新を確認できませんでした。") +
                        "\r\n\r\n" + ex.Message,
                        Lang.T("无法检查软件更新", "無法檢查軟體更新",
                               "Can't check for app updates",
                               "アプリの更新を確認できません"),
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return false;
            }
            finally
            {
                Close(busy);
            }
        }

        private static void Close(BusyForm busy)
        {
            if (busy == null) return;
            try
            {
                busy.Close();
                busy.Dispose();
            }
            catch (Exception) { }
        }

        // The settings sheet can start an update too. Close it first, then the
        // main window, so its modal loop does not swallow the app shutdown.
        private static void CloseForRestart(Form appForm, Form ownerForm)
        {
            Form app = appForm;
            if (app == null && ownerForm != null) app = ownerForm.Owner as Form;
            if (app == null) app = ownerForm;
            if (app == null || app.IsDisposed) return;

            Action closeApp = delegate
            {
                try { if (!app.IsDisposed) app.Close(); }
                catch (Exception) { }
            };

            if (ownerForm != null && ownerForm != app && !ownerForm.IsDisposed)
            {
                try
                {
                    app.BeginInvoke((Action)delegate
                    {
                        try { ownerForm.Close(); }
                        catch (Exception) { }
                        try { app.BeginInvoke(closeApp); }
                        catch (Exception) { closeApp(); }
                    });
                }
                catch (Exception) { closeApp(); }
            }
            else
            {
                try { app.BeginInvoke(closeApp); }
                catch (Exception) { closeApp(); }
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception) { }
        }
    }
}
