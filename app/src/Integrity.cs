using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RenPyDocs
{
    public class IntegrityProblem
    {
        public string Path;
        public string Reason;
        // short category for the summary: 图片损坏 / 文件缺失 / 页面异常 …
        public string Kind;
    }

    public class IntegrityReport
    {
        public string DocName = "";
        public int Files;
        public int Images;
        public readonly List<IntegrityProblem> Problems = new List<IntegrityProblem>();

        public void Bad(string path, string kind, string reason)
        {
            IntegrityProblem p = new IntegrityProblem();
            p.Path = path;
            p.Kind = kind;
            p.Reason = reason;
            Problems.Add(p);
        }

        // how many problems of each kind, e.g. { "图片损坏": 2, "页面异常": 1 }
        public Dictionary<string, int> Kinds()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (IntegrityProblem p in Problems)
            {
                string kind = string.IsNullOrEmpty(p.Kind) ? "其他" : p.Kind;
                int n;
                counts.TryGetValue(kind, out n);
                counts[kind] = n + 1;
            }
            return counts;
        }
    }

    // Walks a downloaded document and checks that every file can actually be
    // shown: a mirror that was filled by a challenge page looks complete in a
    // file listing (right names, right sizes within a few bytes) and is broken
    // in every page, so content is checked, not just existence.
    public static class Integrity
    {
        private static readonly Regex RefTag = new Regex(
            "(?:src|href)=\"([^\"#?]+)\"", RegexOptions.IgnoreCase);

        private static readonly Regex ChallengeMark = new Regex(
            "<script>\\s*function\\s+a\\(a\\)", RegexOptions.IgnoreCase);

        private static readonly string[] AssetExt = new string[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp",
            ".css", ".js", ".woff", ".woff2", ".ttf", ".eot"
        };

        public static IntegrityReport Check(string dir, string docName,
                                           string langMarker,
                                           Action<string, int> progress)
        {
            IntegrityReport report = new IntegrityReport();
            report.DocName = docName;

            if (!Directory.Exists(dir))
            {
                report.Bad(dir, "目录异常", "文档目录不存在");
                return report;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                report.Bad(dir, "目录异常", "无法读取目录：" + ex.Message);
                return report;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string rel = Relative(dir, file);
                if (progress != null && ((i % 8) == 0 || i == files.Length - 1))
                {
                    try { progress(rel, (i + 1) * 100 / Math.Max(files.Length, 1)); }
                    catch (Exception) { }
                }

                try
                {
                    long length = new FileInfo(file).Length;
                    if (length == 0)
                    {
                        report.Bad(rel, "空文件", "文件是 0 字节");
                        continue;
                    }

                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".html") CheckHtml(dir, file, rel, report);
                    else if (IsImage(ext))
                    {
                        report.Images++;
                        CheckImage(file, rel, ext, report);
                    }
                    else if (ext == ".css" || ext == ".js") CheckText(file, rel, report);
                    report.Files++;
                }
                catch (Exception ex)
                {
                    report.Bad(rel, "读取失败", "读取失败：" + ex.Message);
                }
            }

            // the index tells which document this is: a mirror filled with the
            // wrong language is "complete" in every other sense
            if (langMarker != null && langMarker.Length > 0)
            {
                string index = Path.Combine(dir, "index.html");
                if (File.Exists(index))
                {
                    string text = ReadText(index, 20000);
                    if (text.IndexOf(langMarker, StringComparison.Ordinal) < 0)
                        report.Bad("index.html", "内容不符", "内容不像" + langMarker + "（可能是别的文档）");
                }
            }

            return report;
        }

        private static bool IsImage(string ext)
        {
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" ||
                   ext == ".ico" || ext == ".webp" || ext == ".svg";
        }

        private static string Relative(string dir, string file)
        {
            string rel = file;
            if (rel.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(dir.Length);
            return rel.TrimStart('\\', '/').Replace('\\', '/');
        }

        private static string ReadText(string file, int max)
        {
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                char[] buffer = new char[max];
                int n = sr.Read(buffer, 0, max);
                return new string(buffer, 0, n);
            }
        }

        private static void CheckHtml(string dir, string file, string rel, IntegrityReport report)
        {
            long length = new FileInfo(file).Length;
            if (length < 600)
            {
                report.Bad(rel, "页面异常", "页面太小（" + length + " 字节），像是没下完");
                return;
            }

            string head = ReadText(file, 4000);
            string low = head.ToLowerInvariant();
            if (low.IndexOf("<html") < 0)
                report.Bad(rel, "页面异常", "不是 HTML 页面（可能是反爬校验页）");
            else if (ChallengeMark.IsMatch(head) && head.IndexOf("_0x", StringComparison.Ordinal) >= 0)
                report.Bad(rel, "页面异常", "内容是反爬校验脚本");
            else if (head.IndexOf("Ren'Py", StringComparison.OrdinalIgnoreCase) < 0 &&
                     head.IndexOf("Ren\u2019Py", StringComparison.OrdinalIgnoreCase) < 0)
                report.Bad(rel, "页面异常", "页面里没有 Ren'Py 字样");

            // every asset the page points at has to be on disk (once per page:
            // a picture used twice in the markup is still one problem)
            string text = ReadText(file, 400000);
            string pageDir = Path.GetDirectoryName(file);
            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in RefTag.Matches(text))
            {
                string target = m.Groups[1].Value;
                if (target.Length == 0) continue;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("//", StringComparison.Ordinal) ||
                    target.StartsWith("/", StringComparison.Ordinal) ||
                    target.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    continue;
                string ext = Path.GetExtension(target).ToLowerInvariant();
                if (Array.IndexOf(AssetExt, ext) < 0) continue;

                string full;
                try { full = Path.GetFullPath(Path.Combine(pageDir, target.Replace('/', '\\'))); }
                catch (Exception) { continue; }
                if (!full.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(full) && missing.Add(target))
                    report.Bad(rel, "文件缺失", "引用的文件缺失：" + target);
            }
        }

        private static void CheckImage(string file, string rel, string ext, IntegrityReport report)
        {
            byte[] head = new byte[16];
            int got;
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite))
                got = fs.Read(head, 0, head.Length);

            if (ext == ".svg")
            {
                string text = ReadText(file, 800).ToLowerInvariant();
                if (text.IndexOf("<svg") < 0 && text.IndexOf("<?xml") < 0)
                    report.Bad(rel, "图片损坏", "不是 SVG（可能是反爬校验页）");
                return;
            }

            string magic = MagicKind(head, got);
            if (magic == null)
            {
                report.Bad(rel, "图片损坏", "文件头不是图片（可能是反爬校验页）");
                return;
            }

            // the real test: can a decoder read it?
            if (ext == ".webp") return;      // GDI+ has no webp decoder
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite))
                using (Image img = Image.FromStream(fs))
                {
                    if (img.Width <= 0 || img.Height <= 0)
                        report.Bad(rel, "图片损坏", "图片尺寸为 0");
                }
            }
            catch (Exception ex)
            {
                report.Bad(rel, "图片损坏", "图片打不开：" + ex.GetType().Name);
            }
        }

        // "ok:<ext>" when the header matches that extension, "ok:other" for a
        // different but valid image, null when it is not an image at all
        private static string MagicKind(byte[] h, int got)
        {
            if (got >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47)
                return "ok:.png";
            if (got >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
                return "ok:.jpg";
            if (got >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8')
                return "ok:.gif";
            if (got >= 4 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0)
                return "ok:.ico";
            if (got >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' &&
                h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P')
                return "ok:.webp";
            return null;
        }

        private static void CheckText(string file, string rel, IntegrityReport report)
        {
            string head = ReadText(file, 400);
            string low = head.TrimStart().ToLowerInvariant();
            if (low.StartsWith("<") || ChallengeMark.IsMatch(head))
                report.Bad(rel, "资源异常", "内容是网页而不是脚本/样式表（可能是反爬校验页）");
        }
    }
}
