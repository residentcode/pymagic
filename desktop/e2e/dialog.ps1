# A dialog of a process, for an e2e check: waits for a visible window titled -Title of process -ProcessId (or of its children), moves it
# off-screen without the focus at once (so it is not in front of the person), draws it into -Shot (a PNG) with PrintWindow, prints its
# texts, and clicks the button whose text is -Click (a posted BM_CLICK). Run: powershell -File dialog.ps1 -Title "MCP: commit" -Click Commit
param([string]$Title, [string]$Click = '', [string]$Shot = '', [int]$TimeoutSeconds = 30)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class D {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Text(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, 1024); return s.ToString(); }
    public static string Class(IntPtr h) { var s = new StringBuilder(128); GetClassName(h, s, 128); return s.ToString(); }
    public static IntPtr Find(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { if (IsWindowVisible(h) && Text(h) == title) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    public static List<IntPtr> Children(IntPtr p) { var l = new List<IntPtr>(); EnumChildWindows(p, (h, x) => { l.Add(h); return true; }, IntPtr.Zero); return l; }
}
'@
# the dialog's size in real pixels, as PrintWindow draws it (a scaled screen would cut the picture)
[D]::SetProcessDPIAware() | Out-Null
$h = [IntPtr]::Zero
$until = (Get-Date).AddSeconds($TimeoutSeconds)
while ($h -eq [IntPtr]::Zero -and (Get-Date) -lt $until) { $h = [D]::Find($Title); if ($h -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 40 } }
if ($h -eq [IntPtr]::Zero) { "no window titled $Title"; exit 2 }
$r = New-Object D+RECT
[D]::GetWindowRect($h, [ref]$r) | Out-Null
"found at $($r.Left),$($r.Top)"
# SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
[D]::SetWindowPos($h, [IntPtr]::Zero, -4000, 50, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010) | Out-Null
Start-Sleep -Milliseconds 500
foreach ($c in [D]::Children($h)) { $t = [D]::Text($c); if ($t -and [D]::IsWindowVisible($c)) { "  $([D]::Class($c)) '$t'" } }
if ($Shot) {
    [D]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [D]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "shot: $Shot"
}
if ($Click) {
    $button = [D]::Children($h) | Where-Object { [D]::Class($_) -eq 'Button' -and (([D]::Text($_) -replace '&', '') -eq $Click) } | Select-Object -First 1
    if (-not $button) { "no button $Click"; exit 3 }
    [D]::PostMessage($button, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # BM_CLICK
    "clicked $Click"
}
