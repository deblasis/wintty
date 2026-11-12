<#
.SYNOPSIS
Plain Ctrl+letter must follow the active keyboard layout, not the
QWERTY position of the physical key.

.DESCRIPTION
Issue #1451: under Colemak, Ctrl+D at the Colemak D key (physical G
position) sent 0x07 -- the C0 of the QWERTY letter at that position
(Ctrl+G) -- so every Ctrl+letter shortcut misfired under Colemak,
Dvorak and any non-QWERTY layout. The fix recovers the layout letter
from the WM_CHAR byte Windows itself translated, so the C0 the pty
receives matches the letter the user's layout produces. Colemak puts
d on G, s on D, y on O and o on the ; key (VK_OEM_1); Dvorak puts
s on the ; key and e on D.

Each scenario launches a fresh app whose pane runs a tiny echo program
that optionally pushes a keyboard protocol into the terminal (the same
bytes a clean Neovim sends) and then prints every byte the pty delivers
as hex. The seam injects one Ctrl+letter key press through the pane's
real WM_KEYDOWN + WM_CHAR handlers (terminal-key). The injected pair is
exactly what one press on that physical key delivers under the named
layout: the vk is the physical QWERTY position's virtual key, the char
the byte Windows' layout translation produces. No OS layout is
installed or needed.

Zero OS input is synthesized: the key travels the in-process handlers
(SendKeyCore + HandleCharacter) the framework events drive, and the pane
program reads its console in raw VT mode. Nothing is focused, nothing is
sent to another window.

Exits 0 on pass, 2 on a product finding, 1 when the harness could not
run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # Run only the scenarios whose name matches this wildcard.
    [string]$Only = '*'
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $ExePath)) {
    Write-Host "HARNESS: missing exe: $ExePath"
    exit 1
}
# The pane's -File argument must stay unquoted (see the config comment in
# Invoke-LayoutScenario), so the staging path cannot carry a space.
# The app is launched with its own exe directory as cwd (Start-SeamAppProcess
# in lib/seam-client.ps1), and the pane resolves the staged -File path from
# there, so the staged path baked into the config must be absolute.
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
if ($OutDir -match ' ') {
    Write-Host "HARNESS: OutDir must not contain a space: $OutDir"
    exit 1
}




# The pane program: a raw-mode console reader that pushes a protocol and
# hex-echoes everything the pty delivers. Staged per scenario with the
# protocol bytes baked in (a pane child gets a fresh logon environment,
# not the app's, so the push cannot travel through an env var), and
# written in PowerShell so no interpreter beyond the one running this
# harness is needed (a packaged Store python cannot be spawned by a pane
# at all).
$PwshExe = Join-Path $PSHOME 'pwsh.exe'
function Stage-KeyEcho([string]$Push) {
    $path = Join-Path $OutDir ("keyecho-$([Math]::Abs($Push.GetHashCode())).ps1")
    if (Test-Path $path) { return $path }
    $body = @'
param()
$ErrorActionPreference = 'Stop'
Add-Type -Namespace NativeKernel32 -Name Raw -MemberDefinition @"
[DllImport("kernel32", SetLastError=true)] public static extern IntPtr GetStdHandle(int nStdHandle);
[DllImport("kernel32", SetLastError=true)] public static extern bool GetConsoleMode(IntPtr h, out uint m);
[DllImport("kernel32", SetLastError=true)] public static extern bool SetConsoleMode(IntPtr h, uint m);
"@

# Console to raw VT input so the CSI sequences the terminal writes into the
# pty arrive byte for byte, not pre-digested by conhost. A pane that hands
# the child a pipe instead fails GetConsoleMode: raw=0, and the stream below
# still reads the bytes verbatim.
$raw = 0
try {
    $h = [NativeKernel32.Raw]::GetStdHandle(-10)
    $mode = [uint32]0
    if ([NativeKernel32.Raw]::GetConsoleMode($h, [ref]$mode)) {
        $ENABLE_VIRTUAL_TERMINAL_INPUT = [uint32]0x0200
        if ([NativeKernel32.Raw]::SetConsoleMode($h, $ENABLE_VIRTUAL_TERMINAL_INPUT)) { $raw = 1 }
    }
} catch { }

$push = '__PUSH__'
if ($push) {
    # The harness bakes VT bytes here with \xNN escapes (PowerShell
    # quoting); parse them to real bytes rather than trusting a regex
    # replacement scriptblock, whose argument passing has moved between
    # PowerShell releases.
    $bytes = New-Object System.Collections.Generic.List[byte]
    $i = 0
    while ($i -lt $push.Length) {
        if ($i + 3 -lt $push.Length -and $push[$i] -eq '\' -and $push[$i + 1] -eq 'x') {
            $two = [string]$push[$i + 2] + [string]$push[$i + 3]
            $bytes.Add([Convert]::ToByte($two, 16))
            $i += 4
        }
        else {
            $bytes.Add([byte][char]$push[$i])
            $i++
        }
    }
    $arr = $bytes.ToArray()
    [Console]::OpenStandardOutput().Write($arr, 0, $arr.Length)
}

[Console]::Write("READY raw=$raw`n")

$stdin = [Console]::OpenStandardInput()
$buf = New-Object byte[] 4096
while ($true) {
    $n = $stdin.Read($buf, 0, $buf.Length)
    if ($n -le 0) { break }
    $sb = [Text.StringBuilder]::new($n * 3)
    for ($i = 0; $i -lt $n; $i++) {
        if ($i -gt 0) { [void]$sb.Append(' ') }
        [void]$sb.Append($buf[$i].ToString('x2'))
    }
    [Console]::Write('K[' + $sb.ToString() + "]`n")
}
'@
    $body = $body.Replace('__PUSH__', $Push)
    $body | Set-Content $path -Encoding utf8
    return $path
}

$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Name, [bool]$Ok, [string]$Class, [string]$Detail) {
    $script:Results.Add([pscustomobject]@{ name = $Name; ok = $Ok; class = $Class; detail = $Detail })
    if ($Ok) { Write-Host "PASS $Name $Detail" -ForegroundColor Green }
    else { Write-Host "FAIL $Name [$Class] $Detail" -ForegroundColor Red }
}

function Seam($s, [hashtable]$Command) { Invoke-SeamCommand $s $Command }

function Get-Screen([object]$s) { (Seam $s @{ op = 'screen-text'; index = 0; leaf = -1 }).text }

# Poll the pane's grid until it contains $Pattern or the timeout passes.
function Wait-Screen([object]$s, [string]$Pattern, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $text = Get-Screen $s
        if ($text -match [regex]::Escape($Pattern)) { return $text }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

# The K[...] hex markers the echo program prints, in arrival order.
function Get-KeyHex([string]$Screen) {
    @([regex]::Matches($Screen, 'K\[([0-9a-f ]*)\]') | ForEach-Object { $_.Groups[1].Value.Trim() })
}

# The press must lead the chunk exactly; under event-reporting flags a
# release CSI u may ride in the same chunk behind it, which is the same
# key's own release and fine. Anything else (an unexpected sequence
# leading, extra text after) is a mismatch.
function Test-KeyHex([string]$Hex, [string]$Expect) {
    if ($Hex -eq $Expect) { return $true }
    if (-not $Hex.StartsWith($Expect + ' ')) { return $false }
    return $Hex.Substring($Expect.Length + 1).StartsWith('1b ')
}

$configHead = @"
windows-single-instance = false
window-save-state = never
"@

# One fresh app on the echo program, one key per assertion. $Key is
# @{ vk = 0x47; ctrl = $true; char = [char]0x04 } -- the vk the KeyDown
# half delivers (the physical QWERTY position's virtual key) and the
# char the WM_CHAR half delivers under the named layout -- the exact
# pair one press of that physical key produces. $Expect is the hex the
# pty must receive for that key's PRESS (a release follows under
# event-reporting flags; the press is the insertion a TUI acts on).
function Invoke-LayoutScenario {
    param(
        [Parameter(Mandatory)][string]$Name,
        # The protocol bytes the pane program writes before reading, '' for none.
        [string]$Push = '',
        [Parameter(Mandatory)][hashtable]$Key,
        [Parameter(Mandatory)][string]$Expect
    )
    if ($Name -notlike $Only) { return }
    Write-Host "== $Name" -ForegroundColor Cyan
    $s = $null
    try {
        $staged = Stage-KeyEcho $Push
        # The -File path is staged under $OutDir unquoted: the config
        # command parser mishandles a second quoted token (a "path"
        # after the first), so the staged path carries no spaces (the
        # guard at the top of this file enforces that for $OutDir).
        $config = $configHead + "`ncommand = `"$PwshExe`" -NoProfile -File $staged"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput -PrivateStateBase
        $readyText = Wait-Screen $s 'READY' 30
        if (-not $readyText) {
            Add-Result $Name $false 'harness' ('the echo program never became ready; screen: ' +
                ((Get-Screen $s) -replace "`n", ' | '))
            return
        }
        # raw=0 means the pane child reads a pipe rather than a console
        # (the ConPTY bypass path): bytes arrive verbatim either way, and
        # the K[] markers below prove it. raw=1 is the console path.
        if ($readyText -notlike '*READY raw=*') {
            Add-Result $Name $false 'harness' 'the echo program is up but printed no raw flag'
            return
        }
        # @() around every Get-KeyHex: one marker unrolls to a string
        # on assignment, and indexing a string returns a character.
        $before = @(Get-KeyHex (Get-Screen $s)).Count
        [void](Seam $s @{
            op    = 'terminal-key'
            vk    = $Key.vk
            ctrl  = [bool]$Key.ctrl
            alt   = [bool]$Key.alt
            char  = $Key.char
        })
        # Wait until the marker for this key lands (press, and the
        # release under event-reporting flags).
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $hex = $null
        while ([DateTime]::UtcNow -lt $deadline) {
            $all = @(Get-KeyHex (Get-Screen $s))
            if ($all.Count -gt $before) { $hex = $all[$before]; break }
            Start-Sleep -Milliseconds 200
        }
        if ($null -eq $hex) {
            Add-Result $Name $false 'harness' 'the pane echoed nothing for the key'
            return
        }
        if (-not (Test-KeyHex $hex $Expect)) {
            $screen = Get-Screen $s
            Add-Result $Name $false 'product' ("expected [{0}] got [{1}]; screen: {2}" -f
                $Expect, $hex,
                ((($screen -split "`n") | Where-Object { $_ -match '\S' } | Select-Object -First 5) -join ' | '))
            return
        }
        Add-Result $Name $true 'product' "[$Expect]"
    }
    finally {
        if ($s) { Stop-SeamSession $s }
    }
}

# No protocol (legacy): a plain Ctrl+letter goes to the pty as its C0
# byte -- the C0 of the LAYOUT letter, not the QWERTY position's letter
# (#1451). Each vk is the virtual key of the physical key pressed (its
# QWERTY-position name), each char the byte Windows' translation of the
# named layout delivers for that same physical key: together, exactly
# the pair one real press under that layout produces.
Invoke-LayoutScenario 'colemak-ctrl-d' '' @{ vk = 0x47; ctrl = $true; char = [char]0x04 } '04'
Invoke-LayoutScenario 'colemak-ctrl-o' '' @{ vk = 0xBA; ctrl = $true; char = [char]0x0F } '0f'
Invoke-LayoutScenario 'colemak-ctrl-s' '' @{ vk = 0x44; ctrl = $true; char = [char]0x13 } '13'
Invoke-LayoutScenario 'colemak-ctrl-y' '' @{ vk = 0x4F; ctrl = $true; char = [char]0x19 } '19'
# Dvorak pairs, same shape. S lives on the ; key (VK_OEM_1).
Invoke-LayoutScenario 'dvorak-ctrl-s' '' @{ vk = 0xBA; ctrl = $true; char = [char]0x13 } '13'
Invoke-LayoutScenario 'dvorak-ctrl-e' '' @{ vk = 0x44; ctrl = $true; char = [char]0x05 } '05'
# QWERTY unchanged: the fix must not move the letters QWERTY already
# got right.
Invoke-LayoutScenario 'qwerty-ctrl-d' '' @{ vk = 0x44; ctrl = $true; char = [char]0x04 } '04'
Invoke-LayoutScenario 'qwerty-ctrl-c' '' @{ vk = 0x43; ctrl = $true; char = [char]0x03 } '03'
# Ctrl+Alt: outside the fix's gate (the WM_CHAR byte is 0x07, not the
# letter C0), pinning the current AltGr-ish path.
Invoke-LayoutScenario 'colemak-ctrl-alt-d' '' @{ vk = 0x47; ctrl = $true; alt = $true; char = [char]0x07 } '1b 07'

# kitty disambiguate (flag 1): Ctrl+letter becomes CSI u with the
# layout letter's codepoint (100 = 'd'), not the scancode key of the
# QWERTY position (103 = 'g' for vk 0x47).
Invoke-LayoutScenario 'kitty-colemak-ctrl-d' '\x1b[>1u' @{ vk = 0x47; ctrl = $true; char = [char]0x04 } '1b 5b 31 30 30 3b 35 75'
# Colemak O lives on the ; key (scancode 0x27), CSI 111;5u.
Invoke-LayoutScenario 'kitty-colemak-ctrl-o' '\x1b[>1u' @{ vk = 0xBA; ctrl = $true; char = [char]0x0F } '1b 5b 31 31 31 3b 35 75'

# modifyOtherKeys level 2: CSI 27;5;100~ -- the same xterm encoding
# GTK- and macOS-shaped events already produce in mode 2, so the fixed
# key matches what those clients send.
Invoke-LayoutScenario 'mok2-colemak-ctrl-d' '\x1b[>4;2m' @{ vk = 0x47; ctrl = $true; char = [char]0x04 } '1b 5b 32 37 3b 35 3b 31 30 30 7e'

$failed = @($script:Results | Where-Object { -not $_.ok })
$failed | ForEach-Object { Write-Host ("FAILED {0} [{1}] {2}" -f $_.name, $_.class, $_.detail) }
$script:Results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'results.json')
if ($failed.Count -gt 0) {
    Write-Host ("FAILED ({0} of {1} scenarios)" -f $failed.Count, $script:Results.Count)
    foreach ($f in $failed) { if ($f.class -eq 'product') { exit 2 } }
    exit 1
}
Write-Host ("ALL PASS ({0} scenarios)" -f $script:Results.Count)
exit 0
