#requires -Version 7
<#
    Prove the backdrop stage is an instrument before a matrix trusts it.

    What is checked, against the screen rather than the stage's own answer:
    the window comes up where it was told to and never takes the foreground;
    every scene in the catalogue paints something that reads back as that
    scene (a solid is its colour, a checker is black-or-white, a gradient and
    a photograph vary between two points, the editor's gutter is its grey);
    a place moves it and the pixels follow; quit ends the process with 0.

    Launches no Wintty, sets no wallpaper, and is safe to run with anything
    else open: the stage is topmost while it is up, so the pixels it is
    judged on are its own unless another topmost window sits on the same
    spot, which is reported rather than scored.

    Exit 0 when the instrument is sound, 1 when it is not or could not be
    started. There is no 2: this measures the instrument, never the product.
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'backdrop-stage-selftest')
)
. (Join-Path $PSScriptRoot 'lib/backdrop-stage.ps1')
$ErrorActionPreference = 'Stop'

trap {
    Write-Host "STAGE_SELFTEST_FAIL: $_" -ForegroundColor Red
    if ($null -ne $script:stage) { try { Stop-BackdropStage $script:stage } catch { } }
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
[void][StageWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [bool]$Pass, [string]$Detail) {
    $checks.Add([pscustomobject]@{ check = $Name; pass = $Pass; detail = $Detail })
    $mark = if ($Pass) { 'ok  ' } else { 'FAIL' }
    Write-Host ("  {0} {1,-28} {2}" -f $mark, $Name, $Detail)
    if (-not $Pass) { throw "${Name}: $Detail" }
}

$X = 140; $Y = 140; $W = 480; $H = 320
$foregroundBefore = [StageWin]::GetForegroundWindow()
$script:stage = Start-BackdropStage -X $X -Y $Y -W $W -H $H

$q = Invoke-BackdropStage $script:stage @{ op = 'query' }
Add-Check 'placed-where-told' ($q.x -eq $X -and $q.y -eq $Y -and $q.w -eq $W -and $q.h -eq $H) `
    ("asked {0},{1} {2}x{3} got {4},{5} {6}x{7}" -f $X, $Y, $W, $H, $q.x, $q.y, $q.w, $q.h)
Add-Check 'hwnd-reported' ($q.hwnd -eq $script:stage.Hwnd64) "READY said $($script:stage.Hwnd64), query says $($q.hwnd)"

$fgNow = [StageWin]::GetForegroundWindow()
Add-Check 'never-activates' ($fgNow.ToInt64() -ne $script:stage.Hwnd64) `
    ("foreground before {0}, now {1}, stage {2}" -f $foregroundBefore.ToInt64(), $fgNow.ToInt64(), $script:stage.Hwnd64)

Start-Sleep -Milliseconds 300
$sceneDir = Join-Path $OutDir 'scenes'
$cx = $X + [int]($W / 2); $cy = $Y + [int]($H / 2)
foreach ($scene in (Get-BackdropScenes).Values) {
    $png = Set-BackdropScene -Stage $script:stage -Scene $scene -SceneDir $sceneDir -Width 960 -Height 540
    Start-Sleep -Milliseconds 120
    $centre = Get-ScreenPixel -X $cx -Y $cy
    switch ($scene.Kind) {
        'solid' {
            Add-Check "scene-$($scene.Name)" (Test-PixelNear $centre $scene.Color 3) "centre $($centre.Hex) expected $($scene.Color)"
        }
        default {
            switch ($scene.Name) {
                'checker' {
                    $p2 = Get-ScreenPixel -X ($cx + 3) -Y $cy
                    $bw = { param($p) (Test-PixelNear $p '#000000' 40) -or (Test-PixelNear $p '#FFFFFF' 40) }
                    Add-Check 'scene-checker' ((& $bw $centre) -and (& $bw $p2)) "centre $($centre.Hex), +3px $($p2.Hex): both must be near black or white"
                }
                'editor' {
                    # The minimap band on the right is the one text-free
                    # surface in the scene (the gutter carries line numbers,
                    # and a sample there reads a glyph's anti-aliasing).
                    $band = Get-ScreenPixel -X ($X + $W - 6) -Y ($Y + 40)
                    Add-Check 'scene-editor' (Test-PixelNear $band '#F8F8F8' 6) "minimap band $($band.Hex) expected #F8F8F8"
                }
                default {
                    $top = Get-ScreenPixel -X $cx -Y ($Y + 8)
                    $bot = Get-ScreenPixel -X $cx -Y ($Y + $H - 8)
                    $gap = [Math]::Abs($top.R - $bot.R) + [Math]::Abs($top.G - $bot.G) + [Math]::Abs($top.B - $bot.B)
                    Add-Check "scene-$($scene.Name)" ($gap -gt 40) "top $($top.Hex) bottom $($bot.Hex) differ by $gap (needs > 40)"
                }
            }
        }
    }
    Add-Check "png-$($scene.Name)" (Test-Path -LiteralPath $png) $png
}

# Move it and read the moved pixels: the last scene painted was the checker,
# so the new centre must still be black-or-white.
$X2 = 260; $Y2 = 200; $W2 = 300; $H2 = 200
$q2 = Invoke-BackdropStage $script:stage @{ op = 'place'; x = $X2; y = $Y2; w = $W2; h = $H2 }
Add-Check 'place-moves' ($q2.x -eq $X2 -and $q2.y -eq $Y2 -and $q2.w -eq $W2 -and $q2.h -eq $H2) `
    ("asked {0},{1} {2}x{3} got {4},{5} {6}x{7}" -f $X2, $Y2, $W2, $H2, $q2.x, $q2.y, $q2.w, $q2.h)
Start-Sleep -Milliseconds 120
$moved = Get-ScreenPixel -X ($X2 + [int]($W2 / 2)) -Y ($Y2 + [int]($H2 / 2))
Add-Check 'pixels-follow' ((Test-PixelNear $moved '#000000' 40) -or (Test-PixelNear $moved '#FFFFFF' 40)) "new centre $($moved.Hex)"

# Bigger than every monitor together. A harness grows the stage past the
# window under test on every side, and a window that fills the screen needs
# a stage that overhangs it; Windows clamps a window to the monitor unless
# the stage overrides WM_GETMINMAXINFO, and this is the check that it does.
$vs = Get-VirtualScreenRect
$bigX = $vs.X - 200; $bigY = $vs.Y - 200; $bigW = $vs.W + 400; $bigH = $vs.H + 400
$q3 = Invoke-BackdropStage $script:stage @{ op = 'place'; x = $bigX; y = $bigY; w = $bigW; h = $bigH }
Add-Check 'bigger-than-screen' ($q3.x -eq $bigX -and $q3.y -eq $bigY -and $q3.w -eq $bigW -and $q3.h -eq $bigH) `
    ("virtual screen {0}x{1}; asked {2},{3} {4}x{5} got {6},{7} {8}x{9}" -f $vs.W, $vs.H, $bigX, $bigY, $bigW, $bigH, $q3.x, $q3.y, $q3.w, $q3.h)
[void](Invoke-BackdropStage $script:stage @{ op = 'place'; x = $X2; y = $Y2; w = $W2; h = $H2 })

# Refusals are answered, not crashed on.
$refused = $false
try { [void](Invoke-BackdropStage $script:stage @{ op = 'image'; path = 'C:\no\such\file.png'; mode = 'stretch' }) }
catch { $refused = "$_" -like '*no such image*' }
Add-Check 'refuses-missing-image' $refused 'image op with a missing path is refused with a reason'
$alive = -not $script:stage.Proc.HasExited
Add-Check 'survives-refusal' $alive 'the stage is still up after a refused op'

Stop-BackdropStage $script:stage
$code = $script:stage.Proc.ExitCode
Add-Check 'quit-exits-zero' ($code -eq 0) "exit code $code"
$script:stage = $null

[pscustomobject]@{ checks = $checks } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'result.json') -Encoding utf8
Write-Host "backdrop-stage: $($checks.Count) checks passed"
exit 0
