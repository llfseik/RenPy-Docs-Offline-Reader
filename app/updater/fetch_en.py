# -*- coding: utf-8 -*-
"""Mirror one Ren'Py documentation site (a Sphinx build) into a local folder.

    python updater/fetch_en.py site/en

Pages keep their plain names, assets keep their relative paths, cache-busting
queries are dropped so the pages also work straight from disk. The UI block is
injected later by build.py.

`crawl()` is also imported by download_doc.py, which adds progress reporting and
a pause switch on top of it and tells it which site to mirror (the English docs
on www.renpy.org or the Chinese ones on doc.renpy.cn).
"""
import concurrent.futures
import datetime
import email.utils
import html as html_mod
import io
import os
import re
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "https://www.renpy.org/doc/html/"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0 Safari/537.36")

# the docs hosts answer a fast client with an obfuscated challenge page (HTTP
# 200, `<script>function a(a){..._0x...}`) instead of the file; a hundred pages
# in a few seconds is enough to trip it
MIN_INTERVAL = 0.06
_rate_lock = threading.Lock()
_last_request = [0.0]

BINARY_EXT = (".png", ".jpg", ".jpeg", ".gif", ".ico", ".woff", ".woff2", ".ttf", ".eot")


class BlockedError(Exception):
    """The site answered with its browser check instead of the documents."""


def throttle():
    with _rate_lock:
        wait = MIN_INTERVAL - (time.time() - _last_request[0])
        if wait > 0:
            time.sleep(wait)
        _last_request[0] = time.time()


def looks_like_challenge(data):
    head = data[:400].lstrip()
    return head.startswith(b"<script>") and b"_0x" in data[:400]


def looks_like_html(data):
    head = data[:2000].lower()
    return b"<html" in head or b"<!doctype html" in head

ASSET_EXT = (".css", ".js", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico",
             ".woff", ".woff2", ".ttf", ".eot", ".map", ".txt")

PAGE_LINK = re.compile(r'href="([^"#?]+\.html)(?:[?#][^"]*)?"')
REF = re.compile(r'(?:href|src)="([^"]+)"')
CSS_URL = re.compile(r"url\(\s*['\"]?([^'\"()]+)['\"]?\s*\)")


def fetch(url, with_headers=False):
    throttle()
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    for attempt in range(3):
        try:
            resp = urllib.request.urlopen(req, timeout=45)
            data = resp.read()
            if with_headers:
                return data, dict(resp.headers.items())
            return data
        except (urllib.error.URLError, OSError):
            if attempt == 2:
                raise
            time.sleep(1.5 * (attempt + 1))


def local(name, base=BASE):
    """Relative local path for a same-directory link, else None."""
    if not name or name.startswith("#"):
        return None
    p = name.split("#")[0].split("?")[0]
    if p.startswith(base):
        p = p[len(base):]
    elif p.startswith("http://") or p.startswith("https://") or p.startswith("/"):
        return None
    if p.startswith("..") or p.startswith("_sources"):
        return None
    return p or None


def crawl(out, report=None, wait=None, base=BASE):
    """Download every page and asset into *out*.

    report(percent, text) is called as the download moves along, wait() is
    called between items and is expected to block while the user paused.
    *base* is the site root the pages are read from; everything is stored with
    base-relative names.

    The percentage only ever moves forward: the page total is seeded from the
    front page (Sphinx lists the whole toctree there), the assets are fetched
    once every page is in, and `note()` clamps anything that would go back.
    Raises BlockedError when the site answers with its browser check.
    """
    os.makedirs(out, exist_ok=True)

    best = [4]

    def note(pct, text):
        pct = max(best[0], min(99, int(pct)))
        best[0] = pct
        if report:
            try:
                report(pct, text)
            except Exception:               # noqa: BLE001 - progress is optional
                pass

    def tick():
        if wait:
            try:
                wait()
            except Exception:               # noqa: BLE001
                pass

    pages = {"index.html"}
    assets = set()
    done_pages = set()
    done_assets = set()
    errors = []         # assets that did not come down (logged, not fatal)
    page_errors = []    # pages that did not come down (fatal: the mirror would
                        # have holes and every link to them would 404)
    stamp = [""]        # the site's own Last-Modified, saved as build.txt

    def seed_pages():
        """Sphinx' own search index lists every document it built — including
        pages the navigation does not link to (the Chinese build has an orphan
        `atl` page, and only the index knows about it). Its response headers
        also say when the site was last rebuilt."""
        try:
            data, heads = fetch(base + "searchindex.js", True)
        except Exception as exc:            # noqa: BLE001 - best effort only
            errors.append("searchindex.js: %s" % exc)
            return set()
        stamp[0] = heads.get("Last-Modified") or heads.get("Date") or ""
        text = data.decode("utf-8", "replace")
        m = re.search(r'docnames"\s*:\s*\[([^\]]*)\]', text)
        if not m:
            return set()
        names = re.findall(r'"([^"\\]+)"', m.group(1))
        return set(n + ".html" for n in names if "/" not in n)

    def save(rel, data):
        path = os.path.join(out, rel.replace("/", os.sep))
        folder = os.path.dirname(path)
        if folder and not os.path.isdir(folder):
            os.makedirs(folder)
        with open(path, "wb") as f:
            f.write(data)

    def do_page(name):
        # 403 / 429 / 503 on a document means the host is refusing the fetch
        # (bot protection or a rate limit) while its static files still come
        # down — exactly what the reader's hidden browser is for, so hand it
        # over instead of crawling a mirror with every page missing
        raw = b""
        for attempt in range(4):
            try:
                raw = fetch(base + name)
            except urllib.error.HTTPError as exc:
                if exc.code not in (403, 429, 503):
                    raise
                if attempt == 3:
                    raise BlockedError(
                        "网站要求浏览器校验（页面 %s，HTTP %d）" % (name, exc.code))
                time.sleep(3 + 3 * attempt)
                continue
            if not looks_like_challenge(raw) and looks_like_html(raw):
                break
            time.sleep(2 + 2 * attempt)
        else:
            raise BlockedError("网站要求浏览器校验（页面 %s）" % name)
        html = raw.decode("utf-8", "replace")
        html = html.replace(base, "")
        html = re.sub(r'((?:href|src)="[^"]*?_static/[^"]*?)\?v=[0-9a-fA-F]+"', r'\1"', html)
        save(name, html.encode("utf-8"))
        found_pages, found_assets = set(), set()
        for m in PAGE_LINK.finditer(html):
            p = local(m.group(1), base)
            if p and "/" not in p:
                found_pages.add(p)
        for m in REF.finditer(html):
            p = local(m.group(1), base)
            if not p:
                continue
            if p.endswith(".html"):
                if "/" not in p:
                    found_pages.add(p)
            elif p.lower().endswith(ASSET_EXT):
                found_assets.add(p)
        return found_pages, found_assets

    def do_asset(rel):
        # an image must never be replaced by the challenge script: a mirror
        # full of 987-byte "<script>" files looks fine in a file listing and
        # broken in every page
        binary = rel.lower().endswith(BINARY_EXT)
        text = rel.lower().endswith((".css", ".js"))
        for attempt in range(4):
            try:
                data = fetch(base + urllib.parse.quote(rel, safe="/"))
            except urllib.error.HTTPError as exc:
                # referenced but not published (a font the theme asks for and
                # the site never shipped): not a broken download
                if exc.code == 404:
                    return
                raise
            bad = looks_like_challenge(data)
            if not bad and (binary or text) and data.lstrip()[:1] == b"<":
                bad = True
            if not bad:
                save(rel, data)
                return
            time.sleep(2 + 2 * attempt)
        raise BlockedError("网站要求浏览器校验（资源 %s）" % rel)

    # the front page carries the whole Sphinx toctree, so the page count is
    # right from the start (asking for it later is what made the bar jump back
    # to 5% after reaching 60%)
    host = urllib.parse.urlparse(base).netloc
    note(4, "正在连接 %s…" % host)
    pages |= seed_pages()

    # the build stamp the app compares against ("本地构建" / "线上构建"): the
    # site's `_static/documentation_options.js` is years stale, so a stamp taken
    # from the search index response is the only honest one. With no header at
    # all, "now" is the safe value: it never reports an update that predates
    # this download.
    when = None
    if stamp[0]:
        try:
            when = email.utils.parsedate_to_datetime(stamp[0])
        except Exception:                   # noqa: BLE001 - keep the fallback
            when = None
    if when is None:
        when = datetime.datetime.now(datetime.timezone.utc)
    elif when.tzinfo is None:
        when = when.replace(tzinfo=datetime.timezone.utc)
    when = when.astimezone(datetime.timezone.utc)
    with io.open(os.path.join(out, "build.txt"), "w", encoding="ascii") as f:
        f.write(when.strftime("%Y-%m-%dT%H:%M:%SZ") + u"\n")

    # the home page is not optional — build.py needs it to make the helper
    # pages. A single failed request here used to crawl everything else, then
    # die inside the builder with "FileNotFoundError: index.html", which looked
    # like a build bug instead of a flaky download.
    for attempt in range(3):
        try:
            found_pages, found_assets = do_page("index.html")
            done_pages.add("index.html")
            pages |= found_pages
            assets |= found_assets
            break
        except BlockedError:
            raise
        except Exception as exc:            # noqa: BLE001 - retried here
            page_errors.append("index.html: %s" % exc)
            if attempt < 2:
                time.sleep(2 + 2 * attempt)
    if not os.path.exists(os.path.join(out, "index.html")):
        raise Exception("没有抓到首页 index.html，请重试")

    # the site's browser check answers with a challenge page instead of the
    # documents: stop here rather than mirroring the challenge as if it were
    # the documentation
    if os.path.exists(os.path.join(out, "index.html")):
        head = io.open(os.path.join(out, "index.html"), encoding="utf-8",
                       errors="replace").read(4000)
        m = re.search(r"<title>(.*?)</title>", head, re.S)
        # the title arrives HTML-escaped on some builds ("Ren&#39;Py"), so
        # unescape before looking for the project name
        title = html_mod.unescape(m.group(1)) if m else ""
        # the Chinese pages spell it Ren’Py with a curly apostrophe
        flat = title.replace(u"\u2019", "'").replace(u"\u2018", "'")
        if "安全检测" in title or "Ren'Py" not in flat:
            raise BlockedError("网站要求浏览器校验（%s）" % (title.strip()[:40] or "无标题"))
    total = max(len(pages), len(done_pages), 1)
    tick()
    note(5, "正在下载页面 %d/%d…" % (len(done_pages), total))

    # six pages at a time: progress moves as each one lands, and the pause
    # switch is checked between batches (a fetched page cannot be recalled)
    done_count = len(done_pages)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        while True:
            todo = sorted(pages - done_pages)
            if not todo:
                break
            futs = {}
            for name in todo[:6]:
                done_pages.add(name)
                futs[pool.submit(do_page, name)] = name
            for fut in concurrent.futures.as_completed(futs):
                name = futs[fut]
                try:
                    found_pages, found_assets = fut.result()
                except BlockedError:
                    raise
                except Exception as exc:    # noqa: BLE001 - report and go on
                    page_errors.append("%s: %s" % (name, exc))
                else:
                    pages |= found_pages
                    assets |= found_assets
                done_count += 1
                total = max(total, len(pages))
                note(5 + 55 * done_count // total,
                     "正在下载页面 %d/%d…" % (done_count, total))
            print("pages %d/%d" % (done_count, total), flush=True)
            tick()

    # every page is in, so the asset list is final: one pass with a fixed total
    todo = sorted(assets - done_assets)
    for i, rel in enumerate(todo):
        done_assets.add(rel)
        try:
            do_asset(rel)
        except BlockedError:
            raise
        except Exception as exc:            # noqa: BLE001
            errors.append("%s: %s" % (rel, exc))
        tick()
        note(60 + 28 * (i + 1) // max(len(todo), 1),
             "正在下载资源 %d/%d…" % (i + 1, len(todo)))
    print("assets %d" % len(done_assets), flush=True)

    # fonts and images that are only referenced from the stylesheets
    css_refs = set()
    for base_dir, _, files in os.walk(out):
        for fn in files:
            if not fn.lower().endswith(".css"):
                continue
            path = os.path.join(base_dir, fn)
            rel_dir = os.path.relpath(base_dir, out).replace(os.sep, "/")
            text = io.open(path, encoding="utf-8", errors="replace").read()
            for m in CSS_URL.finditer(text):
                u = m.group(1).split("?")[0].split("#")[0].strip()
                if not u or u.startswith("data:") or u.startswith("http") or u.startswith("/"):
                    continue
                ref = os.path.normpath(os.path.join(rel_dir, u)).replace(os.sep, "/")
                if ref.startswith(".."):
                    continue
                css_refs.add(ref)
    refs = sorted(css_refs - done_assets)
    for i, rel in enumerate(refs):
        tick()
        if not os.path.exists(os.path.join(out, rel.replace("/", os.sep))):
            try:
                do_asset(rel)
                done_assets.add(rel)
            except BlockedError:
                raise
            except Exception as exc:        # noqa: BLE001
                errors.append("%s: %s" % (rel, exc))
        note(88 + 2 * (i + 1) // max(len(refs), 1),
             "正在补全字体与图片 %d/%d…" % (i + 1, len(refs)))
    note(90, "资源下载完成…")
    print("css refs %d" % len(css_refs), flush=True)

    ver = ""
    opts = os.path.join(out, "_static", "documentation_options.js")
    if os.path.exists(opts):
        m = re.search(r"VERSION\s*:\s*'([^']+)'",
                      io.open(opts, encoding="utf-8", errors="replace").read())
        if m:
            ver = m.group(1)
    io.open(os.path.join(out, "version.txt"), "w", encoding="utf-8",
            newline="\n").write((ver or "unknown") + "\n")

    if errors:
        print("errors %d" % len(errors), flush=True)
        for e in errors[:10]:
            print("  ! %s" % e, flush=True)
    if page_errors:
        print("page errors %d" % len(page_errors), flush=True)
        for e in page_errors[:10]:
            print("  !! %s" % e, flush=True)

    return {"pages": len(done_pages), "assets": len(done_assets),
            "version": ver, "errors": errors, "page_errors": page_errors}


def main():
    # optional third argument: the site to mirror (defaults to the English one)
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join("site", "en")
    base = sys.argv[2] if len(sys.argv) > 2 else BASE
    info = crawl(out, base=base)
    print("pages:", info["pages"], "assets:", info["assets"],
          "version:", info["version"] or "?", "errors:", len(info["errors"]))
    for e in info["errors"][:10]:
        print("  !", e)


if __name__ == "__main__":
    main()
