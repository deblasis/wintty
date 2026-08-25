<#
.SYNOPSIS
    Round-trips a payload through the system clipboard using the Kitty
    clipboard protocol (OSC 5522), and checks it came back byte for byte.

.DESCRIPTION
    RUN THIS INSIDE WINTTY. It drives the terminal with escape sequences and
    reads the terminal's replies off its own stdin, so it needs no GUI
    automation, no UIA, and no synthesized input -- the three things that
    make every other harness here fragile. What it exercises is the whole
    path that unit tests cannot reach:

        script -> OSC 5522 write -> libghostty -> write_clipboard_cb
              -> Windows clipboard
              -> read_clipboard_cb -> OSC 5522 read -> script

    The oracle is a SHA-256 comparison of the bytes written against the
    bytes read back. Not "did it crash", not "did a dialog appear": the
    payload either survived the round trip intact or it did not. That is
    what makes an image worth using -- binary data with embedded NULs is
    precisely what a strlen-based marshaller silently truncates, and a
    truncated PNG still looks like a PNG until you hash it.

    Two modes:

      -Unattended   No prompts. Requires clipboard-read = allow and
                    clipboard-write = allow in the config, and then the
                    whole thing loops without a human, which is what makes
                    -Iterations meaningful.

      (default)     Expects the permission prompt on the read and waits for
                    a human to click Allow. Use this to check the prompt
                    itself, including that it previews an image AS an image.

.PARAMETER ImagePath
    A file to round-trip. Defaults to a generated PNG so the script is
    self-contained; pass a real one to check a specific case.

.PARAMETER Iterations
    Round trips to run. Above 1 implies random generated payloads, so this
    is only useful with -Unattended.

.PARAMETER Seed
    Seed for the generated payloads, so a failure replays.

.NOTES
    Exit codes follow the fuzz-suite contract:
      0  pass
      2  product findings (a payload did not survive)
      1  the harness could not run, so nothing is known about the product
#>
[CmdletBinding()]
param(
    [string]$ImagePath = '',
    [ValidateRange(1, 100000)]
    [int]$Iterations = 1,
    [int]$Seed = 12345,
    [switch]$Unattended,

    # Write a range of payload sizes and report the cost of each, instead of
    # round-tripping one. This exists to answer a question a single timing
    # cannot: whether a slow write is a per-write cost (the clipboard call,
    # the encoder, one dialog's worth of bookkeeping) or a per-byte one (the
    # OSC parser, the transport). Those have opposite fixes, and the shape of
    # cost against size is what tells them apart.
    #
    # Writes only, so it does not wait on a read prompt. Set clipboard-write
    # to allow first, or every size stops for a dialog and measures the human.
    [switch]$Sweep,

    # Exercise the reply parser against synthetic replies. Needs no terminal,
    # no clipboard and no human, so it can run anywhere.
    [switch]$SelfTest,
    [ValidateRange(1000, 600000)]
    [int]$TimeoutMs = 20000,

    # How long to wait for the FIRST reply packet. This spans the permission
    # prompt, so it is a human-scale number, not a protocol-scale one.
    [ValidateRange(1000, 600000)]
    [int]$PromptWaitMs = 120000,
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$ESC = [char]27
$ST  = "$ESC\"

function Write-Osc([string]$payload) {
    [Console]::Out.Write("$ESC]$payload$ST")
    [Console]::Out.Flush()
}

function ConvertTo-B64([byte[]]$b) { [Convert]::ToBase64String($b) }
function ConvertTo-B64Text([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }

# Drains everything the terminal writes back into ONE buffer, then parses.
#
# The previous design returned one packet per call and parsed each in
# isolation. That is wrong here for a reason worth recording: reading the
# console one character at a time is slow (about 500 chars/sec), a 31KB
# image is roughly 43KB of base64, and so a per-packet timeout expires in
# the MIDDLE of a packet. The reply then arrives as fragments, only the
# fragments that happen to start at a `mime=` boundary parse, and the result
# looks exactly like the clipboard corrupting the payload. It is not. So:
# accumulate first, parse once, and let packet boundaries fall where they
# actually are.
function Read-Until-Terminal([int]$firstWaitMs, [int]$idleWaitMs) {
    $sb = [Text.StringBuilder]::new()
    $deadline = (Get-Date).AddMilliseconds($firstWaitMs)

    # Time to FIRST byte is the permission prompt plus whatever the terminal
    # takes to start answering; everything after it is throughput. Reported
    # separately because a single total is a number nobody can act on: it
    # cannot tell a slow protocol from a slow human.
    $script:LastTimeToFirstByteMs = -1
    $script:LastTransferMs = -1
    $started = Get-Date
    $firstByteAt = $null

    while ((Get-Date) -lt $deadline) {
        if ([Console]::KeyAvailable) {
            if ($null -eq $firstByteAt) {
                $firstByteAt = Get-Date
                $script:LastTimeToFirstByteMs = [int]($firstByteAt - $started).TotalMilliseconds
            }
            # Tight drain: no sleep while there is anything to take.
            while ([Console]::KeyAvailable) {
                [void]$sb.Append([Console]::ReadKey($true).KeyChar)
            }

            # Every byte received extends the patience, so a slow transfer is
            # never mistaken for a finished one.
            $deadline = (Get-Date).AddMilliseconds($idleWaitMs)

            # A terminal status ends the transfer. Checked on the whole
            # buffer rather than per packet, so it does not matter where the
            # read happened to break.
            $text = $sb.ToString()
            if ($text -match 'status=(DONE|EPERM|ENOSYS|EIO|EBUSY|EINVAL)') {
                $script:LastTransferMs = [int]((Get-Date) - $firstByteAt).TotalMilliseconds
                break
            }
        }
        else {
            Start-Sleep -Milliseconds 5
        }
    }

    return $sb.ToString()
}

function Clear-Input {
    while ([Console]::KeyAvailable) { [void][Console]::ReadKey($true) }
}

function Read-Reply([int]$timeoutMs) {
    return Read-Until-Terminal $timeoutMs 1500
}

# PURE: raw reply text in, decoded payload out. No console, no clipboard.
#
# Split out deliberately. Every failure this harness has reported so far was
# a bug in HERE, not in the terminal, and each one cost a full attended run
# to find because it could only be exercised through the GUI. It is string
# processing; it can be tested on its own, and -SelfTest does.
function Convert-ReadReply([string]$raw, [string]$mime) {
    $bytes = [System.Collections.Generic.List[byte]]::new()
    $status = ''
    $chunks = 0

    $packets = $raw -split ([regex]::Escape("$ESC\")) | Where-Object { $_ -match '\S' }

    foreach ($packet in $packets) {
        # Last status wins: the reply ends with DONE, and an earlier OK ack
        # must not be what gets reported.
        if ($packet -match 'status=([A-Za-z]+)') { $status = $Matches[1] }

        # A DATA chunk is ...:mime=<b64>;<b64 payload>. The targets listing is
        # also a DATA packet, under the reserved mime ".", so each chunk is
        # matched against the mime we asked for rather than concatenated
        # blindly -- otherwise the listing is spliced into the payload.
        if ($packet -match 'mime=([A-Za-z0-9+/=]*);([A-Za-z0-9+/=]+)') {
            $chunkMime = ''
            try { $chunkMime = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1])) } catch { }
            if ($chunkMime -eq $mime) {
                try { $bytes.AddRange([Convert]::FromBase64String($Matches[2])); $chunks++ } catch { }
            }
        }
    }

    return [pscustomobject]@{
        Bytes = $bytes.ToArray()
        Packets = $packets.Count
        Chunks = $chunks
        Status = $status
    }
}

function Write-ClipboardPayload([byte[]]$bytes, [string]$mime, [string]$id) {
    Clear-Input
    Write-Osc "5522;type=write:id=$id"

    # Chunked, because a single OSC carrying a whole image is exactly the
    # shape that finds buffer limits. 24000 raw bytes -> 32000 base64 chars
    # per packet; smaller chunks mostly measure per-packet overhead, which
    # is the harness, not the clipboard.
    $mimeB64 = ConvertTo-B64Text $mime
    $chunk = 24000
    $sent = 0
    for ($off = 0; $off -lt $bytes.Length; $off += $chunk) {
        $len = [Math]::Min($chunk, $bytes.Length - $off)
        $slice = New-Object byte[] $len
        [Array]::Copy($bytes, $off, $slice, 0, $len)
        Write-Osc "5522;type=wdata:mime=$mimeB64;$(ConvertTo-B64 $slice)"
        $sent++
    }

    # An empty wdata commits the transaction. Only the commit invokes the
    # write callback, so this is the line that actually touches the clipboard.
    Write-Osc '5522;type=wdata'

    $reply = Read-Until-Terminal $TimeoutMs 1500
    return [pscustomobject]@{ Chunks = $sent; Reply = ($reply -replace [regex]::Escape($ESC), '') }
}

function Read-ClipboardPayload([string]$mime) {
    Clear-Input
    Write-Osc "5522;type=read;$(ConvertTo-B64Text $mime)"

    $raw = Read-Until-Terminal $PromptWaitMs 2500
    $parsed = Convert-ReadReply $raw $mime

    return [pscustomobject]@{
        Bytes = $parsed.Bytes
        Packets = $parsed.Packets
        Chunks = $parsed.Chunks
        Status = $parsed.Status
        TimeToFirstByteMs = $script:LastTimeToFirstByteMs
        TransferMs = $script:LastTransferMs
        Raw = ($raw -replace [regex]::Escape($ESC), '<ESC>')
    }
}

function Invoke-SelfTest {
    $fails = 0
    function Check([string]$name, [bool]$ok, [string]$detail) {
        if ($ok) { Write-Host "  ok   $name" }
        else { Write-Host "  FAIL $name : $detail"; $script:selfTestFails++ }
    }
    $script:selfTestFails = 0

    $st = "$ESC\"
    $pngMime = ConvertTo-B64Text 'image/png'
    $textMime = ConvertTo-B64Text 'text/plain'
    $listMime = ConvertTo-B64Text '.'

    # 1. The exact shape from clipboard_response.zig's own test.
    $r = Convert-ReadReply ("$ESC]5522;type=read:status=OK$st" +
        "$ESC]5522;type=read:status=DATA:mime=$textMime;$(ConvertTo-B64 ([Text.Encoding]::UTF8.GetBytes('text/plain'))) $st".Replace(' ', '') +
        "$ESC]5522;type=read:status=DONE$st") 'text/plain'
    Check 'upstream three-packet shape' ($r.Status -eq 'DONE' -and $r.Chunks -eq 1) "status=$($r.Status) chunks=$($r.Chunks)"

    # 2. OK is an ack, not the end. Regression: the parser once stopped here.
    $r = Convert-ReadReply ("$ESC]5522;type=read:status=OK$st" +
        "$ESC]5522;type=read:status=DATA:mime=$pngMime;$(ConvertTo-B64 (,[byte]1 * 10))$st" +
        "$ESC]5522;type=read:status=DONE$st") 'image/png'
    Check 'OK ack does not end the transfer' ($r.Bytes.Length -eq 10) "got $($r.Bytes.Length) bytes"

    # 3. Many chunks concatenate in order.
    $payload = [byte[]](1..250)
    $mid = 100
    $a = ConvertTo-B64 $payload[0..($mid - 1)]
    $b = ConvertTo-B64 $payload[$mid..249]
    $r = Convert-ReadReply ("$ESC]5522;type=read:status=OK$st" +
        "$ESC]5522;type=read:status=DATA:mime=$pngMime;$a$st" +
        "$ESC]5522;type=read:status=DATA:mime=$pngMime;$b$st" +
        "$ESC]5522;type=read:status=DONE$st") 'image/png'
    Check 'chunks concatenate in order' (($r.Bytes.Length -eq 250) -and ($r.Bytes[0] -eq 1) -and ($r.Bytes[249] -eq 250)) "got $($r.Bytes.Length) bytes"

    # 4. The targets listing is a DATA packet too, and must NOT be spliced in.
    $r = Convert-ReadReply ("$ESC]5522;type=read:status=OK$st" +
        "$ESC]5522;type=read:status=DATA:mime=$listMime;$(ConvertTo-B64 ([Text.Encoding]::UTF8.GetBytes('image/png')))$st" +
        "$ESC]5522;type=read:status=DATA:mime=$pngMime;$(ConvertTo-B64 (,[byte]7 * 20))$st" +
        "$ESC]5522;type=read:status=DONE$st") 'image/png'
    Check 'targets listing excluded from payload' (($r.Bytes.Length -eq 20) -and ($r.Chunks -eq 1)) "got $($r.Bytes.Length) bytes in $($r.Chunks) chunk(s)"

    # 5. A representation we did not ask for is ignored.
    $r = Convert-ReadReply ("$ESC]5522;type=read:status=OK$st" +
        "$ESC]5522;type=read:status=DATA:mime=$textMime;$(ConvertTo-B64 (,[byte]9 * 5))$st" +
        "$ESC]5522;type=read:status=DONE$st") 'image/png'
    Check 'other mime ignored' ($r.Bytes.Length -eq 0) "got $($r.Bytes.Length) bytes"

    # 6. Errors surface as the final status.
    foreach ($err in @('EPERM', 'ENOSYS', 'EIO')) {
        $r = Convert-ReadReply "$ESC]5522;type=read:status=$err$st" 'image/png'
        Check "error status $err" ($r.Status -eq $err) "got $($r.Status)"
    }

    # 7. DONE wins over the earlier OK.
    $r = Convert-ReadReply ("$ESC]5522;type=read:status=OK$st" + "$ESC]5522;type=read:status=DONE$st") 'image/png'
    Check 'final status is DONE not OK' ($r.Status -eq 'DONE') "got $($r.Status)"

    # 8. A big multi-chunk image round-trips byte for byte. This is the case
    #    the live run kept failing, at a size that actually chunks.
    $rng = [Random]::new(7)
    $big = New-Object byte[] 31772
    $rng.NextBytes($big)
    $sb = [Text.StringBuilder]::new()
    [void]$sb.Append("$ESC]5522;type=read:status=OK$st")
    for ($off = 0; $off -lt $big.Length; $off += 4096) {
        $len = [Math]::Min(4096, $big.Length - $off)
        $slice = New-Object byte[] $len
        [Array]::Copy($big, $off, $slice, 0, $len)
        [void]$sb.Append("$ESC]5522;type=read:status=DATA:mime=$pngMime;$(ConvertTo-B64 $slice)$st")
    }
    [void]$sb.Append("$ESC]5522;type=read:status=DONE$st")
    $r = Convert-ReadReply $sb.ToString() 'image/png'
    $same = ($r.Bytes.Length -eq $big.Length)
    if ($same) { for ($i = 0; $i -lt $big.Length; $i++) { if ($r.Bytes[$i] -ne $big[$i]) { $same = $false; break } } }
    Check '31772-byte image over 8 chunks is byte-identical' $same "got $($r.Bytes.Length) of $($big.Length) bytes, $($r.Chunks) chunks"

    # Every function the live run calls must exist. This check is here
    # because an edit to this script once deleted Write-ClipboardPayload
    # outright: PowerShell resolves calls at runtime, so nothing failed
    # until an attended run reached the write path and died on a typo-shaped
    # error. A parser test cannot catch that; a roll call can.
    foreach ($fn in @(
        'Write-Osc', 'ConvertTo-B64', 'ConvertTo-B64Text',
        'Read-Until-Terminal', 'Clear-Input', 'Convert-ReadReply',
        'Write-ClipboardPayload', 'Read-ClipboardPayload',
        'Invoke-Sweep', 'New-FillerBytes',
        'Get-Sha', 'New-TestPng')) {
        Check "function $fn is defined" ($null -ne (Get-Command $fn -ErrorAction SilentlyContinue)) 'missing'
    }

    Write-Host ''
    if ($script:selfTestFails -gt 0) {
        Write-Host "self-test: $($script:selfTestFails) failure(s)"
        exit 2
    }
    Write-Host 'self-test: pass'
    exit 0
}

function Get-Sha([byte[]]$b) {
    if ($b.Length -eq 0) { return '<empty>' }
    $sha = [Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash($b)) -replace '-', '').ToLowerInvariant()
}

# A small valid PNG, generated so the script stands alone.
#
# LockBits and one Marshal.Copy, NOT SetPixel. SetPixel is one interop hop
# per pixel, which for a 96x96 image is 9216 of them from PowerShell and
# takes long enough to be mistaken for the clipboard being slow. The
# harness must not be the thing under suspicion.
function New-TestPng([int]$w, [int]$h, [int]$seed) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
    try {
        $bytes = New-Object byte[] ($data.Stride * $h)
        $rng = [Random]::new($seed)
        $rng.NextBytes($bytes)
        # Force alpha opaque so the PNG encoder does not premultiply noise
        # into something whose byte length varies between runs.
        for ($i = 3; $i -lt $bytes.Length; $i += 4) { $bytes[$i] = 255 }
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
    }
    finally { $bmp.UnlockBits($data) }

    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

# ---- run ------------------------------------------------------------------

function New-FillerBytes([int]$n) {
    # StringBuilder, not a pipeline. Generating 256KB one character at a time
    # through ForEach-Object takes longer than the write being measured, and
    # while it sits outside the timed region it still makes the sweep tedious
    # enough that nobody runs it.
    $sb = [Text.StringBuilder]::new($n)
    for ($i = 0; $i -lt $n; $i++) { [void]$sb.Append([char](97 + ($i % 26))) }
    return [Text.Encoding]::UTF8.GetBytes($sb.ToString())
}

function Invoke-Sweep {
    # text/plain, not PNG: the size is then exactly what was asked for, and
    # no image encoder sits between the harness and the path being measured.
    $sizes = @(4KB, 16KB, 64KB, 256KB)
    $repeats = 5
    $chunkBytes = 24000
    $rows = @()

    Write-Host ''
    Write-Host 'Write-cost sweep. Reading nothing, so no read prompt.'
    Write-Host 'Needs clipboard-write = allow, or each size waits on a dialog.'
    Write-Host ''

    # Discarded, never reported. The first write of the session pays for JIT
    # and for whatever the clipboard initialises once, and folding that into
    # the smallest sample is how the first version of this sweep produced a
    # negative slope and then printed a confident sentence about it.
    Write-Host '  warming up (first write is discarded)...'
    $null = Write-ClipboardPayload (New-FillerBytes 4KB) 'text/plain' 'sweep-warmup'

    Write-Host ''
    Write-Host "     size   chunks    median     best    KB/s   (n=$repeats)"

    foreach ($n in $sizes) {
        $payload = New-FillerBytes $n
        $chunks = [Math]::Ceiling($payload.Length / $chunkBytes)
        $samples = @()

        # Repeats and a median, because one sample of a clipboard write on a
        # desktop is competing with whatever else is running. A single number
        # per size cannot be distinguished from noise, and this sweep exists
        # to settle an argument rather than to add to it.
        for ($r = 0; $r -lt $repeats; $r++) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $null = Write-ClipboardPayload $payload 'text/plain' "sweep-$n-$r"
            $sw.Stop()
            $samples += $sw.Elapsed.TotalMilliseconds
        }

        $sorted = $samples | Sort-Object
        $median = $sorted[[int]($repeats / 2)]
        $best = $sorted[0]
        $kbs = [Math]::Round(($payload.Length / 1KB) / ($median / 1000.0), 1)

        Write-Host ('  {0,7}   {1,6}   {2,6:N1}ms  {3,6:N1}ms  {4,6}' -f
            "$([int]($n/1KB))KB", $chunks, $median, $best, $kbs)
        $rows += [pscustomobject]@{ Bytes = $payload.Length; Ms = $median; Best = $best; Chunks = $chunks }
    }

    # Least squares over every sample, not the two endpoints. An endpoint fit
    # gives one outlier total control of the answer, which is exactly what
    # went wrong before.
    function Get-Fit($points, [string]$field) {
        $nPts = $points.Count
        $sumX = 0.0; $sumY = 0.0; $sumXY = 0.0; $sumXX = 0.0
        foreach ($row in $points) {
            $x = [double]$row.Bytes
            $y = [double]$row.$field
            $sumX += $x; $sumY += $y; $sumXY += $x * $y; $sumXX += $x * $x
        }
        $denom = ($nPts * $sumXX) - ($sumX * $sumX)
        $slope = if ($denom -ne 0) { (($nPts * $sumXY) - ($sumX * $sumY)) / $denom } else { 0 }
        $intercept = ($sumY - ($slope * $sumX)) / $nPts

        $meanY = $sumY / $nPts
        $ssTot = 0.0; $ssRes = 0.0
        foreach ($row in $points) {
            $pred = $intercept + ($slope * $row.Bytes)
            $ssRes += [Math]::Pow($row.$field - $pred, 2)
            $ssTot += [Math]::Pow($row.$field - $meanY, 2)
        }
        return [pscustomobject]@{
            Fixed = $intercept
            PerKB = $slope * 1KB
            R2 = $(if ($ssTot -gt 0) { 1 - ($ssRes / $ssTot) } else { 0 })
        }
    }

    $medFit = Get-Fit $rows 'Ms'
    $bestFit = Get-Fit $rows 'Best'

    # Both fits, because on a loaded desktop they answer different questions.
    # The median says what a write costs here and now, with everything else
    # running. The minimum is the better estimate of what the code actually
    # costs: noise only ever adds time, so the fastest observed run is the one
    # least contaminated by whatever else wanted the CPU.
    Write-Host ''
    Write-Host ('  fit on medians : {0,5:N0}ms fixed + {1,5:N2}ms/KB  ({2,6:N0} KB/s)  R2={3:N3}' -f
        $medFit.Fixed, $medFit.PerKB, $(if ($medFit.PerKB -gt 0) { 1000.0 / $medFit.PerKB } else { 0 }), $medFit.R2)
    Write-Host ('  fit on best    : {0,5:N0}ms fixed + {1,5:N2}ms/KB  ({2,6:N0} KB/s)  R2={3:N3}' -f
        $bestFit.Fixed, $bestFit.PerKB, $(if ($bestFit.PerKB -gt 0) { 1000.0 / $bestFit.PerKB } else { 0 }), $bestFit.R2)

    # Spread is the machine's contribution, stated rather than left in two
    # columns for a human to compare. A quiet machine puts these within a few
    # percent of each other; a busy one does not, and then the median figure
    # says more about the load than about the clipboard.
    $worstSpread = 1.0
    foreach ($row in $rows) {
        if ($row.Best -gt 0) {
            $ratio = $row.Ms / $row.Best
            if ($ratio -gt $worstSpread) { $worstSpread = $ratio }
        }
    }
    Write-Host ''
    Write-Host ('  worst median/best spread: {0:N1}x' -f $worstSpread)

    if ($worstSpread -gt 2.0) {
        Write-Host '  The same payload varied by more than 2x across runs, so'
        Write-Host '  this machine was busy. Trust the best-fit line; the median'
        Write-Host '  one is measuring the load. Re-run on an idle machine to'
        Write-Host '  make the two converge.'
    }

    Write-Host ''
    if ($bestFit.PerKB -le 0 -or $bestFit.R2 -lt 0.9) {
        # Refusing to answer is a result. A model this poor cannot separate
        # fixed cost from marginal cost, and reporting either number would be
        # inventing precision the data does not have.
        Write-Host '  INCONCLUSIVE: cost is not linear in payload size, so the'
        Write-Host '  figures above do not mean what they say. Read the per-size'
        Write-Host '  KB/s column instead: if it climbs with size, a'
        Write-Host '  per-transaction cost is being amortised.'
    }
    elseif ($bestFit.Fixed -gt 100) {
        Write-Host '  Cost is dominated by the FIXED part, so this is a per-write'
        Write-Host '  expense (the clipboard call itself, or work done once per'
        Write-Host '  transaction). Faster parsing would not move it.'
    }
    else {
        Write-Host ('  Cost scales with SIZE at {0:N0} KB/s uncontended.' -f (1000.0 / $bestFit.PerKB))
        Write-Host '  Upstream quoted 1MiB in 446ms (about 2300 KB/s) BEFORE'
        Write-Host '  their SIMD work, so compare against that before calling'
        Write-Host '  anything here a regression.'
    }
    Write-Host ''
    exit 0
}


# Dispatched below every function definition, not beside the parameter
# block: the self-test's roll call asserts each function the live run
# calls actually exists, and a roll call that runs before the
# definitions would report them all missing.
if ($SelfTest) { Invoke-SelfTest }
if ($Sweep) { Invoke-Sweep }

$findings = @()
$rng = [Random]::new($Seed)

Write-Host ''
Write-Host 'kitty-clipboard-roundtrip: OSC 5522 write -> Windows clipboard -> OSC 5522 read'
if (-not $Unattended) {
    Write-Host '  (attended: the read raises a permission prompt -- click Allow, and check it previews the image)'
}
Write-Host ''

for ($iter = 1; $iter -le $Iterations; $iter++) {
    $swGen = [Diagnostics.Stopwatch]::StartNew()
    if ($ImagePath -and $iter -eq 1) {
        if (-not (Test-Path $ImagePath)) { Write-Host "HARNESS: no file at $ImagePath"; exit 1 }
        $payload = [IO.File]::ReadAllBytes($ImagePath)
        $mime = 'image/png'
        $label = "$ImagePath"
    } elseif ($Iterations -eq 1) {
        $payload = New-TestPng 96 96 $Seed
        $mime = 'image/png'
        $label = 'generated 96x96 PNG'
    } else {
        # Fuzz mode: vary size and type. Text and image take different paths
        # through the backend (one is a string, one is encoded bytes), so
        # exercising only one of them would leave half the code untested.
        if ($rng.Next(2) -eq 0) {
            $side = 16 + $rng.Next(112)
            $payload = New-TestPng $side $side $rng.Next()
            $mime = 'image/png'
            $label = "generated ${side}x${side} PNG"
        } else {
            $n = $rng.Next(1, 20000)
            $payload = New-Object byte[] $n
            $rng.NextBytes($payload)
            # text/plain must be valid UTF-8 to survive a string round trip.
            $payload = [Text.Encoding]::UTF8.GetBytes(
                -join ((1..$n) | ForEach-Object { [char](32 + $rng.Next(94)) }))
            $mime = 'text/plain'
            $label = "$($payload.Length)B text"
        }
    }

    $genMs = $swGen.ElapsedMilliseconds
    $wroteSha = Get-Sha $payload
    $swGen.Stop()
    Write-Host "[$iter/$Iterations] $label ($mime, $($payload.Length) bytes, generated in ${genMs}ms)"

    $swWrite = [Diagnostics.Stopwatch]::StartNew()
    $w = Write-ClipboardPayload $payload $mime "rt$iter"
    $swWrite.Stop()
    $done = $w.Reply -match 'status=DONE'
    Write-Host "    write: $($w.Chunks) chunk(s) in $($swWrite.ElapsedMilliseconds)ms, terminal replied: $($w.Reply)"
    if (-not $done) {
        $findings += "iteration ${iter}: write did not report DONE (reply: $($w.Reply -replace [regex]::Escape($ESC), ''))"
        continue
    }

    $swRead = [Diagnostics.Stopwatch]::StartNew()
    $r = Read-ClipboardPayload $mime
    $swRead.Stop()
    $readSha = Get-Sha $r.Bytes
    Write-Host "    read:  $($r.Packets) packet(s), $($r.Chunks) data chunk(s), $($r.Bytes.Length) bytes, status=$($r.Status)"
    Write-Host "           prompt+first byte: $($r.TimeToFirstByteMs)ms   transfer: $($r.TransferMs)ms   total: $($swRead.ElapsedMilliseconds)ms"
    if ($r.TransferMs -gt 0 -and $r.Bytes.Length -gt 0) {
        $kbs = [math]::Round(($r.Bytes.Length / 1KB) / ($r.TransferMs / 1000.0), 1)
        Write-Host "           throughput: $kbs KB/s over the transfer alone (excludes the prompt)"
    }
    if ($r.Status -ne 'DONE' -and $r.Bytes.Length -gt 0) {
        Write-Host "           WARNING: transfer ended on status=$($r.Status), not DONE, so it stopped early"
    }
    Write-Host "    written:  $wroteSha"
    Write-Host "    read back: $readSha"

    if ($wroteSha -eq $readSha) {
        Write-Host '    OK: byte-for-byte identical'
        if ($OutDir -and $iter -eq 1) {
            $p = Join-Path $OutDir 'readback.bin'
            [IO.File]::WriteAllBytes($p, $r.Bytes)
            Write-Host "    wrote the read-back copy to $p"
        }
    } else {
        # Truncation and corruption are different defects and want different
        # fixes, so the harness says which it is rather than leaving it to be
        # guessed from the byte counts.
        $prefix = $r.Bytes.Length -lt $payload.Length
        if ($prefix) {
            for ($b = 0; $b -lt $r.Bytes.Length; $b++) {
                if ($r.Bytes[$b] -ne $payload[$b]) { $prefix = $false; break }
            }
        }

        $shape = if ($r.Bytes.Length -eq 0) {
            'nothing came back'
        } elseif ($prefix) {
            "TRUNCATED: the $($r.Bytes.Length) bytes received are a correct prefix, $($payload.Length - $r.Bytes.Length) missing, ended on status=$($r.Status)"
        } else {
            'CORRUPTED: the bytes received differ from what was written'
        }

        $findings += "iteration ${iter}: payload did not survive ($mime, wrote $($payload.Length)B / read $($r.Bytes.Length)B; $shape; seed $Seed)"
        Write-Host '    MISMATCH'
        Write-Host "    $shape"

        # The raw dump is enormous for an image. Only worth it when the
        # payload came back WRONG rather than SHORT, because a short read is
        # already fully explained by the chunk count and the final status.
        if (-not $prefix -and $r.Bytes.Length -gt 0) {
            Write-Host "    raw: $($r.Raw)"
        }
    }
    Write-Host ''
}

if ($findings.Count -gt 0) {
    Write-Host "=== $($findings.Count) finding(s) ==="
    $findings | ForEach-Object { Write-Host "  $_" }
    exit 2
}

Write-Host 'kitty-clipboard-roundtrip: pass'
exit 0
