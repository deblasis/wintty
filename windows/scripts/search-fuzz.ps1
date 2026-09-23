#requires -Version 7
<#
    Scrollback-search fuzz harness.

    The oracle is the terminal itself: TerminalControl's UIA text provider
    exposes the whole screen including scrollback as one document, so the
    harness can read the real corpus and count matches independently of
    libghostty. Every needle it sets is checked against that count rather
    than against a hardcoded expectation, which is what lets the needles be
    randomly drawn from live terminal content.

    Search is ASCII case-insensitive (src/terminal/search/sliding_window.zig
    uses std.ascii.indexOfIgnoreCase), so the oracle folds case the same way.

    Seam-actuated: the harness synthesizes no OS input and never takes the
    foreground, so it can run beside someone using the machine. Each input
    goes through the path that does not need a keyboard:

      open the bar      focus{frame} + Ctrl+Shift+F through the frame router
      type a needle     UIA ValuePattern on the needle box (TextChanged, the
                        debounce, StartSearch: the same chain a keystroke runs)
      next / previous   search-key, Enter and Shift+Enter through the needle
                        box's own key handler; the seam also reports whether
                        the box held focus, and losing it is a finding
      close             search-key Escape, or UIA Invoke on "Close search"
      seed the shell    send-text (armed with -AllowInput: the corpus has to
                        be typed into a live shell)
      wheel             scroll, the wheel handler's discrete notch; the
                        viewport libghostty reports must move unless it was
                        already at that end
      focus             read from the seam's pane readout, not from the
                        system-wide UIA focus, which follows the foreground

    One check the keyboard version made is gone, because only real key
    presses reach it: that typed keys do not leak through the bar to the
    shell.

    The corpus the oracle reads is typed into a live shell, so every seeding
    send is read back off the input row before Enter commits it. A line that
    cannot be read back ends the run as a harness failure - the corpus was
    never established, and a harness that could not build its own premise has
    nothing to say about the product.

    Failures are recorded and the run continues: one broken invariant should
    not hide the rest.

    Run it with `just search-fuzz`, optionally passing "-Seed N -Iterations N".
    A seed reproduces an entire op sequence, so a finding can be replayed.

    Exit codes, numbered to match the fuzz-suite convention:
      0  clean
      2  product findings - see run-<seed>.json and shots/ under -OutDir
      1  the harness could not run; the product was never exercised, so do
         not file a bug. It will not help to retry when the run was refused
         because a Wintty is already open - close it first

    A seed replays the op sequence, but not the corpus-slice needles drawn
    from live terminal text: those depend on the shell prompt and the window
    width, so a finding on one is reproducible only in the same environment.

    Findings this harness has caught, kept here as the regression list:
      - reopening the bar left the needle visible with no live search behind
        it, so navigation was inert until the text was edited
      - the counter rendered "0 of -1" once a search had been torn down
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Seed = 1337,
    [int]$Iterations = 40,
    [switch]$KeepOpen
    # Isolation is unconditional: Start-SeamSession stages a per-run random
    # temp XDG root and arms WINTTY_TEST_CONFIG=1, so the fuzz cannot read or
    # write the real per-user config and a lost root is a loud startup refusal
    # rather than a silent taint. There is deliberately no real-config escape:
    # a harness must not be able to point the app at the real config. If the
    # historical 0xc000027b under an isolated root ever returns, that is a
    # product bug (possibly the x:Load-overlay family, tracked on the release
    # side): capture it with cdb on the window-owner PID (break on av and
    # 40080201 first-chance) and fix it, never route around it.
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
. (Join-Path $PSScriptRoot 'lib/seam-client.ps1')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
[void][SeamWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

# ---- window / UIA plumbing -------------------------------------------------

$UIA = [System.Windows.Automation.AutomationElement]
$TS  = [System.Windows.Automation.TreeScope]

function Get-Root { return $UIA::FromHandle([SeamWin]::P($script:Hwnd64)) }

function Find-ByType($root, $controlType) {
    if ($null -eq $root) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $controlType)
    $found = $root.FindAll($TS::Descendants, $cond)
    $out = @(); foreach ($i in $found) { $out += $i }; return $out
}

function Find-ByName($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    return $root.FindFirst($TS::Descendants, $cond)
}

function Find-ById($root, [string]$id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $id)
    return $root.FindFirst($TS::Descendants, $cond)
}

# The terminal pane exposes ClassName TerminalControl and supports TextPattern
# over the whole screen document (viewport + scrollback).
function Get-TerminalElements($root) {
    if ($null -eq $root) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ClassNameProperty, 'TerminalControl')
    try { $found = $root.FindAll($TS::Descendants, $cond) } catch { return @() }
    $out = @(); foreach ($i in $found) { $out += $i }; return , $out
}

# UIA FindAll comes back empty while the app is mid-render, so every terminal
# lookup retries rather than indexing whatever the first probe returned.
function Get-Term([int]$timeoutMs = 8000) {
    $dl = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $dl) {
        # No @() here: these helpers already comma-return, and wrapping that
        # in @() yields a one-element array holding the inner array, so the
        # count guard is always true and the retry never retries.
        $els = Get-TerminalElements (Get-Root)
        if ($els.Count -gt 0) { return $els[0] }
        Start-Sleep -Milliseconds 200
    }
    throw 'no TerminalControl in the UIA tree'
}

function Get-TerminalText($el) {
    if ($null -eq $el) { return '' }
    try {
        $tp = $el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        return $tp.DocumentRange.GetText(-1)
    } catch { return '' }
}

# The counter TextBlock renders "" / "No matches" / "N of M". A window-wide
# scan for that shape is more robust than walking the search bar's layout
# panels, which WinUI does not surface consistently in the control view.
$CounterRe = '^(?:-?\d+ of -?\d+|No matches)$'

function Get-CounterTexts($root) {
    $out = @()
    foreach ($t in (Find-ByType $root ([System.Windows.Automation.ControlType]::Text))) {
        try { $n = $t.Current.Name } catch { continue }
        if ($n -match $CounterRe) { $out += $n }
    }
    # Comma-wrap: a bare single-element return unrolls to a scalar string, and
    # indexing that gives its first character rather than the counter.
    return , $out
}

# Returns the single counter in the window. More than one means more than
# one pane or tab has its bar open; the caller cannot attribute a count in
# that case, so say so rather than silently joining them into one string.
function Get-Counter($root) {
    $all = Get-CounterTexts $root
    if ($all.Count -eq 0) { return '' }
    if ($all.Count -gt 1) { return "<ambiguous: $($all -join ' | ')>" }
    return [string]$all[0]
}

function Get-NeedleBox($root) { return Find-ByName $root 'Search scrollback' }

function Get-NeedleText($root) {
    $box = Get-NeedleBox $root
    if ($null -eq $box) { return $null }
    try {
        $vp = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return $vp.Current.Value
    } catch { return $null }
}

function Test-SearchBarOpen($root) { return $null -ne (Get-NeedleBox $root) }

# What holds keyboard focus inside the app, by accessible name. The seam
# reads it through the window's FocusManager, so the answer is the app's own
# and does not depend on which window is in the foreground.
function Get-FocusedName {
    try {
        $r = Invoke-SeamCommand $script:Session @{ op = 'get-state' }
        return [string]$r.state.panes.focusedName
    } catch { return '<none>' }
}

# ---- oracle ---------------------------------------------------------------
#
# The occurrence count and the seed read-back rules are shared with the
# self-test, which exercises them with no window. See lib/seed-readback.ps1.
. (Join-Path $PSScriptRoot 'lib/seed-readback.ps1')

# Both haystacks unwrap soft wraps: the UIA document comes from
# Screen.selectionString with unwrap = true, and the search window builds its
# haystack with PageFormatter unwrap = true. So a match cannot straddle a
# wrap in one and not the other, and the count is exact rather than a range.
# The only remaining difference is trailing whitespace (the document keeps
# it, the search haystack trims it), which is why needles containing
# whitespace never reach a strict assertion.
function Get-ExpectedCount([string]$doc, [string]$needle) {
    return Measure-Occurrences $doc $needle
}

# ---- assertions -----------------------------------------------------------

$script:Findings = [System.Collections.Generic.List[object]]::new()
$script:Checks = 0

function Add-Finding([string]$kind, [string]$detail, [hashtable]$data) {
    $f = [ordered]@{ kind = $kind; detail = $detail; iteration = $script:Iter }
    if ($data) { foreach ($k in $data.Keys) { $f[$k] = $data[$k] } }
    $script:Findings.Add([pscustomobject]$f)
    Write-Host "  FAIL [$kind] $detail" -ForegroundColor Red
}

function Assert-That([bool]$ok, [string]$kind, [string]$detail, [hashtable]$data) {
    $script:Checks++
    if (-not $ok) { Add-Finding $kind $detail $data }
    return $ok
}

function Shot([string]$name) {
    $rc = [SeamWin]::RectOf($script:Hwnd64)
    if ($null -eq $rc) { return }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir "shots\$name.png"))
    $g.Dispose(); $bmp.Dispose()
}

# Counting pixels close to the search highlight colors is the only way to
# tell "libghostty found matches" apart from "the renderer painted them".
# Defaults come from Config.zig: search-background #FFE082, and
# search-selected-background #F2A57E. The window is placed topmost without
# activation at launch, so the screen capture sees the app and not whatever
# the person at the machine is working in.
function Measure-HighlightPixels([int]$r, [int]$g, [int]$b, [int]$tol = 24) {
    $rc = [SeamWin]::RectOf($script:Hwnd64)
    if ($null -eq $rc) { return -1 }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $gfx.Dispose()
    $rect = New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $data.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    $hits = 0
    for ($i = 0; $i -lt $bytes.Length; $i += 4) {
        # BGRA
        if ([Math]::Abs($bytes[$i + 2] - $r) -le $tol -and
            [Math]::Abs($bytes[$i + 1] - $g) -le $tol -and
            [Math]::Abs($bytes[$i] - $b) -le $tol) { $hits++ }
    }
    return $hits
}

function Assert-Alive([string]$where) {
    $script:Proc.Refresh()
    if ($script:Proc.HasExited) {
        Add-Finding 'crash' "process exited during $where (code $($script:Proc.ExitCode))" @{}
        throw "APP_DIED at $where"
    }
}

# ---- input wrappers -------------------------------------------------------

# Bytes to the shell, the way committed IME text arrives: no focus needed.
function Send-Shell([string]$text) {
    [void](Invoke-SeamCommand $script:Session @{ op = 'send-text'; text = $text })
}

# Ctrl+C aborts both a half-typed line and a continuation prompt, and is
# harmless on an already-empty one.
function Clear-CommandLine {
    Send-Shell ([string][char]3)
    Start-Sleep -Milliseconds 300
    Send-Shell "`r"
    Start-Sleep -Milliseconds 500
}

function Open-SearchBar {
    [void](Invoke-SeamCommand $script:Session @{ op = 'focus'; target = 'frame' })
    $r = Invoke-SeamCommand $script:Session @{ op = 'chord'; key = 0x46; ctrl = $true; shift = $true }
    if (-not $r.dispatched) {
        throw "the search chord was not dispatched (focus was '$($r.focus)')"
    }
    Start-Sleep -Milliseconds 500
}


# A key in the needle box, through the box's own key handler (search-key).
# The seam also says whether the box held focus, which is where a real key
# would have landed; a box that lost it is the product's finding, filed here
# so the key's effect is still judged.
function Press-NeedleKey([string]$key, [bool]$shift, [string]$what) {
    $r = Invoke-SeamCommand $script:Session @{ op = 'search-key'; key = $key; shift = $shift }
    [void](Assert-That ([bool]$r.needleFocused) 'needle-focus-lost' `
        "$what pressed while the needle box did not hold focus" @{ key = $key; shift = $shift })
    [void](Assert-That ([bool]$r.handled) 'needle-key-ignored' `
        "the needle box's handler did not take $what" @{ key = $key; shift = $shift })
}

function Press-Next {
    Press-NeedleKey 'enter' $false 'Enter'
    Start-Sleep -Milliseconds 260
}
function Press-Prev {
    Press-NeedleKey 'enter' $true 'Shift+Enter'
    Start-Sleep -Milliseconds 260
}
function Press-Escape {
    Press-NeedleKey 'escape' $false 'Escape'
    Start-Sleep -Milliseconds 300
}
function Close-SearchBar {
    $btn = Find-ByName (Get-Root) 'Close search'
    if ($null -eq $btn) { throw 'the search bar has no Close search button' }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 350
}

# Set the needle box's text, the way typing lands it: TextChanged fires and
# the debounce starts the search.
function Set-Needle([string]$text) {
    $box = Get-NeedleBox (Get-Root)
    if ($null -eq $box) { throw 'the needle box is not in the tree' }
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}

# ---- seeding --------------------------------------------------------------
#
# A seed line is handed to a live shell, and a character it loses on the way
# is invisible: nothing throws, the shell just runs something else. The corpus
# is what every oracle count is measured against, so a lost character does not
# fail one check, it moves the answer for the rest of the run - a false pass
# and a false finding are equally reachable from there.

# How many times a seed line is resent before the run gives up on it.
$script:SeedAttempts = 3

# Send a line that becomes corpus and prove it landed, before Enter commits it.
# Reading back afterwards is too late: by then the shell has echoed, executed
# and scrolled, and a mangled line has become output that cannot be told apart
# from the output that was wanted.
#
# Returns 'ok', 'bar-open' or 'unverified'. 'bar-open' is handed back rather
# than retried: the caller files it as the product defect it is.
function Send-SeedText([string]$text, [string]$dumpPath) {
    # A dump from a previous run reads as evidence about this one, and OutDir
    # is reused. Clear it so its presence means this attempt wrote it.
    if ($dumpPath -and (Test-Path $dumpPath)) { Remove-Item $dumpPath -Force }
    if (Test-SearchBarOpen (Get-Root)) { return 'bar-open' }

    for ($try = 1; $try -le $script:SeedAttempts; $try++) {
        $before = Get-TerminalText (Get-Term)
        Send-Shell (' ' + $text)

        # Polled rather than slept. The peer serves its document from a 500ms
        # cache (TerminalAutomationPeer.ScreenTextCacheMs), so any fixed settle
        # shorter than that can read a snapshot taken before the last
        # characters landed and call a good send a miss.
        $verdict = 'unreadable'
        $doc = ''
        $deadline = (Get-Date).AddMilliseconds(2500)
        do {
            Start-Sleep -Milliseconds 200
            $doc = Get-TerminalText (Get-Term)
            $verdict = Test-SeedLanded $before $doc $text
        } while ($verdict -ne 'landed' -and (Get-Date) -lt $deadline)

        if ($dumpPath) { $doc | Set-Content $dumpPath -Encoding utf8 }
        if ($verdict -eq 'landed') { return 'ok' }

        Write-Host "  seed: the input row does not hold the line ($verdict, attempt $try of $script:SeedAttempts), resending" -ForegroundColor Yellow
        # Drop whatever did land, including the continuation prompt a dropped
        # quote leaves behind, so the retry starts from a bare line.
        Clear-CommandLine
    }
    return 'unverified'
}

# Poll until the counter stops changing, so assertions never race the
# progressive search (24ms refresh tick plus incremental feeding).
function Wait-Counter([int]$timeoutMs = 6000, [int]$stableMs = 400) {
    $dl = (Get-Date).AddMilliseconds($timeoutMs)
    $last = $null; $since = Get-Date
    while ((Get-Date) -lt $dl) {
        $cur = Get-Counter (Get-Root)
        if ($cur -ne $last) { $last = $cur; $since = Get-Date }
        elseif (((Get-Date) - $since).TotalMilliseconds -ge $stableMs) { return $cur }
        Start-Sleep -Milliseconds 90
    }
    return $last
}

function Get-CounterParts([string]$text) {
    if ($text -match '^(-?\d+) of (-?\d+)$') { return @([int]$Matches[1], [int]$Matches[2]) }
    if ($text -eq 'No matches') { return @(0, 0) }
    return $null
}

# ---- run ------------------------------------------------------------------

$rng = [System.Random]::new($Seed)
$configText = @'
windows-single-instance = false
window-save-state = never
window-width = 120
window-height = 34
scrollback-limit = 10000000
window-theme = wintty
'@
$script:Session = $null
$script:Proc = $null
$script:Iter = 0
$script:ExitCode = 0
$result = [ordered]@{
    seed = $Seed; iterations = $Iterations; ops = @()
    actuation = 'seam (WINTTY_TEST_SEAM=<session token>, send-text armed); bar by chord and UIA; no synthesized OS input'
}

$script:CrashBaseline = 0
$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
if (Test-Path $crashPath) { $script:CrashBaseline = (Get-Item $crashPath).Length }

try {
    if (-not (Test-Path $ExePath)) { throw "missing exe: $ExePath" }

    # Never kill by name: developers keep builds from several worktrees open
    # at once, and force-killing every Wintty takes down work this run has
    # nothing to do with. Refuse instead. lib/wintty-process.ps1 carries the
    # rule and the reasoning; the short version is that crash.log is shared
    # per user rather than per exe path, and this harness reports every byte
    # the file gains during a run as a defect in the build under test.
    #
    # The gate sits inside the try, unlike the other harnesses, because the
    # catch below records the refusal as a harness finding and prints it in
    # the harness's own voice. The finally only tears down a session that
    # exists, so a refusal cannot be replaced by a teardown error.
    Assert-NoWintty -Context 'The search fuzz'

    $script:Session = Start-SeamSession -ExePath $ExePath -ConfigText $configText -AllowInput
    $script:Proc = $script:Session.Proc
    $script:Hwnd64 = [int64]$script:Session.Hwnd64
    Write-Host ("config: {0}" -f $script:Session.TempXdg)
    [SeamWin]::PlaceOnTop($script:Hwnd64)
    Start-Sleep -Milliseconds 800

    $terms = Get-TerminalElements (Get-Root)
    if ($terms.Count -lt 1) { throw 'no TerminalControl exposed to UIA' }
    Write-Host "terminal panes: $($terms.Count)"

    # ---- seed the scrollback ---------------------------------------------
    # Every token is assembled at runtime so the echoed command line never
    # contains the needle itself; otherwise the echo would inflate the count
    # in ways the oracle cannot see coming.
    $payload = @(
        "`$a='ZQ'+'XW'; `$b='PL'+'MK'; `$c='VR'+'TN'",
        '1..180 | % { "$a row $_" }',
        '1..47  | % { "$b item $_" }',
        '"$c singleton"',
        '"$a$b adjacent"',
        '"done"+"-marker"'
    ) -join '; '

    Write-Host 'seeding scrollback...'

    # Wait for the shell to actually draw something. Bytes sent before the
    # shell reads its input can be lost, and every later oracle count is
    # measured against the corpus that send was supposed to create.
    $dl = (Get-Date).AddSeconds(30)
    $preseed = ''
    while ((Get-Date) -lt $dl) {
        $preseed = Get-TerminalText (Get-Term)
        if ($preseed.Trim().Length -gt 0) { break }
        Start-Sleep -Milliseconds 500
    }
    $preseed | Set-Content (Join-Path $OutDir 'doc-preseed.txt') -Encoding utf8
    if ($preseed.Trim().Length -eq 0) {
        throw 'the terminal never rendered any text; the shell did not start'
    }

    # The payload is PowerShell. Sniffing the prompt is unreliable (a themed
    # prompt looks nothing like "PS >"), so ask the shell what it is and wait
    # for the answer. This doubles as proof that sent text is landing.
    function Test-Pwsh([int]$timeoutMs) {
        Send-Shell ('"SHELL"+"OK-$($PSVersionTable.PSEdition)"' + "`r")
        $dl = (Get-Date).AddMilliseconds($timeoutMs)
        while ((Get-Date) -lt $dl) {
            if ((Get-TerminalText (Get-Term)) -match 'SHELLOK-Core') { return $true }
            Start-Sleep -Milliseconds 400
        }
        return $false
    }

    $inPwsh = Test-Pwsh 25000
    if (-not $inPwsh) {
        Clear-CommandLine
        Send-Shell "pwsh -NoLogo -NoProfile`r"
        Start-Sleep -Seconds 6
        $inPwsh = Test-Pwsh 25000
    }
    if (-not $inPwsh) {
        (Get-TerminalText (Get-Term)) | Set-Content (Join-Path $OutDir 'doc-noshell.txt') -Encoding utf8
        throw 'no PowerShell in the terminal (sent text may not be landing), see doc-noshell.txt'
    }

    # A dropped character can leave PowerShell in continuation mode, where
    # everything sent afterwards is swallowed as more of an unterminated
    # string. Reset the line before committing to the payload.
    Clear-CommandLine

    # Inline prediction renders the rest of a matching history entry into the
    # grid as soon as the typed prefix matches it, and those are real cells the
    # read-back counts. Default is HistoryAndPlugin on 7.2+, so it has to be
    # turned off rather than assumed off.
    Send-Shell "Set-PSReadLineOption -PredictionSource None`r"
    Start-Sleep -Milliseconds 800
    Clear-CommandLine

    $seeded = Send-SeedText $payload (Join-Path $OutDir 'doc-typed.txt')
    if ($seeded -ne 'ok') {
        throw "the seed payload never landed on the input row ($seeded) after $script:SeedAttempts attempts, see doc-typed.txt"
    }
    Send-Shell "`r"
    Start-Sleep -Seconds 3

    $term = Get-Term
    $doc = ''
    $dl = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $dl) {
        $doc = Get-TerminalText $term
        if ($doc -match 'done-marker') { break }
        Start-Sleep -Milliseconds 500
    }
    $doc | Set-Content (Join-Path $OutDir 'doc-seeded.txt') -Encoding utf8
    if ($doc -notmatch 'done-marker') {
        throw "payload never completed (no done-marker); document is $($doc.Length) chars, see doc-*.txt"
    }
    Start-Sleep -Milliseconds 900
    $doc = Get-TerminalText $term
    Write-Host "corpus: $($doc.Length) chars, $((($doc -split "`n").Count)) rows"
    $result.corpusChars = $doc.Length

    # The scroll oracle below judges only a viewport that can move, and that
    # is decided from the scrollbar libghostty reports. If it never reported
    # one, total stays 0 and every scroll check would pass without judging
    # anything. The seeded corpus overflows the window, so the report must
    # show more rows than fit, and a wheel up from the bottom must move.
    $probe = Invoke-SeamCommand $script:Session @{ op = 'scroll'; notches = 3 }
    $pv = $probe.viewport
    Write-Host "  viewport after seeding: $($pv.total) rows, $($pv.len) visible, offset $($pv.offsetBefore)->$($pv.offsetAfter)"
    [void](Assert-That ([double]$pv.total -gt [double]$pv.len) 'no-scrollback-report' `
        "after seeding, the scrollbar libghostty reports holds $($pv.total) rows for $($pv.len) visible" @{ viewport = $pv })
    [void](Assert-That ([double]$pv.offsetAfter -lt [double]$pv.offsetBefore) 'scroll-did-not-move' `
        "a wheel up from the bottom of the seeded corpus left the viewport at row $($pv.offsetAfter)" @{ viewport = $pv })
    [void](Invoke-SeamCommand $script:Session @{ op = 'scroll'; notches = -3 })

    # Needle pool. Strict needles are checked against the oracle; the rest
    # only have to not break an invariant (no crash, counter well-formed).
    $strict = @('ZQXW', 'zqxw', 'ZqXw', 'PLMK', 'VRTN', 'ZQXWPLMK', 'NOTHERE9X', 'row 1', 'item 4')
    $weird  = @(':', 'a:b', ' ', '  ', 'row ', '"', '\', '%', '$a', '.*', '[', '(', '?', 'ZQXW ', ' ZQXW',
                'ábç', 'こんにちは', '😀', ('Z' * 200), '-', '--', '0', 'e')

    function Get-RandomNeedle {
        $r = $rng.Next(100)
        if ($r -lt 55) { return $strict[$rng.Next($strict.Count)] }
        if ($r -lt 85) { return $weird[$rng.Next($weird.Count)] }
        # A random slice of the live corpus: the oracle handles it the same way.
        $printable = ($doc -replace "[^\x20-\x7E]", ' ')
        # Both draws happen before any early return, so the stream advances
        # by a fixed amount whatever the corpus looks like.
        $len = $rng.Next(2, 7)
        $pos = $rng.Next(0, 100000)
        if ($printable.Length -le $len) { return 'ZQXW' }
        $at = $pos % ($printable.Length - $len)
        return $printable.Substring($at, $len).Trim()
    }

    function Assert-CounterMatchesOracle([string]$needle, [string]$counter) {
        $parts = Get-CounterParts $counter
        if ($null -eq $parts) {
            Add-Finding 'counter-shape' "needle '$needle' produced counter '$counter'" @{ needle = $needle; counter = $counter }
            return
        }
        $total = $parts[1]
        $expected = Get-ExpectedCount $doc $needle
        $ok = ($total -eq $expected)
        [void](Assert-That $ok 'count-mismatch' `
            "needle '$needle': reported $total, oracle expects $expected" `
            @{ needle = $needle; reported = $total; oracle = $expected; counter = $counter })
        if (-not $ok) { Shot ("mismatch-{0:d3}" -f $script:Iter) }
    }

    # ---- baseline invariants ---------------------------------------------
    Write-Host "`n== baseline ==" -ForegroundColor Cyan

    Open-SearchBar
    Assert-Alive 'open'
    [void](Assert-That (Test-SearchBarOpen (Get-Root)) 'bar-missing' 'Ctrl+Shift+F did not surface the search bar' @{})

    # Focus must land in the needle box, or the user types into the shell.
    $focused = Get-FocusedName
    [void](Assert-That ($focused -eq 'Search scrollback') 'focus-not-in-needle' `
        "after Ctrl+Shift+F the focused element is '$focused', expected 'Search scrollback'" @{ focused = $focused })

    Set-Needle 'ZQXW'
    Start-Sleep -Milliseconds 400
    $c = Wait-Counter
    Write-Host "  counter after setting ZQXW: '$c'"
    Assert-CounterMatchesOracle 'ZQXW' $c

    # Navigation must walk 1..N and wrap.
    $parts = Get-CounterParts $c
    if ($null -ne $parts -and $parts[1] -gt 2) {
        $total = $parts[1]
        $seen = @()
        for ($k = 0; $k -lt 4; $k++) {
            Press-Next
            $cc = Wait-Counter 4000 250
            $pp = Get-CounterParts $cc
            $seen += if ($null -eq $pp) { -1 } else { $pp[0] }
        }
        Write-Host "  next x4 -> $($seen -join ', ') (of $total)"
        $monotonic = $true
        for ($k = 1; $k -lt $seen.Count; $k++) {
            $prev = $seen[$k - 1]; $cur = $seen[$k]
            # -1 marks a counter that could not be parsed; reporting that as
            # a navigation fault would blame the wrong thing.
            if ($prev -lt 0 -or $cur -lt 0) { continue }
            if ($cur -ne ($prev % $total) + 1) { $monotonic = $false }
        }
        [void](Assert-That ($seen[0] -ge 1) 'nav-no-selection' "first next left the index at $($seen[0])" @{ seen = $seen })
        [void](Assert-That $monotonic 'nav-not-sequential' `
            "next did not step 1-by-1 with wrap: $($seen -join ',') of $total" @{ seen = $seen; total = $total })

        Press-Prev
        $cb = Wait-Counter 4000 250
        $pb = Get-CounterParts $cb
        $expectBack = if ($seen[-1] -le 1) { $total } else { $seen[-1] - 1 }
        [void](Assert-That ($null -ne $pb -and $pb[0] -eq $expectBack) 'nav-prev-wrong' `
            "previous from $($seen[-1]) gave $cb, expected index $expectBack" @{ from = $seen[-1]; got = $cb })
    }

    # A correct counter proves libghostty found the matches; it says nothing
    # about whether the renderer painted them. The PLMK rows are the ones
    # sitting in the viewport after seeding, so their highlights must show up
    # as pixels in the window.
    Set-Needle ''
    Start-Sleep -Milliseconds 600
    # Measure the baseline with no search active. Taking it while the
    # previous needle was still highlighted compared one search against
    # another rather than against an unhighlighted screen.
    $baseHighlight = Measure-HighlightPixels 255 224 130
    Set-Needle 'PLMK'
    Start-Sleep -Milliseconds 400
    $cH = Wait-Counter
    Assert-CounterMatchesOracle 'PLMK' $cH
    Start-Sleep -Milliseconds 500
    $litHighlight = Measure-HighlightPixels 255 224 130
    $litSelected = Measure-HighlightPixels 242 165 126
    Write-Host "  highlight pixels: base=$baseHighlight lit=$litHighlight selected=$litSelected"
    Shot 'highlight-plmk'
    [void](Assert-That ($litHighlight -gt ($baseHighlight + 200)) 'no-match-highlight' `
        "search matches are not painted: $baseHighlight highlight-colored pixels before, $litHighlight after" `
        @{ before = $baseHighlight; after = $litHighlight })
    Press-Next
    Start-Sleep -Milliseconds 700
    $selAfter = Measure-HighlightPixels 242 165 126
    Shot 'highlight-plmk-selected'
    [void](Assert-That ($selAfter -gt ($litSelected + 50)) 'no-selected-highlight' `
        "the selected match is not painted in its own color: $litSelected before next, $selAfter after" `
        @{ before = $litSelected; after = $selAfter })

    Press-Escape
    [void](Assert-That (-not (Test-SearchBarOpen (Get-Root))) 'esc-did-not-close' 'Escape left the search bar open' @{})

    # After closing, focus must be back in the terminal, or the next thing
    # the user types goes nowhere. The seam reads it off the app's own focus.
    Start-Sleep -Milliseconds 300
    $afterClose = Invoke-SeamCommand $script:Session @{ op = 'probe' }
    [void](Assert-That ($afterClose.focus -eq 'pane' -and $afterClose.state.panes.focusedName -ne 'Search scrollback') `
        'focus-not-returned' "after closing the bar focus is '$($afterClose.focus)' on '$($afterClose.state.panes.focusedName)'" @{})

    # ---- randomized sweep ------------------------------------------------
    Write-Host "`n== fuzz ($Iterations iterations, seed $Seed) ==" -ForegroundColor Cyan
    $barOpen = $false
    for ($script:Iter = 1; $script:Iter -le $Iterations; $script:Iter++) {
        Assert-Alive "iteration $script:Iter"
        $barOpen = Test-SearchBarOpen (Get-Root)

        # Draw unconditionally, even when the op is forced: letting product
        # state decide whether the RNG advances makes the seed stop
        # replaying the sequence after the first divergence.
        $roll = $rng.Next(100)
        $op = if (-not $barOpen) { 'open' } else {
            if     ($roll -lt 34) { 'type' }
            elseif ($roll -lt 52) { 'next' }
            elseif ($roll -lt 64) { 'prev' }
            elseif ($roll -lt 72) { 'clear' }
            elseif ($roll -lt 80) { 'close' }
            elseif ($roll -lt 86) { 'scroll' }
            elseif ($roll -lt 92) { 'resize' }
            else                  { 'emit' }
        }

        $detail = ''
        switch ($op) {
            'open' {
                Open-SearchBar
                [void](Assert-That (Test-SearchBarOpen (Get-Root)) 'bar-missing' 'Ctrl+Shift+F did not open the bar' @{})
                $f = Get-FocusedName
                [void](Assert-That ($f -eq 'Search scrollback') 'focus-not-in-needle' `
                    "focus is '$f' after opening" @{ focused = $f })
                $detail = "focus=$f"

                # The needle survives a close, and reopening re-runs it.
                # The counter next to it therefore has to describe that
                # needle's live results, not whatever the previous search
                # left behind and not a torn-down search's -1.
                $keptNeedle = Get-NeedleText (Get-Root)
                if ($keptNeedle -and $keptNeedle.Length -gt 0) {
                    $c = Wait-Counter 3000 300
                    $detail += " kept='$keptNeedle' counter='$c'"
                    if ($keptNeedle -match '^[\x21-\x7E]{2,}$') {
                        Assert-CounterMatchesOracle $keptNeedle $c
                    }
                }
            }
            'type' {
                $needle = Get-RandomNeedle
                Set-Needle $needle
                Start-Sleep -Milliseconds 300
                $c = Wait-Counter
                $detail = "needle='$needle' counter='$c'"
                if ($needle.Trim().Length -gt 0) {
                    # Strict counting needs an ASCII needle with no whitespace:
                    # the oracle folds case with ASCII rules like the search
                    # does, but row padding and wrapping make whitespace counts
                    # differ between the UIA document and the terminal grid for
                    # reasons that are not defects.
                    if ($needle -match '^[\x21-\x7E]{2,}$') { Assert-CounterMatchesOracle $needle $c }
                    else {
                        [void](Assert-That ($c -eq '' -or $null -ne (Get-CounterParts $c)) 'counter-shape' `
                            "non-ASCII needle '$needle' produced '$c'" @{ needle = $needle; counter = $c })
                    }
                }
                $box = Get-NeedleText (Get-Root)
                if ($null -ne $box -and $needle -match '^[\x20-\x7E]+$') {
                    [void](Assert-That ($box -ceq $needle) 'needle-box-drift' `
                        "set '$needle' but the box holds '$box'" @{ typed = $needle; box = $box })
                }
            }
            'next' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                Press-Next
                $c = Wait-Counter 4000 250
                $after = Get-CounterParts $c
                $detail = "next -> '$c'"
                if ($null -ne $before -and $null -ne $after -and $before[1] -gt 0) {
                    $exp = ($before[0] % $before[1]) + 1
                    [void](Assert-That ($after[0] -eq $exp) 'nav-not-sequential' `
                        "next from $($before[0])/$($before[1]) gave $($after[0]), expected $exp" `
                        @{ from = $before; got = $after })
                }
            }
            'prev' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                Press-Prev
                $c = Wait-Counter 4000 250
                $after = Get-CounterParts $c
                $detail = "prev -> '$c'"
                if ($null -ne $before -and $null -ne $after -and $before[1] -gt 0) {
                    $exp = if ($before[0] -le 1) { $before[1] } else { $before[0] - 1 }
                    [void](Assert-That ($after[0] -eq $exp) 'nav-not-sequential' `
                        "prev from $($before[0])/$($before[1]) gave $($after[0]), expected $exp" `
                        @{ from = $before; got = $after })
                }
            }
            'clear' {
                Set-Needle ''
                Start-Sleep -Milliseconds 500
                $c = Get-Counter (Get-Root)
                $detail = "cleared -> '$c'"
                [void](Assert-That ($c -eq '') 'counter-not-cleared' `
                    "empty needle left the counter showing '$c'" @{ counter = $c })
            }
            'close' {
                $useEsc = $rng.Next(2) -eq 0
                if ($useEsc) { Press-Escape } else { Close-SearchBar }
                $detail = if ($useEsc) { 'esc' } else { 'button' }
                [void](Assert-That (-not (Test-SearchBarOpen (Get-Root))) 'esc-did-not-close' `
                    "close via $detail left the bar open" @{ via = $detail })

                # A torn-down search must leave no counter at all. Anything
                # else means the teardown sentinel is being rendered as a
                # number again ("0 of -1").
                $after = Get-Counter (Get-Root)
                [void](Assert-That ($after -eq '') 'counter-survived-close' `
                    "closing via $detail left the counter showing '$after'" `
                    @{ via = $detail; counter = $after })
            }
            'scroll' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                $notches = $rng.Next(-6, 7)
                $detail = "wheel $notches"
                if ($notches -ne 0) {
                    $s = Invoke-SeamCommand $script:Session @{ op = 'scroll'; notches = $notches }
                    $v = $s.viewport
                    $detail += " offset $($v.offsetBefore)->$($v.offsetAfter) of $($v.total)/$($v.len)"
                    # Positive notches scroll up (towards row 0), negative
                    # down (towards total-len). Only a viewport already at
                    # that end may stay put.
                    $bottom = [Math]::Max(0, [double]$v.total - [double]$v.len)
                    $canMove = if ($notches -gt 0) { $v.offsetBefore -gt 0 } else { $v.offsetBefore -lt $bottom }
                    $moved = if ($notches -gt 0) { $v.offsetAfter -lt $v.offsetBefore } else { $v.offsetAfter -gt $v.offsetBefore }
                    if ($canMove) {
                        [void](Assert-That $moved 'scroll-did-not-move' `
                            "a $notches-notch wheel left the viewport at row $($v.offsetAfter) of $bottom" `
                            @{ notches = $notches; viewport = $v })
                    }
                }
                Start-Sleep -Milliseconds 500
                $after = Get-CounterParts (Get-Counter (Get-Root))
                if ($null -ne $before -and $null -ne $after) {
                    [void](Assert-That ($after[1] -eq $before[1]) 'total-changed-on-scroll' `
                        "scrolling changed the total from $($before[1]) to $($after[1])" `
                        @{ before = $before[1]; after = $after[1] })
                }
            }
            'resize' {
                $before = Get-CounterParts (Get-Counter (Get-Root))
                $w = $rng.Next(700, 1400); $h = $rng.Next(500, 900)
                $rc = [SeamWin]::RectOf($script:Hwnd64)
                if ($null -eq $rc -or -not [SeamWin]::MoveWindow([SeamWin]::P($script:Hwnd64), $rc.L, $rc.T, $w, $h, $true)) {
                    Add-Finding 'harness' "resize to ${w}x${h} failed" @{}
                    break
                }
                Start-Sleep -Milliseconds 900
                $after = Get-CounterParts (Get-Counter (Get-Root))
                $detail = "resize ${w}x${h}"
                # Reflow can legitimately change how content wraps, so the doc
                # is re-read and the oracle recomputed rather than assumed.
                $doc = Get-TerminalText (Get-Term)
                if ($null -ne $before -and $null -ne $after -and $before[1] -ne $after[1]) {
                    $result.ops += [ordered]@{ i = $script:Iter; op = 'resize-total-drift'; before = $before[1]; after = $after[1] }
                }
            }
            'emit' {
                # Append matching content, then reopen and search again: the
                # total must count it.
                Close-SearchBar
                $emit = '$a=' + "'ZQ'+'XW'; 1..12 | % { `"`$a extra `$_`" }"
                # The initial seed clears the row first and this has to as
                # well: anything already sitting there is prefixed to the
                # payload, and "leftover + line" still contains the line, so
                # the read-back would bless a command the shell cannot run.
                Clear-CommandLine
                $seeded = Send-SeedText $emit (Join-Path $OutDir 'doc-emit.txt')
                if ($seeded -eq 'bar-open') {
                    # The bar not closing is a product defect this harness
                    # names elsewhere. Record it BEFORE the throw: the verdict
                    # is derived from every finding, so this keeps a live
                    # regression at exit 2 instead of a retried harness miss.
                    [void](Assert-That $false 'esc-did-not-close' `
                        'the close button left the search bar open, so the emit payload could not be sent' @{})
                }
                if ($seeded -ne 'ok') {
                    throw "the emit payload never landed on the input row ($seeded), see doc-emit.txt"
                }
                Send-Shell "`r"
                Start-Sleep -Seconds 3
                $doc = Get-TerminalText (Get-Term)
                Open-SearchBar
                Set-Needle 'ZQXW'
                Start-Sleep -Milliseconds 400
                $c = Wait-Counter
                $detail = "emit -> '$c'"
                Assert-CounterMatchesOracle 'ZQXW' $c
            }
        }

        $result.ops += [ordered]@{ i = $script:Iter; op = $op; detail = $detail }
        Write-Host ("  {0,3}  {1,-7} {2}" -f $script:Iter, $op, $detail)
    }

    Assert-Alive 'end of run'
}
catch {
    Add-Finding 'harness' $_.Exception.Message @{}
    Write-Host "HARNESS ERROR: $($_.Exception.Message)" -ForegroundColor Yellow
}
finally {

    # crash.log is append-only across every run on this machine, so only the
    # bytes this run added are evidence about this run. Slice as bytes: the
    # baseline is a file length, and using it as a character index reads the
    # wrong text and throws outright once the log contains any non-ASCII.
    try {
        if (Test-Path $crashPath) {
            $now = (Get-Item $crashPath).Length
            if ($now -gt $script:CrashBaseline) {
                $bytes = [System.IO.File]::ReadAllBytes($crashPath)
                $fresh = [System.Text.Encoding]::UTF8.GetString(
                    $bytes, $script:CrashBaseline, $bytes.Length - $script:CrashBaseline)
                $fresh | Set-Content (Join-Path $OutDir "crash-$Seed.log") -Encoding utf8
                Add-Finding 'crash' "crash.log grew by $($now - $script:CrashBaseline) bytes during this run" @{}
            }
        }
    } catch {
        Add-Finding 'harness' "could not read crash.log: $($_.Exception.Message)" @{}
    }

    $result.checks = $script:Checks
    $result.findings = @($script:Findings)
    $result.failed = $script:Findings.Count
    # Decide the verdict before any cleanup runs, so nothing thrown on the
    # way down can change what the run reports.
    #
    # 2 means the product is broken, 1 means the run never got far enough to
    # judge it and should be retried rather than filed. Same numbering as the
    # rest of the harnesses.
    $product = @($script:Findings | Where-Object { $_.kind -ne 'harness' })
    if ($script:Findings.Count -eq 0) { $script:ExitCode = 0 }
    elseif ($product.Count -gt 0) { $script:ExitCode = 2 }
    else { $script:ExitCode = 1 }

    # A run that never got to assert anything has nothing to record, and
    # writing it would overwrite the last real run's results.
    if ($script:Checks -gt 0) {
        $result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutDir "run-$Seed.json") -Encoding utf8
    }

    # Tear down only what this run started: the session's own process and
    # anything else that came up from the same exe during the run. -KeepOpen
    # leaves the app and its staged root up on purpose.
    if (-not $KeepOpen -and $null -ne $script:Session) {
        try { Stop-SeamSession $script:Session }
        catch { Add-Finding 'harness' "teardown: $($_.Exception.Message)" @{} }
    }

    Write-Host ""
    if ($script:Findings.Count -eq 0) {
        Write-Host "PASS  $($script:Checks) checks, 0 findings (seed $Seed)" -ForegroundColor Green
    } else {
        Write-Host "FAIL  $($script:Findings.Count) findings across $($script:Checks) checks (seed $Seed)" -ForegroundColor Red
        $script:Findings | Group-Object kind | Sort-Object Count -Descending | ForEach-Object {
            Write-Host ("  {0,3}x {1}" -f $_.Count, $_.Name)
        }
    }

}

exit $script:ExitCode
