#requires -Version 7
<#
    Prove the backdrop stage is an instrument before a matrix trusts it.

    What is checked, against the screen rather than the stage's own answer:
    the window comes up where it was told to and never takes the foreground,
    not at launch and not after any place; every scene in the catalogue
    paints something that reads back as that scene (a solid is its colour, a
    checker is black-or-white, a gradient and a photograph vary between two
    points, the editor's band is its grey); a place to a rect DISJOINT from
    the first moves the pixels with it; a place bigger than every monitor
    together is honoured; a refused op does not kill it; quit exits 0.

    Every pixel read is preceded by asking Windows whose window is at that
    point. A foreign window over the stage is reported as an occlusion, not
    scored as a wrong colour. Scenes are regenerated on every run: a cached
    picture from last time would make the generator's checks vacuous.

    Launches no Wintty, sets no wallpaper, and is safe to run with anything
    else open.

    Exit 0 when the instrument is sound, 1 when it is not or could not be
    started. There is no 2: this measures the instrument, never the product.
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'backdrop-stage-selftest')
)
. (Join-Path $PSScriptRoot 'lib/backdrop-stage.ps1')
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
[void][StageWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [bool]$Pass, [string]$Detail) {
    $checks.Add([pscustomobject]@{ check = $Name; pass = $Pass; detail = $Detail })
    $mark = if ($Pass) { 'ok  ' } else { 'FAIL' }
    Write-Host ("  {0} {1,-28} {2}" -f $mark, $Name, $Detail)
    if (-not $Pass) { throw "${Name}: $Detail" }
}

# A pixel the stage owns, or an occlusion reported by name.
function Read-StagePixel([int]$X, [int]$Y) {
    $owner = Get-WindowPidAt -X $X -Y $Y
    if ($owner -ne [uint32]$script:stage.Proc.Id) {
        $who = try { (Get-Process -Id $owner -ErrorAction Stop).ProcessName } catch { '?' }
        throw "OCCLUDED: the point $X,$Y belongs to pid $owner ($who), not the stage; nothing was measured there"
    }
    return Get-ScreenPixel -X $X -Y $Y
}

function Assert-NotForeground([string]$After) {
    $fg = [StageWin]::GetForegroundWindow().ToInt64()
    Add-Check "never-activates-$After" ($fg -ne $script:stage.Hwnd64) "foreground $fg, stage $($script:stage.Hwnd64)"
}

$script:stage = $null
$exitCode = 1
try {
    $X = 140; $Y = 140; $W = 480; $H = 320
    $script:stage = Start-BackdropStage -X $X -Y $Y -W $W -H $H

    $q = Invoke-BackdropStage $script:stage @{ op = 'query' }
    Add-Check 'placed-where-told' ($q.x -eq $X -and $q.y -eq $Y -and $q.w -eq $W -and $q.h -eq $H) `
        ("asked {0},{1} {2}x{3} got {4},{5} {6}x{7}" -f $X, $Y, $W, $H, $q.x, $q.y, $q.w, $q.h)
    Add-Check 'hwnd-reported' ($q.hwnd -eq $script:stage.Hwnd64) "READY said $($script:stage.Hwnd64), query says $($q.hwnd)"
    Assert-NotForeground 'launch'

    $sceneDir = Join-Path $OutDir 'scenes'
    Remove-Item $sceneDir -Recurse -Force -ErrorAction SilentlyContinue
    $cx = $X + [int]($W / 2); $cy = $Y + [int]($H / 2)
    foreach ($scene in (Get-BackdropScenes).Values) {
        $png = Set-BackdropScene -Stage $script:stage -Scene $scene -SceneDir $sceneDir -Width 960 -Height 540
        $centre = Read-StagePixel $cx $cy
        switch ($scene.Kind) {
            'solid' {
                Add-Check "scene-$($scene.Name)" (Test-PixelNear $centre $scene.Color 3) "centre $($centre.Hex) expected $($scene.Color)"
            }
            default {
                switch ($scene.Name) {
                    'checker' {
                        $p2 = Read-StagePixel ($cx + 3) $cy
                        $bw = { param($p) (Test-PixelNear $p '#000000' 40) -or (Test-PixelNear $p '#FFFFFF' 40) }
                        Add-Check 'scene-checker' ((& $bw $centre) -and (& $bw $p2)) "centre $($centre.Hex), +3px $($p2.Hex): both must be near black or white"
                    }
                    'editor' {
                        # The minimap band on the right is the one text-free
                        # surface in the scene (the gutter carries line numbers,
                        # and a sample there reads a glyph's anti-aliasing).
                        $band = Read-StagePixel ($X + $W - 6) ($Y + 40)
                        Add-Check 'scene-editor' (Test-PixelNear $band '#F8F8F8' 6) "minimap band $($band.Hex) expected #F8F8F8"
                    }
                    default {
                        $top = Read-StagePixel $cx ($Y + 8)
                        $bot = Read-StagePixel $cx ($Y + $H - 8)
                        $gap = [Math]::Abs($top.R - $bot.R) + [Math]::Abs($top.G - $bot.G) + [Math]::Abs($top.B - $bot.B)
                        Add-Check "scene-$($scene.Name)" ($gap -gt 40) "top $($top.Hex) bottom $($bot.Hex) differ by $gap (needs > 40)"
                    }
                }
            }
        }
        Add-Check "png-$($scene.Name)" (Test-Path -LiteralPath $png) $png
    }

    # Move it to a rect that shares no pixel with the first, paint a colour
    # no earlier scene left behind, and read the new centre: a place that
    # silently did nothing cannot pass this.
    $X2 = $X + $W + 40; $Y2 = 200; $W2 = 300; $H2 = 200
    $q2 = Invoke-BackdropStage $script:stage @{ op = 'place'; x = $X2; y = $Y2; w = $W2; h = $H2 }
    Add-Check 'place-moves' ($q2.x -eq $X2 -and $q2.y -eq $Y2 -and $q2.w -eq $W2 -and $q2.h -eq $H2) `
        ("asked {0},{1} {2}x{3} got {4},{5} {6}x{7}" -f $X2, $Y2, $W2, $H2, $q2.x, $q2.y, $q2.w, $q2.h)
    Assert-NotForeground 'place'
    [void](Invoke-BackdropStage $script:stage @{ op = 'solid'; color = '#0067C0' })
    $moved = Read-StagePixel ($X2 + [int]($W2 / 2)) ($Y2 + [int]($H2 / 2))
    Add-Check 'pixels-follow' (Test-PixelNear $moved '#0067C0' 3) "new centre $($moved.Hex) expected #0067C0"

    # Bigger than every monitor together. A harness grows the stage past the
    # window under test on every side, and a window that fills the screen
    # needs a stage that overhangs it; Windows clamps a window to the monitor
    # unless the stage overrides WM_GETMINMAXINFO, and this is the check.
    $vs = Get-VirtualScreenRect
    $bigX = $vs.X - 200; $bigY = $vs.Y - 200; $bigW = $vs.W + 400; $bigH = $vs.H + 400
    $q3 = Invoke-BackdropStage $script:stage @{ op = 'place'; x = $bigX; y = $bigY; w = $bigW; h = $bigH }
    Add-Check 'bigger-than-screen' ($q3.x -eq $bigX -and $q3.y -eq $bigY -and $q3.w -eq $bigW -and $q3.h -eq $bigH) `
        ("virtual screen {0}x{1}; asked {2},{3} {4}x{5} got {6},{7} {8}x{9}" -f $vs.W, $vs.H, $bigX, $bigY, $bigW, $bigH, $q3.x, $q3.y, $q3.w, $q3.h)
    Assert-NotForeground 'oversize'
    [void](Invoke-BackdropStage $script:stage @{ op = 'place'; x = $X2; y = $Y2; w = $W2; h = $H2 })

    # Refusals are answered, not crashed on.
    $refused = $false
    try { [void](Invoke-BackdropStage $script:stage @{ op = 'image'; path = 'C:\no\such\file.png'; mode = 'stretch' }) }
    catch { $refused = "$_" -like '*no such image*' }
    Add-Check 'refuses-missing-image' $refused 'image op with a missing path is refused with a reason'
    Add-Check 'survives-refusal' (-not $script:stage.Proc.HasExited) 'the stage is still up after a refused op'

    Stop-BackdropStage $script:stage
    $code = $script:stage.Proc.ExitCode
    Add-Check 'quit-exits-zero' ($code -eq 0) "exit code $code"
    $script:stage = $null

    [pscustomobject]@{ checks = $checks } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
    Write-Host "backdrop-stage: $($checks.Count) checks passed"
    $exitCode = 0
}
catch {
    Write-Host "STAGE_SELFTEST_FAIL: $_" -ForegroundColor Red
}
finally {
    # Every exit path, a throw and a Ctrl+C included, takes the stage down:
    # its window has no taskbar entry and cannot be focused for Alt+F4.
    if ($null -ne $script:stage) { try { Stop-BackdropStage $script:stage } catch { } }
}
exit $exitCode
