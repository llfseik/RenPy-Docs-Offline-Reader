# -*- coding: utf-8 -*-
"""Download one document and build it, detached from the reader window.

    python updater/download_doc.py <site dir> <doc id> <state dir> <python exe>

Progress is written to <state dir>/download-<id>.txt as three lines:
    percent
    status text
    running | done | failed

Pausing is a file: while <state dir>/download-<id>.pause exists the download
waits. The reader can be closed at any time — this process keeps going, and the
next start of the reader picks the progress file up again.
"""
import io
import json
import os
import subprocess
import sys
import time
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import fetch_en                                    # noqa: E402

# Which site serves which document. `docs.json` (written by build.py) carries
# the same table; this is only the fallback for a copy that has no catalogue
# yet. The Chinese hosts may answer with their browser check, and only then does
# the reader fall back to its own hidden browser.
DOCS = {
    "zh-CN": ("https://doc.renpy.cn/zh-CN/", "doc.renpy.cn"),
    "zh-TW": ("https://doc.renpy.cn/zh-TW/", "doc.renpy.cn"),
    "en": ("https://www.renpy.org/doc/html/", "www.renpy.org"),
    "ja": ("http://ja.renpy.org/doc/html/", "ja.renpy.org"),
    "en-nightly": ("https://nightly.renpy.org/current-8/doc/", "nightly.renpy.org"),
    "en-nightly-fix": ("https://nightly.renpy.org/current-8-fix/doc/", "nightly.renpy.org"),
}


def site_for(site, doc):
    """The mirror URL for *doc*, preferring the catalogue build.py writes."""
    try:
        meta = json.load(io.open(os.path.join(site, "docs.json"), encoding="utf-8"))
        for entry in meta:
            if entry.get("id") == doc and entry.get("site"):
                base = entry["site"]
                return base if base.endswith("/") else base + "/"
    except Exception:                               # noqa: BLE001 - fallback below
        pass
    spec = DOCS.get(doc)
    return spec[0] if spec else None


def main():
    if len(sys.argv) < 5:
        return 2

    site = sys.argv[1]
    doc = sys.argv[2]
    state = sys.argv[3]
    python = sys.argv[4]

    out = os.path.join(site, doc)
    report_path = os.path.join(state, "download-%s.txt" % doc)
    pause_path = os.path.join(state, "download-%s.pause" % doc)

    # the reader is a GUI app: give this process a real log file to print into
    try:
        log = io.open(os.path.join(state, "download-%s.log" % doc),
                      "w", encoding="utf-8", buffering=1)
        sys.stdout = log
        sys.stderr = log
    except OSError:
        pass

    def report(pct, text, state_name="running"):
        tmp = report_path + ".tmp"
        with io.open(tmp, "w", encoding="utf-8", newline="\n") as f:
            f.write("%d\n%s\n%s\n" % (pct, text, state_name))
        for _ in range(20):
            try:
                os.replace(tmp, report_path)
                return
            except OSError:
                time.sleep(0.05)

    def wait():
        while os.path.exists(pause_path):
            time.sleep(0.25)

    try:
        base = site_for(site, doc)
        if not base:
            report(0, "不支持这份文档", "failed")
            return 1
        host = urllib.parse.urlparse(base).netloc

        report(2, "正在连接 %s…" % host)
        info = fetch_en.crawl(out, lambda p, t: report(p, t), wait, base)
        if info["pages"] < 2:
            report(0, "没有下载到页面，请检查网络", "failed")
            return 1
        # a mirror with holes in it is worse than no mirror: a missing page is
        # still linked from the sidebar, so retry instead of shipping it
        if info.get("page_errors"):
            report(0, "有 %d 个页面没抓到，请重试" % len(info["page_errors"]), "failed")
            return 1

        report(92, "正在重建索引…")
        builder = os.path.join(HERE, "build.py")
        template = os.path.join(HERE, "search_body.html")
        app_dir = os.path.dirname(HERE)
        # keep the builder's own output: when it fails, "构建失败" alone tells
        # nobody why, and the traceback is the only useful thing we have
        proc = subprocess.run(
            [python, builder, site, template, doc],
            cwd=app_dir,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace")
        output = (proc.stdout or "").strip()
        if output:
            print(output)
        if proc.returncode != 0:
            report(0, "构建失败（详见 download-%s.log）" % doc, "failed")
            return 1

        report(100, "下载完成", "done")
        return 0
    except fetch_en.BlockedError as exc:
        # the reader knows what to do with this text: fetch it with its own
        # browser instead (src/Program.cs, DownloadWithBrowser)
        report(0, str(exc), "failed")
        return 9
    except Exception as exc:                       # noqa: BLE001
        report(0, "下载失败：%s" % exc, "failed")
        return 1
    finally:
        try:
            if os.path.exists(pause_path):
                os.remove(pause_path)
        except OSError:
            pass


if __name__ == "__main__":
    sys.exit(main())
