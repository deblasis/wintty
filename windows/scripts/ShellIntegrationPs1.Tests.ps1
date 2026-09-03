# Tests for the payload-building half of the PowerShell shell integration.
#
# Same shape as EsctestParse.Tests.ps1: plain asserts, no Pester, run it
# directly. It dot-sources the shipped script rather than a copy, so what is
# asserted here is what a user's shell runs.
#
#     pwsh -NoProfile -File windows/scripts/ShellIntegrationPs1.Tests.ps1
#
# It is worth running under Windows PowerShell 5.1 too, which takes the
# BitConverter path instead of [Convert]::ToHexString.

$ErrorActionPreference = 'Stop'

$script:integration = Join-Path $PSScriptRoot '..\..\src\shell-integration\powershell\ghostty.ps1'
. $script:integration

$script:fails = 0
function Assert-Equal($expected, $actual, $msg) {
    if ($expected -ne $actual) { $script:fails++; Write-Host "FAIL: $msg`n  expected=[$expected] actual=[$actual]" }
    else { Write-Host "ok: $msg" }
}
function Assert-True($cond, $msg) {
    if (-not $cond) { $script:fails++; Write-Host "FAIL: $msg" }
    else { Write-Host "ok: $msg" }
}

# Decode ESC ] 7777 ; p ; <hex> BEL back to the JSON it carries, or $null if
# the sequence is not well formed.
function Get-ReportJson([string] $seq) {
    $m = [regex]::Match($seq, "^\x1b\]7777;p;([0-9A-Fa-f]+)\x07$")
    if (-not $m.Success) { return $null }
    $hex = $m.Groups[1].Value
    $bytes = [byte[]]::new($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16)
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

# --- wire format ---
$seq = Get-GhosttyPromptReport 'C:\Users\me' 7
$json = Get-ReportJson $seq
Assert-True ($null -ne $json) 'a report is a well formed OSC 7777 sequence'
$obj = $json | ConvertFrom-Json
Assert-Equal 1 $obj.v 'schema version is 1'
Assert-Equal 'C:\Users\me' $obj.cwd 'cwd round trips'
Assert-Equal 7 $obj.exit 'exit code round trips'
Assert-True ($obj.shell -in @('pwsh', 'powershell')) 'shell names the shell'

# --- the hex is ASCII, which is what survives every console code page ---
Assert-True ($seq -match '^\x1b\]7777;p;[0-9A-F]+\x07$') 'payload is uppercase ASCII hex'

# --- non-ASCII paths ---
$nonAscii = "C:\Gr" + [char]0x00FC + [char]0x00DF + "e\" + [char]0x65E5 + [char]0x672C
$obj = (Get-ReportJson (Get-GhosttyPromptReport $nonAscii 0)) | ConvertFrom-Json
Assert-Equal $nonAscii $obj.cwd 'a non-ASCII path round trips exactly'

# --- the payload bound the terminal enforces ---
# The parser accepts at most 512 encoded bytes. A path over that must produce
# nothing at all rather than a sequence the terminal will drop, because the
# drop would repeat on every prompt for as long as the user stands there.
$long = 'C:\' + ('a' * 4000)
Assert-Equal '' (Get-GhosttyPromptReport $long 0) 'an oversized payload sends nothing'

# And the boundary itself holds from the other side: grow a path one
# character at a time and the last accepted report must still be within the
# limit, with the first rejected one just past it.
$lastGood = $null
$firstBad = $null
for ($n = 380; $n -le 520; $n++) {
    $r = Get-GhosttyPromptReport ('C:\' + ('a' * $n)) 0
    if ($r) { $lastGood = $r } elseif ($null -eq $firstBad) { $firstBad = $n; break }
}
Assert-True ($null -ne $lastGood) 'a realistic path is under the limit'
Assert-True ($null -ne $firstBad) 'the limit is reached inside the swept range'
Assert-True ((($lastGood.Length - 10) / 2) -le 512) 'the largest accepted payload is within 512 bytes'

if ($script:fails -gt 0) {
    Write-Host "`n$script:fails failure(s)"
    exit 1
}
Write-Host "`nall passed"
