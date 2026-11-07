# Rebuilds the desktop app: pages -> RenPyDocs.exe -> shortcut.
# The site mirror in .\site is the source of truth; there is no web build any more.
#
#   powershell -ExecutionPolicy Bypass -File tools\build.ps1
$ErrorActionPreference = "Stop"
$app = Split-Path -Parent $PSScriptRoot
Set-Location $app

$env:PYTHONIOENCODING = "utf-8"
Write-Host "== pages =="
# every document that is on disk gets its own page rewrite; zh-CN is always
# present, the others only after they have been downloaded
$langs = @("zh-CN", "zh-TW", "en", "ja", "en-nightly", "en-nightly-fix")
$built = 0
foreach ($lang in $langs) {
  if ($lang -ne "zh-CN" -and -not (Test-Path (Join-Path $app "site\$lang\index.html"))) { continue }
  python (Join-Path $app "updater\build.py") (Join-Path $app "site") (Join-Path $app "updater\search_body.html") $lang
  if ($LASTEXITCODE -ne 0) { throw "site build failed: $lang" }
  $built++
}
if ($built -eq 0) { throw "site build failed" }

Write-Host "== exe =="
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

# Ask the running reader to close normally first: OnFormClosing saves the open
# tabs, the active tab and the scroll positions. Force-killing it here is what
# used to make the next start come back on an old tab set.
$running = Get-Process RenPyDocs -ErrorAction SilentlyContinue
if ($running) {
  Write-Host "closing the running reader (saving tabs)…"
  if (-not ("BuildWin" -as [type])) {
    Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class BuildWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static void Close(uint pid) {
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      StringBuilder t = new StringBuilder(256); GetWindowText(h, t, 256);
      if (t.ToString().Length > 0) PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
      return true;
    }, IntPtr.Zero);
  }
}
"@
  }
  foreach ($p in $running) { [BuildWin]::Close([uint32]$p.Id) }
  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 250
    if (-not (Get-Process RenPyDocs -ErrorAction SilentlyContinue)) { break }
  }
  Get-Process RenPyDocs -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
}

$refs = @(
  "/r:System.dll",
  "/r:System.Drawing.dll",
  "/r:System.Windows.Forms.dll",
  "/r:System.Net.Http.dll",
  "/r:$app\Microsoft.Web.WebView2.Core.dll",
  "/r:$app\Microsoft.Web.WebView2.WinForms.dll"
)
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$app\app.ico" `
  "/out:$app\RenPyDocs.exe" $refs (Join-Path $app "src\Program.cs") `
  (Join-Path $app "src\TabStrip.cs") (Join-Path $app "src\Updater.cs") `
  (Join-Path $app "src\Integrity.cs") (Join-Path $app "src\AppUpdate.cs")
if ($LASTEXITCODE -ne 0) { throw "csc failed" }

Write-Host "== shortcut =="
$exe = Join-Path $app "RenPyDocs.exe"
$lnkName = "Ren'Py 中文文档.lnk"
Get-ChildItem $app -Filter "*.lnk" | Remove-Item -Force -ErrorAction SilentlyContinue
$shell = New-Object -ComObject WScript.Shell

function New-AppShortcut($path) {
  $lnk = $shell.CreateShortcut($path)
  $lnk.TargetPath = $exe
  $lnk.WorkingDirectory = $app
  $lnk.IconLocation = "$exe,0"
  $lnk.Description = "Ren'Py 中文文档（离线）"
  $lnk.Save()
}

# 应用目录内一份
New-AppShortcut (Join-Path $app $lnkName)

# 桌面一份：目录被移动或改名后，桌面的 .lnk 仍指向旧路径，图标会变成空白；
# 每次构建都重写一遍，路径和图标就自动跟上当前目录。
$desk = [Environment]::GetFolderPath("Desktop")
if ($desk -and (Test-Path $desk)) {
  New-AppShortcut (Join-Path $desk $lnkName)
  Write-Host "desktop shortcut -> $desk"
}

# 任务栏固定项：只在用户已经固定过时才修，避免凭空造出一个固定图标
$pin = Join-Path $env:APPDATA "Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\$lnkName"
if (Test-Path $pin) {
  New-AppShortcut $pin
  Write-Host "taskbar pin repaired"
}

# 让资源管理器丢弃旧图标缓存（同一路径反复覆盖 exe 时图标会一直空着）
& "$env:WINDIR\System32\ie4uinit.exe" -show 2>$null

$kb = [math]::Round((Get-Item $exe).Length / 1KB)
$mb = [math]::Round((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "built: $exe ($kb KB exe, $mb MB total)"
