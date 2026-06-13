#!/usr/bin/env pwsh
# Generates the bundled default bell sound (Ghostty/Assets/bell.wav).
#
# Original synthesis (no third-party samples, no licensing): an ascending
# four-note glockenspiel/music-box arpeggio in Bb major, evoking the
# classic Windows chime family without reproducing any actual system sound.
# Each note is additive struck-bar synthesis (near-harmonic partials with a
# fast exponential decay) staggered so the notes ring together like a chime.
#
# Run from anywhere; writes relative to this script's location.
# Deterministic: same output every run, so the committed asset is reproducible.

$ErrorActionPreference = 'Stop'

$outPath = Join-Path $PSScriptRoot '..\Ghostty\Assets\bell.wav'
$outDir = Split-Path $outPath -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$sr = 44100
$totalDur = 1.5
$noteDecay = 0.55
$gain = 0.6
$n = [int]($sr * $totalDur)

# Glockenspiel / struck-metal-bar partials: ratio, amplitude.
$partials = @(@(1.0, 1.0), @(2.76, 0.45), @(5.40, 0.22), @(8.93, 0.10))

# Ascending Bb5, D6, F6, Bb6 (Bb-major arpeggio); onset times in seconds.
$notes = @(
    @(0.00, 932.33),
    @(0.13, 1174.66),
    @(0.26, 1396.91),
    @(0.39, 1864.66)
)

$samples = New-Object double[] $n
$twoPi = [Math]::PI * 2
foreach ($note in $notes) {
    $onset = [int]($note[0] * $sr)
    $f0 = $note[1]
    foreach ($p in $partials) {
        $f = $f0 * $p[0]
        $a = $p[1]
        for ($i = $onset; $i -lt $n; $i++) {
            $t = ($i - $onset) / $sr
            $env = [Math]::Exp(-$t / $noteDecay)
            $samples[$i] += $a * $env * [Math]::Sin($twoPi * $f * $t)
        }
    }
}

# Normalize to the requested gain and apply a 2ms attack ramp (anti-click).
$max = 0.0
foreach ($s in $samples) { if ([Math]::Abs($s) -gt $max) { $max = [Math]::Abs($s) } }
$atk = [int]($sr * 0.002)
$bytes = New-Object byte[] ($n * 2)
for ($i = 0; $i -lt $n; $i++) {
    $v = $samples[$i] / $max * $gain
    if ($i -lt $atk) { $v *= ($i / $atk) }
    $iv = [int]([Math]::Round($v * 32767))
    if ($iv -gt 32767) { $iv = 32767 }
    if ($iv -lt -32768) { $iv = -32768 }
    $b = [BitConverter]::GetBytes([int16]$iv)
    $bytes[$i * 2] = $b[0]
    $bytes[$i * 2 + 1] = $b[1]
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $bytes.Length)); $bw.Write([char[]]'WAVE')
$bw.Write([char[]]'fmt '); $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
$bw.Write([int]$sr); $bw.Write([int]($sr * 2)); $bw.Write([int16]2); $bw.Write([int16]16)
$bw.Write([char[]]'data'); $bw.Write([int]$bytes.Length); $bw.Write($bytes)
[System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host ("Wrote {0} ({1:N0} bytes)" -f $outPath, (Get-Item $outPath).Length)
