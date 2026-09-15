#requires -Version 7
<#
    Shared process policy for the GUI harnesses in this directory.

    These scripts used to open with `Get-Process Wintty | Stop-Process -Force`
    to get a clean slate. That kills every Wintty on the machine, including
    builds from other worktrees and the window the developer is working in,
    which is not a harness's call to make.

    The replacement is two rules:

      1. Refuse to start while any Wintty is running. Say which pids, so the
         developer can close them. This is not about the single-instance
         mutex - that is keyed on a hash of the exe path, so another
         worktree's build would not collide. It is that state is shared:
         crash.log lives under %LOCALAPPDATA% per user rather than per exe
         path, and a harness that reads it cannot tell whose crash it saw.

      2. Clean up only what the run started, identified by start time and,
         where the caller knows it, image path. Anything that cannot be
         positively identified is left alone: an unreadable path or start
         time is a reason to skip a process, never a reason to kill it.

    Rule 1 has one sanctioned exception, the coexistence guard below
    (Test-WinttyCoexistence / Assert-WinttyCoexistence): a launch may run
    beside Wintty instances somebody else started when, and only when, it
    proves before launching that it can neither reach them nor share any
    state with them. See the guard's own header for the list.

    Dot-source it:

        . (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
#>

# Throws if any Wintty is running. Call once, before the first launch.
function Assert-NoWintty {
    param([string]$Context = 'This harness')

    $running = @(Get-Process Wintty -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    $pids = ($running | ForEach-Object { $_.Id }) -join ', '
    throw ("close the running Wintty first (pid: $pids). " +
           "$Context shares crash.log and the state directory with it, so " +
           'its crashes would be read as belonging to this run, and this ' +
           'harness will not kill instances it did not start.')
}

# The timestamp to hand to Stop-WinttyStartedAfter. Take it immediately
# before the first Start-Process, not at script start: anything earlier
# widens the window in which an unrelated instance looks like ours.
function Get-WinttyLaunchStamp { return Get-Date }

# Kill the Wintty processes this run started. Fails closed on anything it
# cannot identify.
function Stop-WinttyStartedAfter {
    param(
        [Parameter(Mandatory)][datetime]$Since,
        # Optional, and worth passing whenever the caller knows which exe it
        # launched: start time alone will also match an instance the
        # developer opened while the run was in flight. Omit it to mean that
        # deliberately; passing $null or '' is treated as a filter that could
        # not be built, and sweeps nothing.
        [string]$ExePath,
        [int]$TimeoutMs = 3000,
        # The processes to consider; defaults to every running Wintty. A
        # seam for the tests, which hand in stand-ins that record a Kill
        # rather than risk a real sweep on a machine with real instances.
        [object[]]$Processes
    )

    # Three cases, and the distinction matters: a caller that omitted -ExePath
    # is knowingly sweeping on time alone, but a caller that PASSED something
    # empty or unresolvable meant to filter and failed to. Treating those the
    # same is a fail-open: an unreadable path swept nothing while $null swept
    # everything, which is backwards.
    $spellings = $null
    if ($PSBoundParameters.ContainsKey('ExePath')) {
        if ([string]::IsNullOrWhiteSpace($ExePath)) { return }
        $full = (Resolve-Path -LiteralPath $ExePath -ErrorAction SilentlyContinue)?.Path
        if (-not $full) { return }
        # The process table reports an image path as it was launched, so an
        # 8.3 spelling (C:\Users\ABCDEF~1\...) and its long form both name
        # this exe. Either matches; nothing else does.
        $spellings = Get-WinttyExeSpellings $full
    }

    if (-not $PSBoundParameters.ContainsKey('Processes')) {
        $Processes = @(Get-Process Wintty -ErrorAction SilentlyContinue)
    }
    foreach ($p in @($Processes)) {
        # Process.StartTime and .Path return null or throw for a process the
        # harness cannot open (elevated, another session, or one that exited
        # between enumeration and the read). Skip those.
        $started = try { $p.StartTime } catch { $null }
        if ($null -eq $started -or $started -lt $Since) { continue }

        if ($spellings) {
            $path = try { $p.Path } catch { $null }
            if ([string]::IsNullOrEmpty($path) -or
                $spellings -notcontains (ConvertTo-WinttyPathKey $path)) { continue }
        }

        # Kill the tree: the shell runs as a child, and a wedged one would
        # otherwise outlive every run.
        try { $p.Kill($true); [void]$p.WaitForExit($TimeoutMs) } catch { }
    }
}

# ---- paths ------------------------------------------------------------------

# The comparison key for a path: quotes and trailing separators dropped,
# separators unified, case folded. Pure string work, so it is safe on a path
# that belongs to somebody else's install: nothing is looked up on disk.
function ConvertTo-WinttyPathKey([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return $Path.Trim().Trim('"').Replace('/', '\').TrimEnd('\').ToLowerInvariant()
}

if (-not ('WinttyLongPath' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinttyLongPath {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);
    // The path with every 8.3 segment of its longest existing prefix
    // expanded; the rest is appended as spelled. The input when nothing
    // along it exists.
    public static string Of(string path) {
        string head = path, tail = "";
        while (!string.IsNullOrEmpty(head)) {
            var sb = new StringBuilder(1024);
            uint n = GetLongPathNameW(head, sb, (uint)sb.Capacity);
            if (n > 0 && n < sb.Capacity) return sb.ToString().TrimEnd('\\') + tail;
            var parent = System.IO.Path.GetDirectoryName(head);
            if (string.IsNullOrEmpty(parent) || parent == head) break;
            tail = "\\" + head.Substring(parent.Length).TrimStart('\\') + tail;
            head = parent;
        }
        return path;
    }
}
'@
}

# The keys an exe of THIS run can show up under in the process table: as
# given and with its 8.3 segments expanded (the table reports a path the way
# it was launched). Expanding a name looks it up on disk, so this is only
# ever called on paths the run owns, never on another instance's.
function Get-WinttyExeSpellings([Parameter(Mandatory)][string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    return @(@((ConvertTo-WinttyPathKey $full),
            (ConvertTo-WinttyPathKey ([WinttyLongPath]::Of($full)))) | Select-Object -Unique)
}

# Whether $Path is $Root or lies under it. Both are the run's own (its temp
# roots, the temp directory), so both are expanded before the compare.
function Test-WinttyPathUnder([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { return $false }
    $p = ConvertTo-WinttyPathKey ([WinttyLongPath]::Of([System.IO.Path]::GetFullPath($Path)))
    $r = ConvertTo-WinttyPathKey ([WinttyLongPath]::Of([System.IO.Path]::GetFullPath($Root)))
    return $p -eq $r -or $p.StartsWith($r + '\', [StringComparison]::Ordinal)
}

# ---- what is running, and as which edition ----------------------------------

# Every running Wintty as { Id, Path, StartTime }. Path and StartTime are
# $null for a process this user cannot open.
function Get-WinttyInstances {
    return @(Get-Process Wintty -ErrorAction SilentlyContinue | ForEach-Object {
        $p = $_
        $path = try { $p.Path } catch { $null }
        $started = try { $p.StartTime } catch { $null }
        [pscustomobject]@{ Id = $p.Id; Path = $path; StartTime = $started }
    })
}

# The executable out of a toast activator's LocalServer32 command line: the
# first quoted run, or the whole value when it is unquoted. The same rule as
# Ghostty.Core's ToastRegistration.ServerExecutable.
function Get-WinttyActivatorExe([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $null }
    $c = $CommandLine.Trim()
    if ($c[0] -ne '"') { return $c }
    $close = $c.IndexOf('"', 1)
    if ($close -gt 1) { return $c.Substring(1, $close - 1) }
    return $null
}

# AUMID -> the executable its toast activator starts, for every registration
# under HKCU\Software\Classes\AppUserModelId that names one. Read-only.
# Every launch re-points the registration of its own AUMID at its own image
# (Ghostty.Core ToastRegistration), so a running instance is found here
# under the AUMID it runs as.
function Get-WinttyToastRegistrations {
    $map = @{}
    $root = 'HKCU:\Software\Classes\AppUserModelId'
    foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
        $clsid = (Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue).CustomActivator
        if ([string]::IsNullOrWhiteSpace($clsid)) { continue }
        $server = (Get-ItemProperty -LiteralPath "HKCU:\Software\Classes\CLSID\$clsid\LocalServer32" `
                -ErrorAction SilentlyContinue).'(default)'
        $exe = Get-WinttyActivatorExe $server
        if ($exe) { $map[$key.PSChildName] = $exe }
    }
    return $map
}

# The AUMID a build registers its toasts and jump list under: the constant
# Ghostty.Core.Version.BuildInfo.AumId, read out of the metadata of the
# Ghostty.Core.dll beside the exe, so nothing is loaded or run. $null when
# there is no such assembly (a single-file or AOT publish); the caller then
# has to say which AUMID it launches.
function Get-WinttyBuildAumid([Parameter(Mandatory)][string]$ExePath) {
    $dll = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($ExePath))) 'Ghostty.Core.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { return $null }
    $stream = [System.IO.File]::OpenRead($dll)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $pe.HasMetadata) { return $null }
            $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($typeHandle in $md.TypeDefinitions) {
                $type = $md.GetTypeDefinition($typeHandle)
                if ($md.GetString($type.Name) -cne 'BuildInfo' -or
                    $md.GetString($type.Namespace) -cne 'Ghostty.Core.Version') { continue }
                foreach ($fieldHandle in $type.GetFields()) {
                    $field = $md.GetFieldDefinition($fieldHandle)
                    if ($md.GetString($field.Name) -cne 'AumId') { continue }
                    $constantHandle = $field.GetDefaultValue()
                    if ($constantHandle.IsNil) { return $null }
                    $blob = $md.GetBlobReader($md.GetConstant($constantHandle).Value)
                    return $blob.ReadUTF16($blob.Length)
                }
            }
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
    return $null
}

# ---- the coexistence guard ----------------------------------------------------
<#
    A launch may run beside Wintty instances it did not start when, and only
    when, every one of these holds. All are checked BEFORE the launch, against
    the environment the child is about to inherit:

      - its own exe: no running instance was started from it, and it does
        not sit inside a running instance's install directory (a build
        output or a temp publish, never the installed app);
      - config: XDG_CONFIG_HOME under the temp directory and
        WINTTY_TEST_CONFIG=1, so the app itself refuses any config outside
        temp;
      - state: WINTTY_STATE_BASE set and under the temp directory, so logs,
        crash.log, session and window state are the run's own;
      - single instance: the staged config turns windows-single-instance
        off, so the launch neither forwards to nor takes forwards from
        anybody else's instance;
      - session daemon, for builds that carry one: a daemon pipe named in
        the environment (WINTTY_SESSIOND_PIPE) is a private name, never a
        per-user one (those end in the user's SID), and the daemon's
        data, log and bin dirs, when named, sit under temp;
      - per-user state no variable moves: every launch re-points the toast
        registration of its AUMID at itself and rebuilds that AUMID's jump
        list. Beside a running instance of the SAME edition there is
        therefore no isolated launch at all, so the build's AUMID must
        differ from every running instance's. A running instance's AUMID is
        read off the toast registration naming its image; one that no
        registration names cannot be told apart from this build, and
        refuses.

    A process whose image path cannot be read refuses too. With no Wintty
    running there is nothing to coexist with and the launch is allowed
    whatever its isolation, which is the behaviour every harness had before.

    Nothing here stops, signals or opens another process: it reads the
    process table and the registry, and expands only paths the run owns.
#>
function Test-WinttyCoexistence {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        # The config text the launch stages (the last windows-single-instance
        # line wins, as in the app).
        [Parameter(Mandatory)][AllowEmptyString()][string]$ConfigText,
        # The AUMID the build runs as; read from its Ghostty.Core.dll when
        # omitted.
        [string]$AumId,
        # The environment the child inherits; this process's when omitted.
        [System.Collections.IDictionary]$Environment,
        # The running instances ({ Id, Path }); the live process table when
        # omitted.
        [object[]]$Instances,
        # AUMID -> activator exe; the live registry when omitted.
        [System.Collections.IDictionary]$Registrations,
        [string]$TempRoot
    )
    if (-not $PSBoundParameters.ContainsKey('Environment')) {
        $Environment = [System.Environment]::GetEnvironmentVariables()
    }
    if (-not $PSBoundParameters.ContainsKey('Instances')) { $Instances = Get-WinttyInstances }
    if (-not $TempRoot) { $TempRoot = [System.IO.Path]::GetTempPath() }

    $running = @($Instances | Where-Object { $null -ne $_ })
    $why = [System.Collections.Generic.List[string]]::new()
    $verdict = [pscustomobject]@{
        Allowed = $true
        Running = $running
        Reasons = $why
    }
    if ($running.Count -eq 0) { return $verdict }

    $mine = Get-WinttyExeSpellings $ExePath
    $own = @($running | Where-Object { $_.Path -and $mine -contains (ConvertTo-WinttyPathKey $_.Path) })
    $foreign = @($running | Where-Object { $_.Path -and $mine -notcontains (ConvertTo-WinttyPathKey $_.Path) })
    foreach ($p in $own) {
        $why.Add("pid $($p.Id) is running from this very exe; close it first (a harness only ever stops what it started)")
    }
    foreach ($p in @($running | Where-Object { [string]::IsNullOrWhiteSpace($_.Path) })) {
        $why.Add("pid $($p.Id): its image path cannot be read, so it cannot be told apart from the build under test")
    }

    # Never the installed app: not inside the directory that holds a running
    # instance's own directory. Compared as keys only; the other install is
    # never looked up on disk.
    foreach ($p in $foreign) {
        $installRoot = ConvertTo-WinttyPathKey (Split-Path -Parent (Split-Path -Parent $p.Path))
        if ($installRoot -and @($mine | Where-Object {
                    $_.StartsWith($installRoot + '\', [StringComparison]::Ordinal) }).Count -gt 0) {
            $why.Add("the exe under test sits inside the install of running pid $($p.Id) ($installRoot); launch a build output or a temp publish, never the installed app")
        }
    }

    $read = { param($name) $v = $Environment[$name]; if ($null -eq $v) { '' } else { [string]$v } }
    if ((& $read 'WINTTY_TEST_CONFIG') -cne '1') {
        $why.Add('WINTTY_TEST_CONFIG is not 1, so the app would accept a config outside temp')
    }
    $xdg = & $read 'XDG_CONFIG_HOME'
    if (-not $xdg) { $why.Add('XDG_CONFIG_HOME is not set, so the launch would read and write the real config') }
    elseif (-not (Test-WinttyPathUnder $xdg $TempRoot)) { $why.Add("XDG_CONFIG_HOME '$xdg' is not under the temp directory") }
    $stateBase = & $read 'WINTTY_STATE_BASE'
    if (-not $stateBase) {
        $why.Add('WINTTY_STATE_BASE is not set, so logs, crash.log, session and window state would be the real per-user ones')
    }
    elseif (-not (Test-WinttyPathUnder $stateBase $TempRoot)) {
        $why.Add("WINTTY_STATE_BASE '$stateBase' is not under the temp directory")
    }

    $single = $null
    foreach ($line in ($ConfigText -split "\r?\n")) {
        if ($line -match '^\s*windows-single-instance\s*=\s*(.*?)\s*$') { $single = $Matches[1] }
    }
    if ($single -cne 'false') {
        $why.Add("the staged config leaves windows-single-instance at '$(if ($null -eq $single) { '(default)' } else { $single })', so the launch could forward to, or take forwards from, another instance; stage 'windows-single-instance = false'")
    }

    $pipe = & $read 'WINTTY_SESSIOND_PIPE'
    if ($pipe -and ($pipe -replace '^\\\\\.\\pipe\\', '') -match '(?i)-S-1-\d+(-\d+)+') {
        $why.Add("WINTTY_SESSIOND_PIPE '$pipe' is a per-user daemon name (it carries the user's SID), so the launch would reach the user's daemon")
    }
    foreach ($name in 'WINTTY_SESSIOND_DATA_DIR', 'WINTTY_SESSIOND_LOG_FILE', 'WINTTY_SESSIOND_BIN_DIR') {
        $value = & $read $name
        if ($value -and -not (Test-WinttyPathUnder $value $TempRoot)) {
            $why.Add("$name '$value' is not under the temp directory")
        }
    }

    if ($foreign.Count -gt 0) {
        if (-not $AumId) { $AumId = Get-WinttyBuildAumid $ExePath }
        if (-not $AumId) {
            $why.Add("the AUMID this build runs as cannot be read (no Ghostty.Core.dll beside $ExePath), so it cannot be compared with the running instances'; pass it explicitly")
        }
        else {
            if (-not $PSBoundParameters.ContainsKey('Registrations')) { $Registrations = Get-WinttyToastRegistrations }
            foreach ($p in $foreign) {
                $key = ConvertTo-WinttyPathKey $p.Path
                $owned = @($Registrations.Keys | Where-Object { (ConvertTo-WinttyPathKey $Registrations[$_]) -ceq $key })
                if ($owned.Count -eq 0) {
                    $why.Add("pid $($p.Id) ($($p.Path)) holds no toast registration, so its edition cannot be told apart from this build's ($AumId)")
                }
                elseif (@($owned | Where-Object { $_ -ieq $AumId }).Count -gt 0) {
                    $why.Add("pid $($p.Id) ($($p.Path)) is the same edition ($AumId): this launch would re-point its toast registration and rebuild its jump list")
                }
            }
        }
    }

    $verdict.Allowed = $why.Count -eq 0
    return $verdict
}

# Test-WinttyCoexistence, as a refusal: throws with every reason when the
# launch may not proceed, returns the verdict when it may.
function Assert-WinttyCoexistence {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ConfigText,
        [string]$AumId,
        [string]$Context = 'This harness'
    )
    $check = @{ ExePath = $ExePath; ConfigText = $ConfigText }
    if ($AumId) { $check.AumId = $AumId }
    $verdict = Test-WinttyCoexistence @check
    if ($verdict.Allowed) { return $verdict }
    $pids = (@($verdict.Running) | ForEach-Object { $_.Id }) -join ', '
    throw ("$Context will not launch beside the running Wintty (pid: $pids):`n  - " +
        ($verdict.Reasons -join "`n  - ") +
        "`nClose them, or make the launch fully isolated (lib/wintty-process.ps1, the coexistence guard). " +
        'Nothing was launched and nothing was stopped.')
}

# The narrow up-front check for a harness that isolates itself: refuse only
# an instance of THIS build, which shares the exe with the run and which a
# sweep by exe path could not tell apart from the run's own. Whether the
# launch may go ahead beside anything else is Start-SeamSession's call,
# through the guard above.
function Assert-NoWinttyFrom {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [string]$Context = 'This harness'
    )
    $mine = Get-WinttyExeSpellings $ExePath
    $own = @(Get-WinttyInstances | Where-Object { $_.Path -and $mine -contains (ConvertTo-WinttyPathKey $_.Path) })
    if ($own.Count -eq 0) { return }
    throw ("close the Wintty running from $ExePath first (pid: $(($own | ForEach-Object { $_.Id }) -join ', ')). " +
        "$Context launches that exe itself, and only ever stops what it started.")
}
