using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RenPyDocs
{
    // Headless check of the per-document tab bookkeeping: it never shows a
    // window, and it only touches tabs for the fake ids "test-a" / "test-b".
    static class SelfTest
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;

        static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethod(name, F);
            if (m == null) throw new Exception("no method " + name);
            return m.Invoke(o, args);
        }

        static void SetId(MainForm f, string id)
        {
            f.GetType().GetField("CurrentDocId", F).SetValue(f, id);
        }

        static string HomeUrl(MainForm f)
        {
            return (string)f.GetType().GetProperty("HomeUrl", F).GetValue(f, null);
        }

        static string TabsFile(MainForm f)
        {
            return (string)f.GetType().GetProperty("TabsFile", F).GetValue(f, null);
        }

        // 1x1 transparent PNG: small enough to embed, real enough for GDI+ to
        // decode it (the integrity check opens every image)
        static byte[] OnePixelPng()
        {
            return Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAE" +
                "hQGAhKmMIQAAAABJRU5ErkJggg==");
        }

        [STAThread]
        static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "site");
            string lines = "";
            int bad = 0;

            // second mode: start with a site where no document is on disk (the
            // "deleted the document you were reading" state) and check that the
            // window opens on the picker page with the tab strip hidden
            if (args.Length > 2 && args[2] == "empty")
            {
                try
                {
                    MainForm g = new MainForm(root);
                    TabStrip t = (TabStrip)g.GetType().GetField("tabs", F).GetValue(g);
                    Call(g, "LoadStartupTabs");
                    bool picker = t.Tabs.Count == 1 && t.Tabs[0].Url.EndsWith("/picker.html");
                    bool mode = (bool)g.GetType().GetField("pickerMode", F).GetValue(g);
                    lines += "empty site -> tabs=" + t.Tabs.Count +
                             " url=" + (t.Tabs.Count > 0 ? t.Tabs[0].Url : "-") +
                             " pickerMode=" + mode + " picker=" + picker + "\r\n";
                    if (!picker || !mode) bad++;
                    g.Dispose();
                }
                catch (Exception ex)
                {
                    lines += "EXCEPTION " + ex.GetType().Name + ": " + ex.Message + "\r\n";
                    bad++;
                }
                File.WriteAllText(args.Length > 1 ? args[1] : "selftest.txt",
                                  lines + "bad=" + bad + "\r\n");
                return bad;
            }

            // third mode: a document download must replace only its own folder
            if (args.Length > 2 && args[2] == "swap")
            {
                string app = Path.Combine(Path.GetTempPath(), "rpd-swap-" +
                                          Guid.NewGuid().ToString("N"));
                string site2 = Path.Combine(app, "site");
                string staging = Path.Combine(app, "_staging");
                try
                {
                    Directory.CreateDirectory(Path.Combine(site2, "zh-CN"));
                    Directory.CreateDirectory(Path.Combine(site2, "en"));
                    File.WriteAllText(Path.Combine(site2, "zh-CN", "index.html"), "old-zh");
                    File.WriteAllText(Path.Combine(site2, "en", "index.html"), "english");
                    File.WriteAllText(Path.Combine(site2, "picker.html"), "old-picker");

                    Directory.CreateDirectory(Path.Combine(staging, "zh-CN"));
                    Directory.CreateDirectory(Path.Combine(staging, "_static"));
                    File.WriteAllText(Path.Combine(staging, "zh-CN", "index.html"), "new-zh");
                    File.WriteAllText(Path.Combine(staging, "_static", "x.css"), "css");
                    File.WriteAllText(Path.Combine(staging, "picker.html"), "new-picker");
                    File.WriteAllText(Path.Combine(staging, "docs.json"), "[]");

                    SiteUpdater up = new SiteUpdater(app, delegate(string t, int p) { });
                    Call(up, "Swap", staging);

                    bool zhNew = File.ReadAllText(Path.Combine(site2, "zh-CN", "index.html")) == "new-zh";
                    bool enKept = File.Exists(Path.Combine(site2, "en", "index.html"));
                    bool pickerNew = File.ReadAllText(Path.Combine(site2, "picker.html")) == "new-picker";
                    bool cleaned = !Directory.Exists(staging);
                    bool css = File.Exists(Path.Combine(site2, "_static", "x.css"));

                    lines += "swap: zh=" + zhNew + " en kept=" + enKept + " picker=" + pickerNew +
                             " static=" + css + " staging gone=" + cleaned + "\r\n";
                    if (!zhNew || !enKept || !pickerNew || !css || !cleaned) bad++;
                }
                catch (Exception ex)
                {
                    lines += "EXCEPTION " + ex.GetType().Name + ": " + ex.Message + "\r\n";
                    bad++;
                }
                finally
                {
                    try { Directory.Delete(app, true); } catch (Exception) { }
                }
                File.WriteAllText(args.Length > 1 ? args[1] : "selftest.txt",
                                  lines + "bad=" + bad + "\r\n");
                return bad;
            }

            // fourth mode: the integrity check must spot the challenge pages that
            // once filled _images/ with 987-byte "<script>" files
            if (args.Length > 2 && args[2] == "check")
            {
                string tmp = Path.Combine(Path.GetTempPath(), "rpd-check-" +
                                          Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(Path.Combine(tmp, "img"));
                    File.WriteAllText(Path.Combine(tmp, "index.html"),
                        "<!DOCTYPE html><html><head><title>欢迎来到Ren'Py的参考文档 — Ren'Py 中文文档</title>" +
                        "</head><body>" + new string(' ', 700) + "中文</body></html>");
                    File.WriteAllText(Path.Combine(tmp, "page.html"),
                        "<!DOCTYPE html><html><head><title>x — Ren'Py 中文文档</title></head><body>" +
                        new string(' ', 700) + "<img src=\"img/missing.png\"></body></html>");
                    File.WriteAllBytes(Path.Combine(tmp, "img", "ok.png"), OnePixelPng());
                    File.WriteAllText(Path.Combine(tmp, "img", "fake.png"),
                        "<script>function a(a){function n(){for(var a={wQzOV:_0x649a(\"0x4\")}}}</script>");
                    byte[] broken = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G',
                                                 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
                    File.WriteAllBytes(Path.Combine(tmp, "img", "broken.png"), broken);
                    File.WriteAllText(Path.Combine(tmp, "style.css"),
                        "<script>function a(a){var x=_0x1234;}</script>");

                    IntegrityReport corrupt = Integrity.Check(tmp, "坏样本", "中文", null);
                    string seen = "";
                    foreach (IntegrityProblem p in corrupt.Problems) seen += p.Path + ";";
                    lines += "corrupt sample: files=" + corrupt.Files + " images=" + corrupt.Images +
                             " problems=" + corrupt.Problems.Count + " -> " + seen + "\r\n";
                    bool spottedFake = seen.Contains("img/fake.png");
                    bool spottedBroken = seen.Contains("img/broken.png");
                    bool spottedMissing = seen.Contains("page.html");
                    bool spottedCss = seen.Contains("style.css");
                    bool keptOk = !seen.Contains("img/ok.png");
                    if (corrupt.Problems.Count != 4 || !spottedFake || !spottedBroken ||
                        !spottedMissing || !spottedCss || !keptOk) bad++;

                    // the real document must come out clean
                    if (Directory.Exists(root))
                    {
                        IntegrityReport good = Integrity.Check(root, "真实文档", "中文", null);
                        lines += "real doc: files=" + good.Files + " images=" + good.Images +
                                 " problems=" + good.Problems.Count + "\r\n";
                        for (int i = 0; i < Math.Min(5, good.Problems.Count); i++)
                            lines += "  ! " + good.Problems[i].Path + " — " +
                                     good.Problems[i].Reason + "\r\n";
                        if (good.Problems.Count != 0) bad++;
                    }
                }
                catch (Exception ex)
                {
                    lines += "EXCEPTION " + ex.GetType().Name + ": " + ex.Message + "\r\n";
                    bad++;
                }
                finally
                {
                    try { Directory.Delete(tmp, true); } catch (Exception) { }
                }
                File.WriteAllText(args.Length > 1 ? args[1] : "selftest.txt",
                                  lines + "bad=" + bad + "\r\n");
                return bad;
            }

            try
            {
                MainForm f = new MainForm(root);
                TabStrip tabs = (TabStrip)f.GetType().GetField("tabs", F).GetValue(f);

                lines += "home zh: " + HomeUrl(f) + "\r\n";

                // two tabs for a fake document
                SetId(f, "test-a");
                tabs.Tabs.Clear();
                tabs.Tabs.Add(new TabInfo { Url = "https://renpy.localhost/zh-CN/index.html", Title = "a" });
                tabs.Tabs.Add(new TabInfo { Url = "https://renpy.localhost/zh-CN/gui.html", Title = "b" });
                tabs.Active = 1;
                Call(f, "SaveTabs");
                lines += "saved : " + Path.GetFileName(TabsFile(f)) + " exists=" + File.Exists(TabsFile(f)) + "\r\n";
                if (!File.Exists(TabsFile(f))) bad++;

                // another document has its own, empty file
                SetId(f, "test-b");
                bool b = (bool)Call(f, "LoadTabs");
                lines += "load b: " + b + " (expect False)\r\n";
                if (b) bad++;

                // going back restores exactly what was saved
                SetId(f, "test-a");
                bool a = (bool)Call(f, "LoadTabs");
                lines += "load a: " + a + " count=" + tabs.Tabs.Count + " active=" + tabs.Active +
                         " url=" + (tabs.Tabs.Count > 1 ? tabs.Tabs[1].Url : "-") + "\r\n";
                if (!a || tabs.Tabs.Count != 2 || tabs.Active != 1) bad++;

                // the English document has its own home page (when it is on disk;
                // otherwise the picker is the only page there is)
                SetId(f, "en");
                lines += "home en: " + HomeUrl(f) + "\r\n";
                bool enHere = File.Exists(Path.Combine(root, "en", "index.html"));
                if (enHere ? !HomeUrl(f).EndsWith("/en/index.html")
                           : !HomeUrl(f).EndsWith("/picker.html")) bad++;

                // a document that is not on disk: no home page, and the picker
                // tab set is never written over the saved tabs
                SetId(f, "test-a");
                string fake = Path.Combine(Path.GetDirectoryName(TabsFile(f)), "tabs-test-a.txt");
                if (File.Exists(fake)) File.Delete(fake);

                string home = HomeUrl(f);
                lines += "home gone doc: " + home + "\r\n";
                if (!home.EndsWith("/picker.html")) bad++;

                Call(f, "SetPickerTabs");
                string picker = tabs.Tabs.Count == 1 ? tabs.Tabs[0].Url : "-";
                lines += "picker tabs: count=" + tabs.Tabs.Count + " active=" + tabs.Active +
                         " url=" + picker + " title=" + (tabs.Tabs.Count > 0 ? tabs.Tabs[0].Title : "-") + "\r\n";
                if (tabs.Tabs.Count != 1 || tabs.Active != 0 ||
                    !picker.EndsWith("/picker.html") || tabs.Tabs[0].Title != "请选择文档") bad++;

                Call(f, "SaveTabs");
                lines += "save picker: exists=" + File.Exists(fake) + " (expect False)\r\n";
                if (File.Exists(fake)) bad++;

                // the real documents still have their home pages
                SetId(f, "zh-CN");
                lines += "home zh again: " + HomeUrl(f) + "\r\n";
                if (!HomeUrl(f).EndsWith("/zh-CN/index.html")) bad++;

                // ---- deleting the document that is open ---------------------
                // runs against a throw-away site folder, never the real one
                string tmp = Path.Combine(Path.GetTempPath(), "rpd-selftest-" +
                                          Guid.NewGuid().ToString("N"));
                string site2 = Path.Combine(tmp, "site");
                Directory.CreateDirectory(Path.Combine(site2, "test-d"));
                Directory.CreateDirectory(Path.Combine(site2, "test-e"));
                File.WriteAllText(Path.Combine(site2, "test-d", "index.html"), "<html></html>");
                File.WriteAllText(Path.Combine(site2, "test-e", "index.html"), "<html></html>");
                File.WriteAllText(Path.Combine(site2, "docs.json"),
                    "[{\"id\":\"test-d\",\"name\":\"D\",\"home\":\"test-d/index.html\",\"version\":\"1\",\"downloaded\":true}," +
                    "{\"id\":\"test-e\",\"name\":\"E\",\"home\":\"test-e/index.html\",\"version\":\"1\",\"downloaded\":true}]");

                FieldInfo rootField = f.GetType().GetField("root", F);
                rootField.SetValue(f, site2);
                Call(f, "LoadDocs");

                SetId(f, "test-d");
                tabs.Tabs.Clear();
                tabs.Tabs.Add(new TabInfo { Url = "https://renpy.localhost/test-d/index.html", Title = "d1" });
                tabs.Tabs.Add(new TabInfo { Url = "https://renpy.localhost/test-d/gui.html", Title = "d2" });
                tabs.Active = 0;
                Call(f, "SaveTabs");
                string dfile = Path.Combine(Path.GetDirectoryName(TabsFile(f)), "tabs-test-d.txt");
                bool savedBefore = File.Exists(dfile);
                lines += "doc on disk, saved=" + savedBefore + " (expect True)\r\n";
                if (!savedBefore) bad++;

                bool removed = (bool)Call(f, "DeleteDocFiles", "test-d");
                bool folderGone = !Directory.Exists(Path.Combine(site2, "test-d"));
                bool marked = File.ReadAllText(Path.Combine(site2, "docs.json"))
                                  .Contains("\"downloaded\": false");
                lines += "delete doc: ok=" + removed + " folder gone=" + folderGone +
                         " docs.json updated=" + marked + "\r\n";
                if (!removed || !folderGone || !marked) bad++;

                Call(f, "DeleteTabsForDoc", "test-d");
                lines += "tabs removed: " + !File.Exists(dfile) + " (expect True)\r\n";
                if (File.Exists(dfile)) bad++;

                string homeGone = HomeUrl(f);
                lines += "home after delete: " + homeGone + "\r\n";
                if (!homeGone.EndsWith("/picker.html")) bad++;

                Call(f, "SetPickerTabs");
                Call(f, "SaveTabs");
                lines += "picker did not recreate tabs: " + !File.Exists(dfile) + " (expect True)\r\n";
                if (File.Exists(dfile)) bad++;

                foreach (string id in new string[] { "test-d", "test-e" })
                {
                    string p = Path.Combine(Path.GetDirectoryName(TabsFile(f)), "tabs-" + id + ".txt");
                    if (File.Exists(p)) File.Delete(p);
                }
                try { Directory.Delete(tmp, true); } catch (Exception) { }
                rootField.SetValue(f, root);
                Call(f, "LoadDocs");
                SetId(f, "zh-CN");

                // the info dialog grows for a long body and can carry the extra
                // "校验完整性" style button
                using (InfoDialog plain = new InfoDialog("标题", "两行文本\r\n第二行"))
                {
                    lines += "info plain height=" + plain.ClientSize.Height + "\r\n";
                    if (plain.ClientSize.Height != 330) bad++;
                }
                using (InfoDialog grown = new InfoDialog("标题",
                    new string('长', 260) + "\r\n" + new string('文', 260), "重新下载"))
                {
                    bool extra = false;
                    foreach (Control c in grown.Controls)
                        if (c is Button && ((Button)c).DialogResult == DialogResult.Retry) extra = true;
                    lines += "info grown height=" + grown.ClientSize.Height +
                             " extra=" + extra + "\r\n";
                    if (grown.ClientSize.Height <= 330 || !extra) bad++;
                }

                // the integrity window needs a real 0..100 bar (a marquee one
                // never looks full, which is exactly what got reported)
                using (BusyForm fill = new BusyForm("正在校验…", true))
                {
                    bool continuous = false;
                    int max = 0;
                    foreach (Control c in fill.Controls)
                        if (c is ProgressBar)
                        {
                            continuous = ((ProgressBar)c).Style == ProgressBarStyle.Continuous;
                            max = ((ProgressBar)c).Maximum;
                        }
                    lines += "busy bar continuous=" + continuous + " max=" + max + "\r\n";
                    if (!continuous || max != 100) bad++;
                }
                using (BusyForm spin = new BusyForm("正在检查更新…"))
                {
                    bool marquee = false;
                    foreach (Control c in spin.Controls)
                        if (c is ProgressBar)
                            marquee = ((ProgressBar)c).Style == ProgressBarStyle.Marquee;
                    lines += "busy default marquee=" + marquee + "\r\n";
                    if (!marquee) bad++;
                }

                // the corruption report is a count + kinds, never a path list
                IntegrityReport planted = new IntegrityReport();
                planted.DocName = "测试文档";
                planted.Files = 210;
                planted.Bad("_images/a.png", "图片损坏", "文件头不是图片");
                planted.Bad("_images/b.png", "图片损坏", "图片打不开");
                planted.Bad("c.html", "页面异常", "页面太小");
                MethodInfo summary = f.GetType().GetMethod(
                    "IntegritySummary", BindingFlags.NonPublic | BindingFlags.Static);
                string text = (string)summary.Invoke(null, new object[] { planted });
                lines += "summary: " + text.Replace("\r\n", " | ") + "\r\n";
                if (!text.Contains("3 处问题") || !text.Contains("图片损坏：2 处") ||
                    !text.Contains("页面异常：1 处") || text.Contains(".png") ||
                    text.Contains(".html")) bad++;

                foreach (string id in new string[] { "test-a", "test-b" })
                {
                    string p = Path.Combine(Path.GetDirectoryName(TabsFile(f)), "tabs-" + id + ".txt");
                    if (File.Exists(p)) File.Delete(p);
                }
                f.Dispose();
            }
            catch (Exception ex)
            {
                lines += "EXCEPTION " + ex.GetType().Name + ": " + ex.Message + "\r\n" +
                         ex.StackTrace + "\r\n";
                bad++;
            }

            File.WriteAllText(args.Length > 1 ? args[1] : "selftest.txt", lines + "bad=" + bad + "\r\n");
            return bad;
        }
    }
}
