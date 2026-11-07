using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
    }

    // Checks the project's GitHub releases by default. The settings field can
    // also point at another GitHub repository or at a JSON manifest:
    //   {"version":"1.0.1","url":"https://example.com/download"}
    // A plain text file whose first version-looking number is 1.0.1 works too.
    public static class AppUpdater
    {
        // Bump this for every published build; the GitHub tag is compared to it.
        public const string CurrentVersion = "1.0.1";
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
            return info;
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
                string zip = Path.Combine(work, "update.zip");
                string payload = Path.Combine(work, "payload");

                await DownloadAsync(info.DownloadUrl, zip, delegate(string text, int percent)
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

        private static async Task DownloadAsync(
            string url, string path, Action<string, int> report)
        {
            using (HttpResponseMessage response = await Http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;

                using (Stream input = await response.Content.ReadAsStreamAsync())
                using (FileStream output = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(
                        buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read);
                        done += read;
                        if (report != null)
                        {
                            if (total > 0)
                            {
                                int percent = (int)Math.Min(99L, done * 100 / total);
                                report(string.Format(Lang.T(
                                    "正在下载更新… {0}%", "正在下載更新… {0}%",
                                    "Downloading update… {0}%",
                                    "アップデートをダウンロード中… {0}%"), percent),
                                    percent);
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
                    }
                }
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
            IWin32Window owner, bool manual, string configuredUrl)
        {
            Form ownerForm = owner as Form;
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
                            if (installed && ownerForm != null && !ownerForm.IsDisposed)
                            {
                                ownerForm.BeginInvoke((Action)delegate
                                {
                                    try { ownerForm.Close(); }
                                    catch (Exception) { }
                                });
                            }
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
