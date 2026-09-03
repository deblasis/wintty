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
# character at a time and find the last path that still produces a report.
$lastGood = $null
$firstBad = $null
for ($n = 380; $n -le 520; $n++) {
    $r = Get-GhosttyPromptReport ('C:\' + ('a' * $n)) 0
    if ($r) { $lastGood = $r } elseif ($null -eq $firstBad) { $firstBad = $n; break }
}
Assert-True ($null -ne $lastGood) 'a realistic path is under the limit'
Assert-True ($null -ne $firstBad) 'the limit is reached inside the swept range'

# The assertion that spans both halves: the largest sequence this emitter
# will ever put on the wire is one the parser will accept. Both bounds below
# are the parser's, not this script's, so the pair fails if either side moves
# without the other. Re-deriving the emitter's own 512-byte budget from its
# own output would assert nothing.
#
#   max_hex_len = 1024        src/terminal/osc/parsers/prompt_report.zig
#   Parser.MAX_BUF = 2048     src/terminal/osc.zig
#
# The sequence is ESC ] 7 7 7 7 ; p ; <hex> BEL, so ten characters are
# framing and the rest is hex; what the parser captures and must hold in its
# inline buffer is "p;<hex>".
$hexLen = $lastGood.Length - 10
Assert-True ($hexLen -le 1024) 'the largest report the emitter sends is within the parser hex limit'
Assert-True (($hexLen + 2) -le 2048) 'the largest report the emitter sends fits the parser inline buffer'

# --- OSC 7 percent-encoding ---
# Decode the path half of a file:// URI back to the characters it names.
function Get-UriPath([string] $uri) {
    $m = [regex]::Match($uri, '^file://[^/]*/(.*)$')
    if (-not $m.Success) { return $null }
    return [System.Uri]::UnescapeDataString($m.Groups[1].Value)
}

$uri = Get-GhosttyFileUri 'C:\Users\me'
Assert-Equal "file://$env:COMPUTERNAME/c:/Users/me" $uri 'an ASCII path is unchanged by encoding'

# Pure ASCII on the wire whatever the path, which is the property the child
# console's code page cannot damage.
$cases = @(
    'C:\Users\me',
    ("C:\Gr" + [char]0x00FC + [char]0x00DF + "e\" + [char]0x65E5 + [char]0x672C),
    'C:\100% done',
    'C:\a b\c#d?e',
    ("C:\bell" + [char]0x0007 + "esc" + [char]0x001b)
)
foreach ($c in $cases) {
    $u = Get-GhosttyFileUri $c
    Assert-True ($u -cmatch '^file://[^/]*/[A-Za-z0-9\-._~/:%]*$') "the URI for [$c] is pure ASCII URI characters"
}

# The characters that would otherwise be misread, each for its own reason.
Assert-Equal "file://$env:COMPUTERNAME/c:/100%25%20done" (Get-GhosttyFileUri 'C:\100% done') `
    'a literal percent is encoded, so the terminal does not decode it away'
Assert-Equal "file://$env:COMPUTERNAME/c:/%1B%07" (Get-GhosttyFileUri ("C:\" + [char]0x001b + [char]0x0007)) `
    'ESC and BEL cannot terminate the sequence reporting them'

# And the whole point: percent-decoding the emitted URI gives back the path.
foreach ($c in $cases) {
    $decoded = Get-UriPath (Get-GhosttyFileUri $c)
    $expected = ($c -replace '\\', '/')
    $expected = $expected.Substring(0, 1).ToLowerInvariant() + $expected.Substring(1)
    Assert-Equal $expected $decoded "[$c] survives the round trip through the URI"
}

if ($script:fails -gt 0) {
    Write-Host "`n$script:fails failure(s)"
    exit 1
}
Write-Host "`nall passed"
