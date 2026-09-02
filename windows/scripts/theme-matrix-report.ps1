#requires -Version 7
<#
    Turn a theme-matrix.ps1 run into the markdown that gets pasted into #937.

    The matrix cell is the WORST judged ratio across every layout and scene
    the cell was photographed in, so a green cell is green everywhere it was
    looked at. A cell with an unmeasured surface shows a ? beside its number:
    a number nothing is known about is not a pass.

    Reads <RunDir>/result.json, writes <RunDir>/matrix.md, prints the path.
    Exit 0 when written, 1 when there is no result to read.
#>
param(
    [Parameter(Mandatory)][string]$RunDir,
    [string]$OutFile = ''
)
$ErrorActionPreference = 'Stop'
$resultPath = Join-Path $RunDir 'result.json'
if (-not (Test-Path -LiteralPath $resultPath)) { Write-Host "no result.json under $RunDir"; exit 1 }
if (-not $OutFile) { $OutFile = Join-Path $RunDir 'matrix.md' }
$r = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json

$rows = @($r.rows); $findings = @($r.findings); $unmeasured = @($r.unmeasured); $deltas = @($r.deltas)
# Invariant, round-trip: the machine's own culture read the ISO date with
# its day and month swapped.
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$started = [datetime]::Parse($r.startedUtc, $inv, [System.Globalization.DateTimeStyles]::RoundtripKind)
$finished = [datetime]::Parse($r.finishedUtc, $inv, [System.Globalization.DateTimeStyles]::RoundtripKind)
$md = [System.Text.StringBuilder]::new()
function L([string]$s = '') { [void]$md.AppendLine($s) }

L "## theme matrix run $($started.ToString('yyyy-MM-dd HH:mm')) UTC"
L
L ("build ``{0}``, {1} min, {2} rows, **{3} finding(s)**, {4} unmeasured{5}" -f
    $r.buildSha, [Math]::Round(($finished - $started).TotalMinutes, 1), $rows.Count, $findings.Count, $unmeasured.Count,
    $(if ($r.fatal) { ", RUN FAILED: $($r.fatal)" } else { '' }))
L
L ("desktop was **{0}** with wallpaper ``{1}``{2}; filters: theme={3} polarity={4} app={5} frame={6} layout={7} scene={8}{9}{10}" -f
    $r.machine.polarityBefore, $r.machine.wallpaperBefore, $(if ($r.machine.noFlip) { ' (no flip)' } else { '' }),
    ($r.filters.theme -join ','), ($r.filters.polarity -join ','), ($r.filters.app -join ','), ($r.filters.frame -join ','),
    ($r.filters.layout -join ','), ($r.filters.scene -join ','),
    $(if ($r.filters.maxCells -gt 0) { " maxCells=$($r.filters.maxCells)" } else { '' }),
    $(if ($r.filters.mutate -ne 'none') { " **MUTATED ($($r.filters.mutate))**" } else { '' }))
if (@($r.axes.skippedThemes).Count -gt 0) { L; L ("not in the catalogue, skipped: {0}" -f (@($r.axes.skippedThemes) -join ', ')) }
L
L "Cell = worst judged ratio over layouts x scenes (text >= 4.5, glyph >= 3.0, field <= 1.05). Bold = a failure in the cell; ? = a surface went unmeasured."

$columns = @()
foreach ($a in $r.axes.apps) { foreach ($f in $r.axes.frames) { $columns += "$a/$f" } }

foreach ($polarity in $r.axes.polarities) {
    L
    L "### desktop $polarity"
    L
    L ('| theme | ' + ($columns -join ' | ') + ' |')
    L ('|---|' + (($columns | ForEach-Object { '---:' }) -join '|') + '|')
    foreach ($theme in $r.axes.themes) {
        $line = "| $theme |"
        foreach ($col in $columns) {
            $app, $frame = $col -split '/'
            $inCell = @($rows | Where-Object { $_.polarity -eq $polarity -and $_.theme -eq $theme -and $_.app -eq $app -and $_.frame -eq $frame })
            $missing = @($unmeasured | Where-Object { $_.polarity -eq $polarity -and $_.theme -eq $theme -and $_.app -eq $app -and $_.frame -eq $frame }).Count
            if ($inCell.Count -eq 0) { $line += $(if ($missing -gt 0) { ' ? |' } else { ' - |' }); continue }
            # Worst = the lowest text/glyph ratio; a field failure is reported
            # by its count since its scale runs the other way.
            $judged = @($inCell | Where-Object { $_.class -ne 'field' })
            $worst = $(if ($judged.Count -gt 0) { ($judged | Measure-Object -Property ratio -Minimum).Minimum } else { $null })
            $fails = @($inCell | Where-Object { -not $_.pass }).Count
            $text = $(if ($null -ne $worst) { '{0:N2}' -f $worst } else { 'field' })
            if ($fails -gt 0) { $text = "**$text** ($fails)" }
            if ($missing -gt 0) { $text += ' ?' }
            $line += " $text |"
        }
        L $line
    }
}

if ($deltas.Count -gt 0) {
    L
    L "### materials, measured (#897): mean ratio of the strip ground against the terminal and against the scene"
    L
    L '| desktop | app/frame | strip vs terminal | strip vs scene | n |'
    L '|---|---|---:|---:|---:|'
    foreach ($polarity in $r.axes.polarities) { foreach ($col in $columns) {
        $app, $frame = $col -split '/'
        $d = @($deltas | Where-Object { $_.polarity -eq $polarity -and $_.app -eq $app -and $_.frame -eq $frame })
        if ($d.Count -eq 0) { continue }
        $vt = @($d | Where-Object delta -eq 'strip-vs-terminal' | Measure-Object -Property ratio -Average).Average
        $vs = @($d | Where-Object delta -eq 'strip-vs-scene')
        $vsText = $(if ($vs.Count -gt 0) { '{0:N2}' -f ($vs | Measure-Object -Property ratio -Average).Average } else { '-' })
        L ('| {0} | {1} | {2:N2} | {3} | {4} |' -f $polarity, $col, $vt, $vsText, $d.Count)
    } }
}

if ($findings.Count -gt 0) {
    L
    L "### findings ($($findings.Count))"
    L
    L '| desktop | theme | app/frame | layout | scene | surface | ratio | floor | ink on ground |'
    L '|---|---|---|---|---|---|---:|---:|---|'
    foreach ($f in ($findings | Sort-Object polarity, theme, app, frame, layout, scene, surface)) {
        L ('| {0} | {1} | {2}/{3} | {4} | {5} | {6} | {7:N2} | {8} | {9} on {10} |' -f
            $f.polarity, $f.theme, $f.app, $f.frame, $f.layout, $f.scene, $f.surface, $f.ratio, $f.min, $f.fg, $f.bg)
    }
}

if ($unmeasured.Count -gt 0) {
    L
    L ('<details><summary>unmeasured ({0})</summary>' -f $unmeasured.Count)
    L
    foreach ($u in $unmeasured) { L ('- {0} / {1} / {2}/{3} / {4} / {5} / {6}: {7}' -f $u.polarity, $u.theme, $u.app, $u.frame, $u.layout, $u.scene, $u.surface, $u.why) }
    L
    L '</details>'
}

[IO.File]::WriteAllText($OutFile, $md.ToString())
Write-Host $OutFile
exit 0
