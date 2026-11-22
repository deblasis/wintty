#requires -Version 7
<#
.SYNOPSIS
IME commit bytes: what the pty receives for committed composition text
and for non-ASCII characters typed on a keyboard layout, per keyboard
protocol.

.DESCRIPTION
Issue #1468: committed composition text left through the paste path, so
under bracketed paste (mode 2004) every syllable arrived as ESC[200~ ..
ESC[201~ and a receiver that handles pastes off its key path (Claude Code
behind a multiplexer) dropped it. The fix sends a composition commit as a
key press carrying the text with no physical key, the way GTK's im-commit
does -- but a character typed directly on a layout (ue on de-DE, ef on
ru-RU, AltGr chars) must keep the text exit, because its own KeyDown is
still pending in libghostty and a keyless press would flush it textless:
under the kitty protocol that is a CSI u for a key the user never pressed,
ahead of the character.

Each scenario launches a fresh app whose pane runs a raw-VT echo that
pushes one protocol and hex-prints every byte it reads. The seam drives
the real composition lifecycle (ime-started / ime-set-text / ime-changed /
ime-ended, the settle timer, ime-commit) and the real key and character
halves (terminal-key, ime-char) -- no OS input is synthesized. The layout
scenarios model a plain-layout key exactly as WinUI delivers it: the
KeyDown press first (no text), then the character.

Exits 0 on pass, 2 on a product finding, 1 when the harness could not
run and nothing is known about the product.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    # The build's AUMID, for the coexistence guard, when it cannot read it
    # itself.
    [string]$AumId = '',
    # Run only the scenarios whose name matches this wildcard.
    [string]$Only = '*'
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$PwshExe = Join-Path $PSHOME 'pwsh.exe'

function Stage-Echo([string]$Push) {
    $path = Join-Path $OutDir ("echo-$([Math]::Abs($Push.GetHashCode())).ps1")
    if (Test-Path $path) { return $path }
    $body = @'
$ErrorActionPreference = 'Stop'
Add-Type -Namespace NK -Name Raw -MemberDefinition @"
[DllImport("kernel32", SetLastError=true)] public static extern IntPtr GetStdHandle(int n);
[DllImport("kernel32", SetLastError=true)] public static extern bool GetConsoleMode(IntPtr h, out uint m);
[DllImport("kernel32", SetLastError=true)] public static extern bool SetConsoleMode(IntPtr h, uint m);
[DllImport("kernel32", SetLastError=true)] public static extern bool SetConsoleCP(uint cp);
"@
# conhost hands ReadFile bytes in the input codepage; UTF-8 so non-ASCII arrives as sent.
[void][NK.Raw]::SetConsoleCP(65001)
$raw = 0
try { $h = [NK.Raw]::GetStdHandle(-10); $m = [uint32]0
      if ([NK.Raw]::GetConsoleMode($h, [ref]$m)) { if ([NK.Raw]::SetConsoleMode($h, [uint32]0x0200)) { $raw = 1 } } } catch { }
$push = '__PUSH__'
if ($push) {
    $b = New-Object System.Collections.Generic.List[byte]; $i = 0
    while ($i -lt $push.Length) {
        if ($i + 3 -lt $push.Length -and $push[$i] -eq '\' -and $push[$i+1] -eq 'x') { $b.Add([Convert]::ToByte([string]$push[$i+2] + [string]$push[$i+3], 16)); $i += 4 }
        else { $b.Add([byte][char]$push[$i]); $i++ }
    }
    $a = $b.ToArray(); [Console]::OpenStandardOutput().Write($a, 0, $a.Length)
}
[Console]::Write("READY raw=$raw`n")
$in = [Console]::OpenStandardInput(); $buf = New-Object byte[] 4096; $seq = 0
while ($true) {
    $n = $in.Read($buf, 0, $buf.Length); if ($n -le 0) { break }
    for ($o = 0; $o -lt $n; $o += 12) {
        $e = [Math]::Min($n, $o + 12); $sb = [Text.StringBuilder]::new()
        for ($i = $o; $i -lt $e; $i++) { if ($i -gt $o) { [void]$sb.Append(' ') }; [void]$sb.Append($buf[$i].ToString('x2')) }
        $seq++; [Console]::Write(('K{0:d4}[' -f $seq) + $sb.ToString() + "]`n")
    }
}
'@
    $body.Replace('__PUSH__', $Push) | Set-Content $path -Encoding utf8
    return $path
}

function Seam($s, [hashtable]$c) { Invoke-SeamCommand $s $c }
function Screen($s) { (Seam $s @{ op = 'screen-text'; index = 0; leaf = -1 }).text }
function KLines([string]$t) {
    @([regex]::Matches($t, 'K(\d{4})\[([0-9a-f ]*)\]') | ForEach-Object {
        [pscustomobject]@{ n = [int]$_.Groups[1].Value; hex = $_.Groups[2].Value.Trim() } })
}
function Wait-Screen($s, [string]$p, [int]$sec) {
    $d = [DateTime]::UtcNow.AddSeconds($sec)
    while ([DateTime]::UtcNow -lt $d) {
        # The surface comes up after the seam pipe; until then screen-text errors.
        $t = try { Screen $s } catch { if ("$_" -like '*no live surface*') { '' } else { throw } }
        if ($t -like "*$p*") { return $t }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

$script:Results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Name, [bool]$Ok, [string]$Class, [string]$Detail) {
    $script:Results.Add([pscustomobject]@{ name = $Name; ok = $Ok; class = $Class; detail = $Detail })
    if ($Ok) { Write-Host "PASS $Name $Detail" -ForegroundColor Green }
    else { Write-Host "FAIL $Name [$Class] $Detail" -ForegroundColor Red }
}

# One scenario: $Steps is the seam script, $Expect the hex the pty must
# receive for it (bytes in order, spaces between). A trailing release the
# event-reporting flags produce is folded into the same marker line, so the
# expectation matches a prefix: the whole hex, or the hex followed by more
# bytes that start with an ESC (a release CSI u).
function Test-Seq([string]$Hex, [string]$Expect) {
    if ($Hex -eq $Expect) { return $true }
    if (-not $Hex.StartsWith($Expect + ' ')) { return $false }
    return $Hex.Substring($Expect.Length + 1).StartsWith('1b ')
}

function Invoke-Scenario([string]$Name, [string]$Push, [array]$Steps, [array]$Expect) {
    if ($Name -notlike $Only) { return }
    Write-Host "== $Name" -ForegroundColor Cyan
    $s = $null
    try {
        $staged = Stage-Echo $Push
        $config = "windows-single-instance = false`nwindow-save-state = never`ncommand = `"$PwshExe`" -NoProfile -File $staged"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput -PrivateStateBase
        $ready = Wait-Screen $s 'READY' 40
        if (-not $ready) {
            $scr = try { Screen $s } catch { "screen-text error: $_" }
            Add-Result $Name $false 'harness' ('the echo program never became ready; screen: ' + ($scr -replace "`n", ' | '))
            return
        }
        # The intro (READY, the pushed protocol's replies) can still be
        # arriving; wait it out so it is never counted as a step's bytes.
        $settle = [DateTime]::UtcNow.AddSeconds(3)
        while ([DateTime]::UtcNow -lt $settle) {
            $a = @(KLines (Screen $s) | Measure-Object -Property n -Maximum).Maximum
            Start-Sleep -Milliseconds 600
            $b = @(KLines (Screen $s) | Measure-Object -Property n -Maximum).Maximum
            if ($a -eq $b) { break }
        }
        $last = (@(KLines (Screen $s)) | Measure-Object -Property n -Maximum).Maximum
        if ($null -eq $last) { $last = 0 }
        for ($i = 0; $i -lt $Steps.Count; $i++) {
            foreach ($step in $Steps[$i]) {
                if ($step.ContainsKey('sleep')) { Start-Sleep -Milliseconds $step.sleep; continue }
                [void](Seam $s $step)
            }
            # One step group can land as several console reads (the settle
            # writes, then a held key flushes), and a buffered press writes
            # nothing until the text that flushes it arrives. So: wait for
            # the first marker, then keep collecting until the pane goes
            # quiet, and judge the group's joined bytes.
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            $parts = [System.Collections.Generic.List[string]]::new()
            $quiet = 0
            while ([DateTime]::UtcNow -lt $deadline) {
                $lines = @(KLines (Screen $s) | Where-Object { $_.n -gt $last } | Sort-Object n)
                if ($lines.Count -gt 0) {
                    foreach ($l in $lines) { $parts.Add($l.hex) }
                    $last = $lines[-1].n
                    $quiet = 0
                } elseif ($parts.Count -gt 0) {
                    $quiet += 250
                    if ($quiet -ge 750) { break }
                }
                Start-Sleep -Milliseconds 250
            }
            if ($parts.Count -eq 0) { Add-Result $Name $false 'harness' ("step {0}: the pane echoed nothing" -f $i); return }
            $hex = ($parts -join ' ').Trim()
            if (-not (Test-Seq $hex $Expect[$i])) {
                Add-Result $Name $false 'product' ("step {0}: expected [{1}] got [{2}]" -f $i, $Expect[$i], $hex)
                return
            }
        }
        Add-Result $Name $true 'product' (($Expect | ForEach-Object { "[$_]" }) -join ' ')
    }
    finally {
        if ($s) {
            try { $trace = Seam $s @{ op = 'ime-trace' }; $trace.events | Set-Content (Join-Path $OutDir "$Name.ime-trace.txt") } catch { }
            Stop-SeamSession $s
        }
        # The coexistence guard refuses a launch beside any Wintty it cannot
        # identify, and a process in teardown is exactly that. Wait this
        # scenario's app out before the next one starts.
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline) {
            $left = @(Get-Process -Name Wintty -ErrorAction SilentlyContinue)
            if ($left.Count -eq 0) { break }
            Start-Sleep -Milliseconds 250
        }
    }
}

# --- steps -------------------------------------------------------------

# One composed syllable, committed by the settle timer (no key behind it).
function Syllable([string]$t) {
    @(
        @{ op = 'ime-started'; start = 0 },
        @{ op = 'ime-set-text'; text = $t },
        @{ op = 'ime-changed' },
        @{ op = 'ime-ended' },
        @{ sleep = 400 }
    )
}
# A syllable whose composition ends and Space follows at once (#1468's case).
function SyllableThenSpace([string]$t) {
    @(
        @{ op = 'ime-started'; start = 0 },
        @{ op = 'ime-set-text'; text = $t },
        @{ op = 'ime-changed' },
        @{ op = 'ime-ended' },
        @{ op = 'ime-key'; key = 'Space' },
        @{ op = 'ime-char'; char = ' ' }
    )
}
# Text before a new composition is final and is forwarded while composing.
$PrefixOnStart = @(
    @{ op = 'ime-started'; start = 0 },
    @{ op = 'ime-set-text'; text = ([string][char]0xAC00) },
    @{ op = 'ime-changed' },
    @{ op = 'ime-set-text'; text = ([string][char]0xAC00 + [string][char]0xB098) },
    @{ op = 'ime-started'; start = 1 },
    @{ op = 'ime-ended' },
    @{ op = 'ime-ended' },
    @{ sleep = 400 }
)
# A commit that lands while a textless key is still held: the press was
# already dispatched; the commit must not vanish into it.
$CommitWhileLeftHeld = @(
    @{ op = 'terminal-key'; vk = 0x25; extended = $true },
    @{ op = 'ime-started'; start = 0 },
    @{ op = 'ime-set-text'; text = ([string][char]0xB098) },
    @{ op = 'ime-changed' },
    @{ op = 'ime-ended' },
    @{ sleep = 400 },
    @{ op = 'terminal-key'; vk = 0x25; extended = $true; action = 'release' }
)
# A character typed on a layout: the KeyDown press goes first (no text),
# then the character -- exactly the pair WinUI delivers for one keypress.
function LayoutChar([int]$vk, [string]$ch) {
    @(
        @{ op = 'terminal-key'; vk = $vk },
        @{ op = 'ime-set-text'; text = $ch },
        @{ op = 'ime-char'; char = $ch },
        @{ sleep = 300 },
        @{ op = 'terminal-key'; vk = $vk; action = 'release' }
    )
}

$cheot = [string][char]0xCCC7     # cheot
$e = [string][char]0xC5D0         # e
$nihongo = [string][char]0x65E5 + [char]0x672C + [char]0x8A9E   # Japanese
$nana = [string][char]0xB098      # na
$uuml = [string][char]0x00FC      # u-umlaut
$ef = [string][char]0x0444        # ef

# --- expectations -------------------------------------------------------
# Korean/Japanese: the plain UTF-8 of each character, never a paste frame
# and never dropped.
# Layout: the character alone -- no phantom CSI u ahead of it.
#
# A supplementary-plane commit (U+20000) is deliberately NOT pinned here:
# the pane child's console input conversion mangles it to two U+FFFD on
# both the fixed and the unfixed tree, so this harness cannot observe it.
# What it can observe is pinned; plane-2 bytes are the contributor's to
# verify with a live IME.

$ko2004 = @('ec b3 87', 'ec 97 90 20', 'e6 97 a5 e6 9c ac e8 aa 9e', 'ea b0 80 eb 82 98')
$koLegacy = @('ec b3 87', 'ec 97 90 20', 'e6 97 a5 e6 9c ac e8 aa 9e', 'ea b0 80 eb 82 98')
# Under event-reporting flags the Space key's CSI u carries the live
# modifier bits (CurrentMods reads the real keyboard), so the space step
# runs only where it is a deterministic 0x20 (legacy and plain 2004).
$koKitty = @('ec b3 87', 'ec 97 90', 'e6 97 a5 e6 9c ac e8 aa 9e', 'ea b0 80 eb 82 98')
# The held Left: its press went out first, the syllable follows it, the
# release last (legacy: ESC[D .. ; kitty 3: the flagged form).
$heldLegacy = '1b 5b 44 eb 82 98'
$heldKitty3 = '1b 5b 31 3b 31 3a 31 44 eb 82 98 1b 5b 31 3b 31 3a 33 44'
# Layout characters: the UTF-8 alone under disambiguate, the release riding
# behind under event reporting (matched as a trailing ESC by Test-Seq).
$layoutKitty1U = 'c3 bc'
$layoutKitty1Ef = 'd1 84'

Invoke-Scenario 'ko-commit-2004' '\x1b[?2004h' @(
    (Syllable $cheot), (SyllableThenSpace $e), (Syllable $nihongo), $PrefixOnStart
) $ko2004
Invoke-Scenario 'ko-commit-legacy' '' @(
    (Syllable $cheot), (SyllableThenSpace $e), (Syllable $nihongo), $PrefixOnStart
) $koLegacy
Invoke-Scenario 'ko-commit-kitty3-2004' '\x1b[?2004h\x1b[>3u' @(
    (Syllable $cheot), (Syllable $e), (Syllable $nihongo), $PrefixOnStart
) $koKitty
# The comma keeps each scenario's ONE step group an array: without it
# PowerShell unrolls the outer @() and $Steps[0] becomes the first
# hashtable, which the step loop would read as the whole group.
Invoke-Scenario 'ko-held-key-legacy' '' (, $CommitWhileLeftHeld) @( $heldLegacy )
Invoke-Scenario 'ko-held-key-kitty3' '\x1b[>3u' (, $CommitWhileLeftHeld) @( $heldKitty3 )
Invoke-Scenario 'layout-char-kitty1' '\x1b[>1u' @(
    (LayoutChar 0xBA $uuml), (LayoutChar 0x41 $ef)
) @($layoutKitty1U, $layoutKitty1Ef)
Invoke-Scenario 'layout-char-kitty3' '\x1b[>3u' @(
    (LayoutChar 0xBA $uuml), (LayoutChar 0x41 $ef)
) @($layoutKitty1U, $layoutKitty1Ef)
Invoke-Scenario 'layout-char-2004' '\x1b[?2004h' @(
    (LayoutChar 0xBA $uuml), (LayoutChar 0x41 $ef)
) @($layoutKitty1U, $layoutKitty1Ef)
Invoke-Scenario 'layout-char-legacy' '' @(
    (LayoutChar 0xBA $uuml), (LayoutChar 0x41 $ef)
) @($layoutKitty1U, $layoutKitty1Ef)

$failed = @($script:Results | Where-Object { -not $_.ok })
$failed | ForEach-Object { Write-Host ("FAILED {0} [{1}] {2}" -f $_.name, $_.class, $_.detail) }
$script:Results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'results.json')
if ($failed.Count -gt 0) {
    foreach ($f in $failed) { if ($f.class -eq 'product') { exit 2 } }
    exit 1
}
Write-Host ("ALL PASS ({0} scenarios)" -f $script:Results.Count)
exit 0
