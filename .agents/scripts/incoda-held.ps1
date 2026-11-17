# The one PowerShell reader of INCODA_HELD in this repo. incoda 0.7 exports
# KEY=TICKET entries; 0.6 exported a bare key list. The lane guard (see the
# justfile's _require-lane) reads it with -Strict, which counts ONLY
# KEY=TICKET entries: a bare key is treated as not held. Parse rules mirror
# incoda 0.7.2's own held.Parse (deblasis/incoda 70d017a): split on `,` only,
# trim Go's TrimSpace set, key is 1-64 of [A-Za-z0-9._-], not `.`/`..` or a
# Windows device name, ticket is <nano>-<pid>.ticket with int64 digits.
# Keys compare case-sensitively, as incoda compares them.

function Get-IncodaHeldKeys {
    param(
        [AllowNull()][AllowEmptyString()][string]$Value = $env:INCODA_HELD,
        # Count only 0.7 KEY=TICKET entries: a bare key (0.6 shape) does not
        # prove a live lane holder, so the lane guard treats it as not held.
        [switch]$Strict
    )
    $trimPattern = '^[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$'
    $entryPattern = '^([A-Za-z0-9._-]{1,64})(?:=([0-9]+)-([0-9]+)\.ticket)?$'
    $deviceNames = @('con', 'prn', 'aux', 'nul', 'com1', 'com2', 'com3', 'com4', 'com5', 'com6', 'com7', 'com8', 'com9', 'lpt1', 'lpt2', 'lpt3', 'lpt4', 'lpt5', 'lpt6', 'lpt7', 'lpt8', 'lpt9')
    $int64Max = [System.Numerics.BigInteger]::Parse('9223372036854775807')
    foreach ($entry in (($Value ?? '') -split ',')) {
        $match = [regex]::Match([regex]::Replace($entry, $trimPattern, ''), $entryPattern)
        if (-not $match.Success) { continue }
        if ($Strict -and -not $match.Groups[2].Success) { continue }
        $key = $match.Groups[1].Value
        if ($key -ceq '.' -or $key -ceq '..' -or $deviceNames -ccontains $key.ToLowerInvariant()) { continue }
        if ($match.Groups[2].Success -and (
                [System.Numerics.BigInteger]::Parse($match.Groups[2].Value) -gt $int64Max -or
                [System.Numerics.BigInteger]::Parse($match.Groups[3].Value) -gt $int64Max)) { continue }
        $key
    }
}

# -SelfTest: the two shapes the lane guard depends on. Run by gates-selftest.
if ($MyInvocation.InvocationName -eq '.' -or -not $MyInvocation.MyCommand) { return }
if ($args -contains '-SelfTest') {
    $ErrorActionPreference = 'Stop'
    $fail = @()
    if (@(Get-IncodaHeldKeys -Value 'wintty-build') -cnotcontains 'wintty-build') { $fail += 'lenient: bare key not accepted' }
    if (@(Get-IncodaHeldKeys -Value 'wintty-build' -Strict).Count -ne 0) { $fail += 'strict: bare key accepted' }
    if (@(Get-IncodaHeldKeys -Value 'wintty-build=1-2.ticket' -Strict) -cnotcontains 'wintty-build') { $fail += 'strict: KEY=TICKET not accepted' }
    if (@(Get-IncodaHeldKeys -Value 'wintty-build=1-x.ticket' -Strict).Count -ne 0) { $fail += 'strict: malformed ticket accepted' }
    if ($fail) { $fail | ForEach-Object { Write-Host "incoda-held selftest FAIL: $_" -ForegroundColor Red }; exit 1 }
    Write-Host 'incoda-held selftest ok (lenient bare, strict ticket-only)'
    exit 0
}
