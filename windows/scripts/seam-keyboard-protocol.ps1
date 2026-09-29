<#
.SYNOPSIS
Keyboard-protocol conformance: the pty bytes a shifted text key produces
under each keyboard protocol a TUI app negotiates.

.DESCRIPTION
Issue #1253: Shift+; produced ';' inside Neovim (':' at the shell prompt),
and shifted letters misbehaved, while Windows Terminal and Wave were fine.
Neovim and its kin negotiate a keyboard protocol at startup (kitty CSI u
flags after a CSI ? u query, or xterm modifyOtherKeys level 2 via
CSI > 4;2m) and the bytes a shifted OEM key must produce differ per
protocol: under kitty without "report all keys", a text key whose only
modifier is shift still produces its text (':'), never CSI 59;2u -- an
application decodes that as the unshifted key.

Each scenario launches a fresh app whose pane runs a tiny echo program
that pushes one protocol into the terminal (exactly the bytes a clean
Neovim sends, from a captured transcript) and then prints every byte the
pty delivers as hex. The seam injects one key press through the pane's
real WM_KEYDOWN + WM_CHAR handlers (terminal-key) and the scenario
asserts the exact hex. One scenario drives a real nvim --clean TUI
instead of the echo program and asserts the ':' it renders for Shift+;.

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
    # Full path to nvim.exe for the real-TUI scenario. Empty skips it.
    [string]$Nvim = '',
    # Full path to tuios.exe for the nested-intermediary scenario (the
    # workaround from the issue thread: TuiOS in Wintty, nvim inside
    # TuiOS). Empty skips it. TuiOS negotiates kitty flags and
    # modifyOtherKeys with Wintty and re-presents its own face to its
    # children, so the shifted key must survive two protocol hops.
    [string]$Tuios = '',
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
# Invoke-EchoScenario), so the staging path cannot carry a space.
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
        if ($text -like "*$Pattern*") { return $text }
        Start-Sleep -Milliseconds 250
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

# One fresh app on the echo program, one key per assertion. $Keys entries
# are @{ vk = 0xBA; shift = $true; char = ':' } and $Expect entries the
# hex the pty must receive for that key's PRESS (a release follows under
# event-reporting flags; the press is the insertion a TUI acts on).
function Invoke-EchoScenario {
    param(
        [Parameter(Mandatory)][string]$Name,
        # The protocol bytes the pane program writes before reading, '' for none.
        [string]$Push = '',
        [Parameter(Mandatory)][array]$Keys,
        [Parameter(Mandatory)][array]$Expect
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
        for ($i = 0; $i -lt $Keys.Count; $i++) {
            $k = $Keys[$i]
            # @() around every Get-KeyHex: one marker unrolls to a string
            # on assignment, and indexing a string returns a character.
            $before = @(Get-KeyHex (Get-Screen $s)).Count
            [void](Seam $s @{
                op    = 'terminal-key'
                vk    = $k.vk
                shift = [bool]$k.shift
                ctrl  = [bool]$k.ctrl
                alt   = [bool]$k.alt
                char  = $k.char
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
                Add-Result $Name $false 'harness' ("key {0}: the pane echoed nothing" -f $i)
                return
            }
            if (-not (Test-KeyHex $hex $Expect[$i])) {
                $screen = Get-Screen $s
                Add-Result $Name $false 'product' ("key {0}: expected [{1}] got [{2}]; screen: {3}" -f
                    $i, $Expect[$i], $hex,
                    ((($screen -split "`n") | Where-Object { $_ -match '\S' } | Select-Object -First 5) -join ' | '))
                return
            }
        }
        Add-Result $Name $true 'product' (($Expect | ForEach-Object { "[$_]" }) -join ' ')
    }
    finally {
        if ($s) { Stop-SeamSession $s }
    }
}

# The keys a US layout produces: Shift+; (VK_OEM_1, text ':') and
# Shift+A (text 'A'). The char is the WM_CHAR half, the vk the KeyDown
# half, the pair exactly what one physical key press delivers.
$ShiftSemi = @{ vk = 0xBA; shift = $true; char = ':' }
$ShiftA = @{ vk = 0x41; shift = $true; char = 'A' }

# No protocol: text keys go to the pty as their text.
Invoke-EchoScenario 'legacy-shift-text' '' @($ShiftSemi, $ShiftA) @('3a', '41')

# modifyOtherKeys level 2 (what a clean Neovim sets when the kitty query
# goes unanswered): the OEM key still produces text; the letter is
# xterm's 27;2 encoding, which Neovim decodes back to 'A'.
Invoke-EchoScenario 'mok2-shift-text' '\x1b[>4;2m' @($ShiftSemi, $ShiftA) @(
    '3a',
    '1b 5b 32 37 3b 32 3b 36 35 7e'
)

# kitty flags 7 (disambiguate + event types + alternates): everything
# but report-all. A consumer that wants sequences for text keys asks for
# report-all; without it, a shift-only text key's text is unambiguous
# and CSI 59;2u is decoded back as the unshifted key.
Invoke-EchoScenario 'kitty7-shift-text' '\x1b[>7u' @($ShiftSemi, $ShiftA) @('3a', '41')

# kitty "report all keys as escape codes" (flag 8): everything is CSI u
# by request, the shifted key riding as the colon-separated alternate of
# the key field (ghostty's KittySequence form; the press event type is
# the default and not spelled out). A consumer that asked for encodings
# keeps getting them - the fix must not flatten this.
Invoke-EchoScenario 'kitty-reportall-shift-text' '\x1b[=15;1u' @($ShiftSemi) @(
    '1b 5b 35 39 3a 35 38 3b 32 75'
)

# The real TUI: a clean Neovim negotiating on its own, Shift+; rendered
# as ':' (what the ':' prompt of :wq needs, not ';') and Shift+A
# rendering as 'A'.
if ($Nvim -ne '' -and 'nvim-real-shift' -like $Only) {
    Write-Host "== nvim-real-shift" -ForegroundColor Cyan
    $s = $null
    try {
        $config = $configHead + "`ncommand = `"$Nvim`" --clean"
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput -PrivateStateBase
        # Neovim's UI: the '~' empty-buffer rows.
        if (-not (Wait-Screen $s '~' 30)) {
            Add-Result 'nvim-real-shift' $false 'harness' 'neovim never drew its UI'
        }
        else {
            # i enters insert mode; z, Shift+;, x surround the key under
            # test so the rendered row names what the key produced, and
            # Shift+A lands behind them for the letter half of the report.
            [void](Seam $s @{ op = 'terminal-key'; vk = 0x49; char = 'i' })
            Start-Sleep -Milliseconds 500
            [void](Seam $s @{ op = 'terminal-key'; vk = 0x5A; char = 'z' })
            Start-Sleep -Milliseconds 300
            [void](Seam $s @{ op = 'terminal-key'; vk = 0xBA; shift = $true; char = ':' })
            Start-Sleep -Milliseconds 300
            [void](Seam $s @{ op = 'terminal-key'; vk = 0x58; char = 'x' })
            Start-Sleep -Milliseconds 300
            [void](Seam $s @{ op = 'terminal-key'; vk = 0x41; shift = $true; char = 'A' })
            Start-Sleep -Milliseconds 500
            $text = Get-Screen $s
            # -clike: case-sensitive, so a lowercase 'a' from a broken
            # Shift+A cannot pass as the capital the fix must produce.
            if ($text -clike '*z:xA*') {
                Add-Result 'nvim-real-shift' $true 'product' 'rendered z:xA'
            }
            elseif ($text -clike '*z;x*') {
                Add-Result 'nvim-real-shift' $false 'product' "rendered z;x (Shift+; decoded as ';')"
            }
            elseif ($text -clike '*z:xa*') {
                Add-Result 'nvim-real-shift' $false 'product' 'rendered z:xa (Shift+A decoded as lowercase)'
            }
            else {
                Add-Result 'nvim-real-shift' $false 'product' ('the buffer row shows none of z:xA, z;x, z:xa: ' +
                    ((($text -split "`n") | Where-Object { $_ -match '\S' } | Select-Object -First 3) -join ' | '))
            }
        }
    }
    finally {
        if ($s) { Stop-SeamSession $s }
    }
}

# The workaround from the issue thread, as a regression scenario: TuiOS in
# Wintty, nvim inside TuiOS. TuiOS negotiates its own protocol set with
# Wintty (kitty flags plus modifyOtherKeys 2, per its captured startup
# transcript) and re-presents a face of its own to its children, so the
# shifted key crosses two protocol hops: the fix must send the form a
# kitty-path intermediary accepts, and the intermediary's own UI must stay
# drivable under the fixed encoding.
if ($Tuios -ne '' -and 'tuios-nested-shift' -like $Only) {
    Write-Host "== tuios-nested-shift" -ForegroundColor Cyan
    $s = $null
    try {
        # TuiOS persists sessions through a daemon and restores them on
        # the next run, which would bury the welcome screen this scenario
        # drives. Stop only tuios.exe processes and clear only TuiOS's
        # own session files: named paths, nothing swept.
        Get-Process -Name tuios -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        $tuiosState = Join-Path $env:LOCALAPPDATA 'tuios\sessions'
        if (Test-Path $tuiosState) {
            Get-ChildItem $tuiosState -Filter '*.json' -File -ErrorAction SilentlyContinue |
                Remove-Item -Force -ErrorAction SilentlyContinue
        }

        $config = $configHead + "`ncommand = `"$Tuios`""
        $s = Start-SeamSession -ExePath $ExePath -ConfigText $config -AllowInput -PrivateStateBase
        if (-not (Wait-Screen $s 'Terminal UI Operating System' 40)) {
            Add-Result 'tuios-nested-shift' $false 'harness' ('tuios never drew its welcome; screen: ' +
                ((Get-Screen $s) -replace "`n", ' | '))
        }
        else {
            # TuiOS is modal: n opens a window (window-management mode),
            # i enters terminal mode, then the command line reaches the
            # pane's shell.
            [void](Seam $s @{ op = 'terminal-key'; vk = 0x4E; char = 'n' })
            Start-Sleep -Milliseconds 2000
            [void](Seam $s @{ op = 'terminal-key'; vk = 0x49; char = 'i' })
            Start-Sleep -Milliseconds 1000
            # Virtual-key codes, not ASCII: 0x6E is VK_DECIMAL and 0x76 is
            # VK_F17, so lowercase ASCII values here would name other
            # keys entirely. Letters take their uppercase VK with the
            # lowercase WM_CHAR text, the pair a shifted-off key press
            # produces.
            foreach ($pair in @(
                    @{ vk = 0x4E; char = 'n' }, @{ vk = 0x56; char = 'v' }, @{ vk = 0x49; char = 'i' }, @{ vk = 0x4D; char = 'm' },
                    @{ vk = 0x20; char = ' ' }, @{ vk = 0xBD; char = '-' }, @{ vk = 0xBD; char = '-' },
                    @{ vk = 0x43; char = 'c' }, @{ vk = 0x4C; char = 'l' }, @{ vk = 0x45; char = 'e' },
                    @{ vk = 0x41; char = 'a' }, @{ vk = 0x4E; char = 'n' },
                    @{ vk = 0x0D; char = "`r" }
                )) {
                [void](Seam $s @{ op = 'terminal-key'; vk = $pair.vk; char = $pair.char })
                Start-Sleep -Milliseconds 120
            }
            if (-not (Wait-Screen $s '[No Name]' 40)) {
                Add-Result 'tuios-nested-shift' $false 'harness' ('nvim never appeared inside tuios; screen: ' +
                    (((Get-Screen $s) -split "`n" | Where-Object { $_ -match '\S' } | Select-Object -First 4) -join ' | '))
            }
            else {
                Start-Sleep -Milliseconds 1500
                [void](Seam $s @{ op = 'terminal-key'; vk = 0x49; char = 'i' })
                Start-Sleep -Milliseconds 500
                [void](Seam $s @{ op = 'terminal-key'; vk = 0x5A; char = 'z' })
                Start-Sleep -Milliseconds 300
                [void](Seam $s @{ op = 'terminal-key'; vk = 0xBA; shift = $true; char = ':' })
                Start-Sleep -Milliseconds 300
                [void](Seam $s @{ op = 'terminal-key'; vk = 0x58; char = 'x' })
                Start-Sleep -Milliseconds 300
                [void](Seam $s @{ op = 'terminal-key'; vk = 0x41; shift = $true; char = 'A' })
                Start-Sleep -Milliseconds 800
                $text = Get-Screen $s
                if ($text -clike '*z:xA*') {
                    Add-Result 'tuios-nested-shift' $true 'product' 'rendered z:xA through wintty->tuios->nvim'
                }
                elseif ($text -clike '*z;x*') {
                    Add-Result 'tuios-nested-shift' $false 'product' "rendered z;x (Shift+; lost through the intermediary)"
                }
                else {
                    Add-Result 'tuios-nested-shift' $false 'product' ('the buffer row shows neither z:xA nor z;x: ' +
                        ((($text -split "`n") | Where-Object { $_ -match '\S' } | Select-Object -First 4) -join ' | '))
                }
            }
        }
    }
    finally {
        if ($s) { Stop-SeamSession $s }
        # Leave no daemon behind either: the scenario started it.
        Get-Process -Name tuios -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

$failed = @($script:Results | Where-Object { -not $_.ok })
$failed | ForEach-Object { Write-Host ("FAILED {0} [{1}] {2}" -f $_.name, $_.class, $_.detail) }
$script:Results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'results.json')
if ($failed.Count -gt 0) {
    foreach ($f in $failed) { if ($f.class -eq 'product') { exit 2 } }
    exit 1
}
Write-Host ("ALL PASS ({0} scenarios)" -f $script:Results.Count)
exit 0
