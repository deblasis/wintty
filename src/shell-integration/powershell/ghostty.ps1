# Ghostty PowerShell shell integration.
#
# Wintty (the Windows build of Ghostty) exports the path to this script in
# the GHOSTTY_SHELL_INTEGRATION_PS1 environment variable. Users opt in by
# adding the following line to their $PROFILE:
#
#     if ($env:GHOSTTY_SHELL_INTEGRATION_PS1) { . $env:GHOSTTY_SHELL_INTEGRATION_PS1 }
#
# The script is compatible with Windows PowerShell 5.1 and PowerShell 7+.
# It uses [char]0x1b and [char]0x07 instead of `e / `a so it works under the
# 5.1 parser, and avoids $PSStyle which is 7+ only.
#
# Sourcing this script more than once is a no-op (see guard below).

if ($global:__GhosttyShellIntegrationLoaded) { return }
$global:__GhosttyShellIntegrationLoaded = $true

# ESC and BEL constants. The OSC sequences we emit look like:
#     ESC ] <ps> ; <pt> BEL
# They live in the global scope so the PSReadLine Enter handler can read
# them at command-execution time regardless of what scope this script is
# dot-sourced into.
$global:__GhosttyEsc = [char]0x1b
$global:__GhosttyBel = [char]0x07

# Which shell is reporting. PowerShell 7+ identifies as Core; anything else
# here is Windows PowerShell 5.1.
$global:__GhosttyShellName = if ($PSVersionTable.PSEdition -eq 'Core') {
    'pwsh'
} else {
    'powershell'
}

# [Convert]::ToHexString arrived with .NET 5, so it exists under PowerShell 7
# and not under Windows PowerShell 5.1. Resolved once here rather than per
# prompt, because the fallback allocates a dashed string and then rewrites it.
$global:__GhosttyHasToHexString = $null -ne [Convert].GetMethod(
    'ToHexString', [type[]]@([byte[]]))

# Percent-encode the path half of a file:// URI, the way upstream Ghostty's
# fish integration does with `string escape --style=url $PWD`.
#
# This is not cosmetic. It is what makes OSC 7 carry a Windows path
# correctly at all:
#
#   * The output is pure ASCII, so the child console's code page has nothing
#     to transcode or substitute. A path with a non-ASCII character reaches
#     the terminal intact from a default Windows install, where writing the
#     raw bytes does not.
#
#   * ESC, BEL and ST become %1B, %07 and %9C, so no character of a
#     directory name can terminate the sequence reporting it.
#
#   * `%` is a legal Windows path character. Sending it raw is a live bug:
#     the terminal parses OSC 7 with percent-decoding on, so `C:\100%` used
#     to arrive as whatever `%` plus the next two characters happened to
#     decode to. Encoding it as %25 is what makes it survive.
#
# The unreserved set is RFC 3986's, plus the two characters this URI's path
# needs to keep its shape: `/` as the separator (fish keeps it too) and `:`
# after the drive letter. Written out by hand rather than reaching for
# [System.Uri]::EscapeDataString or [System.Web.HttpUtility], because those
# differ between Windows PowerShell 5.1 and PowerShell 7 over which
# characters are reserved, and this has to produce the same bytes on both.
function Get-GhosttyUriPath([string] $path) {
    $sb = [System.Text.StringBuilder]::new()
    foreach ($b in [System.Text.Encoding]::UTF8.GetBytes($path)) {
        if (($b -ge 0x41 -and $b -le 0x5A) -or `
            ($b -ge 0x61 -and $b -le 0x7A) -or `
            ($b -ge 0x30 -and $b -le 0x39) -or `
            $b -eq 0x2D -or $b -eq 0x2E -or $b -eq 0x5F -or $b -eq 0x7E -or `
            $b -eq 0x2F -or $b -eq 0x3A) {
            [void]$sb.Append([char]$b)
        } else {
            [void]$sb.AppendFormat('%{0:X2}', $b)
        }
    }
    return $sb.ToString()
}

# Convert a Windows path (e.g. C:\Users\me) to an OSC 7 file:// URI of the
# form file://HOST/c:/Users/me. We lowercase the drive letter to match the
# convention used by upstream Ghostty's other shells and convert backslashes
# to forward slashes. UNC paths (\\server\share) pass through as
# file://HOST//server/share which is intentionally lossy; consumers that
# care about the original host can read OSC 9;9 instead.
function Get-GhosttyFileUri([string] $path) {
    $normalized = $path -replace '\\', '/'
    if ($normalized -match '^([A-Za-z]):') {
        $drive = $matches[1].ToLowerInvariant()
        $normalized = "${drive}:" + $normalized.Substring(2)
    }
    return "file://$env:COMPUTERNAME/$(Get-GhosttyUriPath $normalized)"
}

# Build the OSC 7777 prompt report for the current shell state.
#
#     ESC ] 7777 ; p ; <hex-encoded UTF-8 JSON> BEL
#
# Two things this buys that the separate OSC 7 / 9;9 / 133 sequences cannot:
#
#   * The JSON is encoded to UTF-8 here, by us, so what goes on the wire does
#     not depend on the console code page. [Console]::Write emits in the
#     child console's encoding, which is a legacy OEM page on a default
#     Windows install, and that transcodes or substitutes every non-ASCII
#     character of a path. Hex digits are ASCII, so no code page can touch
#     them.
#
#   * Hex means no byte of the payload can end the sequence carrying it, so a
#     path holding ESC, BEL or ST cannot truncate its own report, and a
#     replay that stops mid-sequence cannot splice into live bytes and
#     fabricate a path.
#
# Schema v1 fields: v (schema version), cwd, exit, shell. The schema grows
# additively: a reader ignores fields it does not know and a missing field is
# not an error, `v` gates changes that would break an existing field's
# meaning, and absent is distinct from empty (absent means we did not look).
# Reserved and deliberately not sent yet: git_head, git_branch, git_dirty.
# The parser already accepts them; the shell side needs a cache design that
# stays inside the per-prompt budget, and that is not this change.
#
# Kept as its own function so it can be exercised directly, which is how the
# code page behaviour above is measured rather than assumed.
#
# Returns the empty string when the payload will not fit, which the caller
# treats as "send nothing this prompt".
function global:Get-GhosttyPromptReport([string] $cwd, [int] $exitCode) {
    $payload = [ordered]@{
        v     = 1
        cwd   = $cwd
        exit  = $exitCode
        shell = $global:__GhosttyShellName
    }
    $json = ConvertTo-Json -InputObject $payload -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

    # The terminal accepts at most 1024 hex characters, so 512 encoded bytes,
    # and drops anything longer. Checking here rather than sending and being
    # dropped: the shell knows the answer for free, and the alternative is a
    # kilobyte written to the pty and a warning logged on every single prompt
    # for as long as the user stands in that directory. Measured ceiling, in
    # path characters past the drive: 468 ASCII, 234 Latin-1, 156 CJK, since
    # a character outside ASCII costs two to four bytes and each separator
    # costs two. OSC 7 and OSC 9;9 still carry the directory on those
    # prompts, so the loss is the report and not the path.
    $__GhosttyMaxReportBytes = 512
    if ($bytes.Length -gt $__GhosttyMaxReportBytes) { return '' }

    $hex = if ($global:__GhosttyHasToHexString) {
        [Convert]::ToHexString($bytes)
    } else {
        [BitConverter]::ToString($bytes).Replace('-', '')
    }
    return "$($global:__GhosttyEsc)]7777;p;$hex$($global:__GhosttyBel)"
}

# Capture the user's existing prompt function so frameworks like
# Oh-My-Posh, Starship, posh-git, etc. keep working. We invoke it from
# inside our wrapper to obtain the prompt text.
$global:__GhosttyOriginalPrompt = $function:prompt

# Track whether we emitted OSC 133;C for a running command so the next
# prompt can close it with OSC 133;D;<exitcode>. Initialized false so a
# brand-new shell does not emit a spurious D before the first command.
$global:__GhosttyEmittedC = $false

function global:prompt {
    # Capture exit status FIRST, before any other operation can clobber
    # $LASTEXITCODE or $?. $LASTEXITCODE is $null until the first native
    # process runs in the session; treat that as success (0).
    $lastNative = $LASTEXITCODE
    $lastOk = $?
    if ($null -eq $lastNative) { $lastNative = 0 }
    # Cmdlet failures set $? to false without setting $LASTEXITCODE
    # (no native process ran). Synthesize 1 for that case so the OSC
    # 133;D consumer sees a non-zero exit and can render an error mark.
    $exitCode = if ($lastOk) { 0 } else { if ($lastNative -ne 0) { $lastNative } else { 1 } }

    # Close out the previous command's execution window if we opened one.
    if ($global:__GhosttyEmittedC) {
        [Console]::Write("$($global:__GhosttyEsc)]133;D;$exitCode$($global:__GhosttyBel)")
        $global:__GhosttyEmittedC = $false
    }

    # Report cwd. OSC 7 gives a file:// URI for terminals that prefer the
    # standard; OSC 9;9 gives the raw Windows path which is what
    # Wintty/Windows Terminal historically consume.
    $cwd = (Get-Location).ProviderPath
    if ($cwd) {
        $uri = Get-GhosttyFileUri $cwd
        [Console]::Write("$($global:__GhosttyEsc)]7;$uri$($global:__GhosttyBel)")
        [Console]::Write("$($global:__GhosttyEsc)]9;9;$cwd$($global:__GhosttyBel)")

        # The structured report carries the same directory losslessly plus
        # the rest of the prompt's state. Additive: the two sequences above
        # stay exactly as they are for consumers that only speak those.
        # Wrapped because nothing about reporting is worth breaking a prompt
        # over; a terminal that never receives it simply falls back to them.
        try {
            $report = Get-GhosttyPromptReport $cwd $exitCode
            if ($report) { [Console]::Write($report) }
        } catch {
        }
    }

    # Prompt start.
    [Console]::Write("$($global:__GhosttyEsc)]133;A$($global:__GhosttyBel)")

    # Delegate to the user's previous prompt for the actual text. If they
    # had no custom prompt, fall back to the PowerShell default form.
    $text = if ($global:__GhosttyOriginalPrompt) {
        & $global:__GhosttyOriginalPrompt
    } else {
        "PS $cwd> "
    }

    # Prompt end / command input start.
    [Console]::Write("$($global:__GhosttyEsc)]133;B$($global:__GhosttyBel)")

    # Restore $LASTEXITCODE so the next command sees the same value the
    # user's prompt observed. PowerShell does not let us restore $?.
    $global:LASTEXITCODE = $lastNative

    return $text
}

# Hook Enter via PSReadLine to emit OSC 133;C right before the command
# starts executing. PSReadLine ships with Windows PowerShell 5.1 and is
# the default line editor in PowerShell 7; if it's somehow missing we
# silently skip this and lose the C/D bracket (A/B still work).
if (Get-Module -ListAvailable -Name PSReadLine) {
    try {
        Import-Module PSReadLine -ErrorAction Stop
        Set-PSReadLineKeyHandler -Chord Enter -ScriptBlock {
            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
            [Console]::Write("$($global:__GhosttyEsc)]133;C$($global:__GhosttyBel)")
            $global:__GhosttyEmittedC = $true
        }
    } catch {
        # PSReadLine present in module list but failed to import or bind
        # the key handler. Continue without C/D marks rather than
        # breaking the user's shell.
    }
}
