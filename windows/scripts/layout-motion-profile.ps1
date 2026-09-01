#requires -Version 7
<#
Motion profile of a filmed layout switch: what moved, when, and where.

Reads one leg of a layout-switch-filmstrip run (the frames plus the
-film.json sidecar with the camera's timestamps) and prints, for every
consecutive frame pair:

  t          the later frame's time since the toggle fired (ms)
  dt         gap to the previous frame (ms) -- presentation holes show
             here, because the camera delivers on present
  motion     mean absolute channel delta across the sampled pixels; the
             amplitude of whatever changed
  changedPct share of sampled pixels that changed beyond a small threshold
  box        bounding box of the changed pixels, in window coordinates

The box is the column that finds misdirected motion. It is how the
impact nudge was caught translating the whole window -- change boxes of
(4,4)-(1264,808) at four to six times the switch's own amplitude, when
the accent was meant to be a few pixels on one strip -- and how the fix
was verified: after retargeting, no post-switch box may be wider than
the struck strip's own band.

Sampled every 4th pixel on each axis with an 8-bit-sum threshold of 24,
which is plenty to localize chrome motion and cheap enough to run over a
whole leg in seconds. Not a smoothness oracle: at the camera's delivery
rate a 140ms accent is one or two frames, so this tells you WHERE motion
went and roughly how big it was, and the filmstrip's state track tells
you what the strips were holding while it happened.

Usage:
  .\layout-motion-profile.ps1 -RunDir <filmstrip out dir> -Tag 02-vertical-to-horizontal
#>
param(
    [Parameter(Mandatory)] [string]$RunDir,
    [Parameter(Mandatory)] [string]$Tag
)
Add-Type -AssemblyName System.Drawing

$film = Get-Content (Join-Path $RunDir "$Tag-film.json") -Raw | ConvertFrom-Json

function Get-Pixels([string]$path, [ref]$OutW, [ref]$OutH) {
    $bmp = [System.Drawing.Bitmap]::new($path)
    try {
        $bw = $bmp.Width; $bh = $bmp.Height
        $rect = [System.Drawing.Rectangle]::new(0, 0, $bw, $bh)
        $data = $bmp.LockBits($rect,
            [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $len = $data.Stride * $bh
            $bytes = [byte[]]::new($len)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $len)
            $OutW.Value = $bw; $OutH.Value = $bh
            return ,$bytes
        } finally { $bmp.UnlockBits($data) }
    } finally { $bmp.Dispose() }
}

$prev = $null
$prevT = -1
foreach ($f in $film) {
    $w = 0; $h = 0
    $px = Get-Pixels (Join-Path $RunDir $f.file) ([ref]$w) ([ref]$h)
    if ($null -ne $prev -and $px.Length -eq $prev.Length) {
        [long]$sum = 0; [int]$changed = 0; [int]$n = 0
        $minX = $w; $minY = $h; $maxX = -1; $maxY = -1
        $stride = $w * 4
        for ($y = 0; $y -lt $h; $y += 4) {
            $row = $y * $stride
            for ($x = 0; $x -lt $w; $x += 4) {
                $i = $row + ($x * 4)
                $d = [Math]::Abs([int]$px[$i] - [int]$prev[$i]) +
                     [Math]::Abs([int]$px[$i+1] - [int]$prev[$i+1]) +
                     [Math]::Abs([int]$px[$i+2] - [int]$prev[$i+2])
                $sum += $d
                $n++
                if ($d -gt 24) {
                    $changed++
                    if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                    if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
                }
            }
        }
        $box = if ($maxX -ge 0) {
            "({0},{1})-({2},{3})" -f $minX, $minY, $maxX, $maxY
        } else { "-" }
        [pscustomobject]@{
            t          = [Math]::Round($f.sinceStartMs, 1)
            dt         = [Math]::Round($f.sinceStartMs - $prevT, 1)
            motion     = [Math]::Round($sum / $n, 2)
            changedPct = [Math]::Round(100.0 * $changed / $n, 1)
            box        = $box
        }
    }
    $prev = $px; $prevT = $f.sinceStartMs
}
