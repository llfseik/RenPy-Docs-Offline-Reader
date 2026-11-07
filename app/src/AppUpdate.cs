using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
    }

    // Checks the project's GitHub releases by default. The settings field can
    // also point at another GitHub repository or at a JSON manifest:
    //   {"version":"1.0.1","url":"https://example.com/download"}
    // A plain text file whose first version-looking number is 1.0.1 works too.
    public static class AppUpdater
    {
        // Bump this for every published build; the GitHub tag is compared to it.
        public const string CurrentVersion = "1.0.0";
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
            return info;
        }

        private static AppUpdateInfo ParseManifest(string text, Uri baseUri)
        {
            string body = (text ?? "").Trim();
            string version = "";
            string url = "";
            string name = "";

            if (body.Length > 0 && body[0] == '{')
            {
                version = Field(body, "version");
                if (version.Length == 0) version = Field(body, "tag_name");
                url = Field(body, "url");
                if (url.Length == 0) url = Field(body, "html_url");
                name = Field(body, "name");
            }

            if (CleanVersion(version).Length == 0)
            {
                Match match = Regex.Match(body, @"\b\d+\.\d+(?:\.\d+)*\b");
                if (match.Success) version = match.Value;
            }
            version = CleanVersion(version);

            if (url.Length > 0)
            {
                Uri resolved;
                if (!Uri.TryCreate(baseUri, url, out resolved) ||
                    (resolved.Scheme != Uri.UriSchemeHttp &&
                     resolved.Scheme != Uri.UriSchemeHttps))
                {
                    url = "";
                }
                else
                {
                    url = resolved.ToString();
                }
            }
            if (url.Length == 0) url = baseUri.ToString();

            AppUpdateInfo info = new AppUpdateInfo();
            info.Version = version;
            info.Url = url;
            info.Name = name;
            return info;
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

                string extra = info.Url.Length > 0
                    ? Lang.T("下载页面", "下載頁面",
                             "Download", "ダウンロード")
                    : null;

                using (InfoDialog dialog = new InfoDialog(
                    string.Format(Lang.T("发现新版本 {0}", "發現新版本 {0}",
                                         "Version {0} is available",
                                         "新しいバージョン {0} があります"),
                                  info.Version),
                    details, extra,
                    Lang.T("软件更新", "軟體更新", "App update", "アプリの更新")))
                {
                    if (dialog.ShowDialog(owner) == DialogResult.Retry &&
                        info.Url.Length > 0)
                    {
                        OpenUrl(info.Url);
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
