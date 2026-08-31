#requires -Version 7
# Task #32 Phase 1: the UIA-tree-loss discrimination battery. Run AT the
# HARVEST_MISS moment as a FRESH PROCESS (its own UIA client, no shared
# cache with the harness). Emits the (a)-(c) evidence block to stdout.
param(
    [Parameter(Mandatory)][int]$ProcId,
    [Parameter(Mandatory)][long]$Hwnd,
    [string]$OutFile = ''
)
$ErrorActionPreference = 'Continue'
$lines = [System.Collections.Generic.List[string]]::new()
function Note([string]$s) { $lines.Add($s) }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Wnd {
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetFocus();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern bool GetThreadTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
}
'@

$h = [IntPtr]$Hwnd
Note "=== UIA-LOSS BATTERY pid=$ProcId hwnd=$Hwnd t=$(Get-Date -Format o) ==="

# (a) window state
Note ("(a) IsWindow=" + [Wnd]::IsWindow($h) +
    " Visible=" + [Wnd]::IsWindowVisible($h) +
    " Iconic=" + [Wnd]::IsIconic($h) +
    " Enabled=" + [Wnd]::IsWindowEnabled($h))
$fg = [Wnd]::GetForegroundWindow()
$sb = [System.Text.StringBuilder]::new(256)
[void][Wnd]::GetWindowText($fg, $sb, 256)
Note ("(a) foreground=0x" + $fg.ToString('X') + " title='" + $sb.ToString() + "'" +
    " fgIsTarget=" + ($fg -eq $h))

# (a2) the process's window threads + CPU snapshot (twice, 400ms apart)
$p = Get-Process -Id $ProcId -ErrorAction SilentlyContinue
if ($p) {
    $t1 = $p.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Milliseconds 400
    $p.Refresh()
    $t2 = $p.TotalProcessorTime.TotalMilliseconds
    Note ("(c) proc cpuDelta400ms=" + [math]::Round($t2 - $t1, 1) + "ms threads=" + $p.Threads.Count)
    $hot = $p.Threads | Sort-Object TotalProcessorTime -Descending | Select-Object -First 3
    foreach ($th in $hot) {
        Note ("(c) hot tid=" + $th.Id + " cpu=" + [math]::Round($th.TotalProcessorTime.TotalMilliseconds, 0) +
            "ms state=" + $th.ThreadState.WaitReason)
    }
} else {
    Note "(c) PROCESS GONE"
}

# (c2) UI-thread responsiveness: SendMessageTimeout WM_GETTEXT with a 1s
# cap -- a hung/busy UI thread lets this time out.
$wmGetText = 0x000D
$smtoAbortIfHung = 0x0002
$timeoutMs = 1000
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$result = [IntPtr]::Zero
$ok = [Wnd]::SendMessageTimeout($h, $wmGetText, [IntPtr]::Zero, [IntPtr]::Zero,
    $smtoAbortIfHung, $timeoutMs, [ref]$result)
$sw.Stop()
Note ("(c2) SendMessageTimeout ok=" + $ok + " ms=" + $sw.ElapsedMilliseconds +
    " (0/timeout or 1/answered)")

# (b) FRESH-PROCESS UIA query: this process IS the fresh client (spawned
# separately by the harness). Walk the window for NavView by AutomationId.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
$nav = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
$sw2.Stop()
if ($null -ne $nav) {
    $r = $nav.Current.BoundingRectangle
    Note ("(b) FRESH NavView FOUND rect=" + $r.Width + "x" + $r.Height +
        " walk=" + $sw2.ElapsedMilliseconds + "ms")
    $items = $nav.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)))
    Note ("(b) FRESH NavView ListItems=" + $items.Count)
} else {
    Note ("(b) FRESH NavView NOT FOUND walk=" + $sw2.ElapsedMilliseconds + "ms")
    # The broader tree: what DOES the fresh walk see at the top?
    $kids = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    Note ("(b) root children=" + $kids.Count)
}

# Flush incrementally: the (a)/(c)/(c2) answers go to disk BEFORE the UIA
# walk, so a hang there cannot swallow them. The walk's results are
# appended after.
$out = if ($OutFile) { $OutFile } else { '' }
if ($out) { [System.IO.File]::WriteAllLines($out, $lines) }
$lines.Clear()

# (b) FRESH-PROCESS UIA query: this process IS the fresh client (spawned
# separately by the harness). Walk the window for NavView by AutomationId.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
$nav = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
$sw2.Stop()
if ($null -ne $nav) {
    $r = $nav.Current.BoundingRectangle
    $lines.Add("(b) FRESH NavView FOUND rect=" + $r.Width + "x" + $r.Height +
        " walk=" + $sw2.ElapsedMilliseconds + "ms")
    $items = $nav.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)))
    $lines.Add("(b) FRESH NavView ListItems=" + $items.Count)
} else {
    $lines.Add("(b) FRESH NavView NOT FOUND walk=" + $sw2.ElapsedMilliseconds + "ms")
    $kids = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $lines.Add("(b) root children=" + $kids.Count)
}

# Flush the walk's answers too.
if ($out) {
    $all = [System.IO.File]::ReadAllLines($out)
    [System.IO.File]::WriteAllLines($out, $all + $lines)
} else { $lines | ForEach-Object { Write-Host $_ } }

Note "=== END BATTERY ==="
