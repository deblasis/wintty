#requires -Version 7
<#
    Shared process policy for the GUI harnesses in this directory.

    These scripts used to open with `Get-Process Wintty | Stop-Process -Force`
    to get a clean slate. That kills every Wintty on the machine, including
    builds from other worktrees and the window the developer is working in,
    which is not a harness's call to make.

    The replacement is three rules:

      1. Refuse to start while a Wintty is running from the exe under test
         (Assert-NoWinttyFrom). Say which pids, so the developer can close
         them. Such an instance is the one thing a run cannot work around:
         dotnet build cannot overwrite its locked exe, and a sweep by exe
         path cannot tell it from the run's own launches.

      2. Run beside any other Wintty only through the coexistence guard
         below (Test-WinttyCoexistence / Assert-WinttyCoexistence): a launch
         may run beside instances somebody else started when, and only
         when, it proves before launching that it can neither reach them
         nor share any state with them. See the guard's own header for the
         list. Start-SeamSession runs it before every launch.

      3. Clean up only what the run started, identified by start time and,
         where the caller knows it, image path. Anything that cannot be
         positively identified is left alone: an unreadable path or start
         time is a reason to skip a process, never a reason to kill it.

    Assert-NoWintty, the old blanket refusal, is left for a harness that
    cannot share the machine with any Wintty however it isolates itself,
    and it says why.

    Dot-source it:

        . (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
#>

# Throws if any Wintty is running. Only for a harness with a reason no
# isolation removes; -Reason says it, and the refusal quotes it. The default
# is the reason that holds for a launch that does not isolate its state.
# Call once, before the first launch.
function Assert-NoWintty {
    param(
        [string]$Context = 'This harness',
        [string]$Reason = ('it shares crash.log and the state directory with them, so their ' +
                           'crashes would be read as belonging to this run')
    )

    $running = @(Get-Process Wintty -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    $pids = ($running | ForEach-Object { $_.Id }) -join ', '
    throw ("close the running Wintty first (pid: $pids). " +
           "$Context cannot run beside any Wintty: $Reason. " +
           'It will not stop instances it did not start.')
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
#
# Lexically under is not enough: a junction or symlink under the root that
# points outside it passes the string compare while every write through it
# lands elsewhere (the real per-user state dir, say). So every segment that
# exists below the root is also checked for a reparse point, and one there
# is a no. Segments that do not exist yet cannot redirect anything.
#
# A segment whose attributes cannot be read (access denied, a sharing
# violation, anything but "not there") is a no as well: what cannot be
# shown not to redirect is treated as redirecting.
function Test-WinttyPathUnder(
    [string]$Path,
    [string]$Root,
    # How a segment's attributes are read; [IO.File]::GetAttributes when
    # omitted. A seam for the tests, which need a read that fails.
    [scriptblock]$ReadAttributes = { param($p) [System.IO.File]::GetAttributes($p) }
) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { return $false }
    $pFull = [WinttyLongPath]::Of([System.IO.Path]::GetFullPath($Path))
    $p = ConvertTo-WinttyPathKey $pFull
    $r = ConvertTo-WinttyPathKey ([WinttyLongPath]::Of([System.IO.Path]::GetFullPath($Root)))
    if (-not ($p -eq $r -or $p.StartsWith($r + '\', [StringComparison]::Ordinal))) { return $false }
    $seg = $pFull.TrimEnd('\')
    while ($seg -and (ConvertTo-WinttyPathKey $seg).Length -gt $r.Length) {
        try { $attrs = & $ReadAttributes $seg }
        catch {
            $inner = $_.Exception
            while ($inner -is [System.Management.Automation.MethodInvocationException] -and $inner.InnerException) { $inner = $inner.InnerException }
            if ($inner -is [System.IO.FileNotFoundException] -or $inner -is [System.IO.DirectoryNotFoundException]) { $attrs = $null }
            else { return $false }
        }
        if ($null -ne $attrs -and ($attrs -band [System.IO.FileAttributes]::ReparsePoint)) { return $false }
        $seg = [System.IO.Path]::GetDirectoryName($seg)
    }
    return $true
}

# ---- the staged config, read the way the app reads it -------------------------

# Every value the config text gives $Key, in file order, by the rules of the
# app's own reader (Ghostty.Core ConfigIniFile): a line ends at CR, LF or
# CRLF, leading blanks are dropped, '#' lines and lines without '=' are
# skipped, the key before the first '=' is trimmed and matched
# case-insensitively, and an empty value is ignored. The app acts on the
# FIRST value, so a rule that must hold has to hold for every one of them.
function Get-WinttyConfigValues([AllowEmptyString()][string]$ConfigText, [Parameter(Mandatory)][string]$Key) {
    $values = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($ConfigText -split "\r\n|\r|\n")) {
        $t = $line.TrimStart()
        if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
        $eq = $t.IndexOf('=')
        if ($eq -lt 0) { continue }
        if (-not $t.Substring(0, $eq).Trim().Equals($Key, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $v = $t.Substring($eq + 1).Trim()
        if ($v.Length -gt 0) { $values.Add($v) }
    }
    return , $values
}

# The quick-terminal chord every harness launch stages: Ctrl+Alt+Shift+F24.
# The quick terminal's hotkey is session-global (RegisterHotKey), keyed by
# neither the AUMID nor any environment variable, and it defaults to
# Ctrl+`. A harness left at the default would take the user's chord when
# it starts first, or fail to register it and log a warning when the user's
# instance holds it. F24 has no key on a real keyboard, so nobody binds it.
# There is no "off" value: a value the app cannot parse falls back to
# Ctrl+`, which is why this must stay a chord QuickTerminalKeyChord.Parse
# accepts (Ghostty.Tests pins that).
function Get-WinttyHarnessQuickTerminalKey { return 'ctrl+alt+shift+f24' }

# The config text a harness launch stages: the harness's own text plus the
# harness quick-terminal chord when the text binds none. A text that binds
# its own chord keeps it, and the coexistence guard then refuses to launch
# it beside another instance.
function Add-WinttyHarnessConfigDefaults([AllowEmptyString()][string]$ConfigText) {
    if ((Get-WinttyConfigValues $ConfigText 'quick-terminal-key').Count -gt 0) { return $ConfigText }
    $text = if ([string]::IsNullOrEmpty($ConfigText)) { '' } else { $ConfigText.TrimEnd("`r", "`n") + "`n" }
    return $text + "quick-terminal-key = $(Get-WinttyHarnessQuickTerminalKey)`n"
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
    return Get-WinttyBuildConstant -ExePath $ExePath -Namespace 'Ghostty.Core.Version' -Type 'BuildInfo' -Field 'AumId'
}

# The folder name the build keeps its per-user state under
# (Ghostty.Core.AppIdentity.StateDirName), read the same way. $null when
# there is no Ghostty.Core.dll beside the exe.
function Get-WinttyBuildStateDirName([Parameter(Mandatory)][string]$ExePath) {
    return Get-WinttyBuildConstant -ExePath $ExePath -Namespace 'Ghostty.Core' -Type 'AppIdentity' -Field 'StateDirName'
}

# A string constant out of the metadata of the Ghostty.Core.dll beside the
# exe; nothing is loaded or run.
function Get-WinttyBuildConstant {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$Namespace,
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$Field
    )
    # String work and one existence probe: Join-Path and Split-Path would
    # throw on a drive that does not exist, and a throw is not an answer.
    $dll = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($ExePath)), 'Ghostty.Core.dll')
    if (-not [System.IO.File]::Exists($dll)) { return $null }
    $stream = [System.IO.File]::OpenRead($dll)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (-not $pe.HasMetadata) { return $null }
            $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($typeHandle in $md.TypeDefinitions) {
                $typeDef = $md.GetTypeDefinition($typeHandle)
                if ($md.GetString($typeDef.Name) -cne $Type -or
                    $md.GetString($typeDef.Namespace) -cne $Namespace) { continue }
                foreach ($fieldHandle in $typeDef.GetFields()) {
                    $fieldDef = $md.GetFieldDefinition($fieldHandle)
                    if ($md.GetString($fieldDef.Name) -cne $Field) { continue }
                    $constantHandle = $fieldDef.GetDefaultValue()
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

      - its own exe: no running instance was started from it, and it is
        not an installed app, running or not (a build output or a temp
        publish): not inside a running instance's install directory, not
        under Program Files, and not in a Velopack install, which keeps
        Update.exe in the directory above the app's own (a per-user
        install sits at %LOCALAPPDATA%\<pack id>, the app in its current\);
      - config: XDG_CONFIG_HOME under the temp directory and
        WINTTY_TEST_CONFIG=1, so the app itself refuses any config outside
        temp;
      - state: WINTTY_STATE_BASE set and under the temp directory, so logs,
        crash.log, session and window state are the run's own;
      - native state: XDG_STATE_HOME and XDG_CACHE_HOME set and under the
        temp directory. WINTTY_STATE_BASE moves the shell's tree only;
        libghostty resolves its crash envelopes, sentry bookkeeping and
        caches through these two, and falls back to %LOCALAPPDATA%, which
        the installed app shares;
      - single instance: every windows-single-instance line of the staged
        config is false, so the launch neither forwards to nor takes
        forwards from anybody else's instance (the app acts on the first
        line, key matched case-insensitively; requiring every line makes
        the order moot);
      - quick terminal: every quick-terminal-key line of the staged config
        is the harness chord (Get-WinttyHarnessQuickTerminalKey), so the
        launch neither takes the user's session-global hotkey nor fails to
        register it beside the user's instance;
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

    It is a check at launch time. An instance somebody starts after it
    passed is not seen; the one thing such an instance and the run then
    share is the toast registration and jump list of their AUMID, and only
    when both are the same edition, which the next launch refuses again.
#>
function Test-WinttyCoexistence {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        # The config text the launch stages.
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
        [string]$TempRoot,
        # Where machine-wide installs live; Program Files when omitted.
        [string[]]$InstallBases
    )
    if (-not $PSBoundParameters.ContainsKey('Environment')) {
        $Environment = [System.Environment]::GetEnvironmentVariables()
    }
    if (-not $PSBoundParameters.ContainsKey('Instances')) { $Instances = Get-WinttyInstances }
    if (-not $TempRoot) { $TempRoot = [System.IO.Path]::GetTempPath() }
    if (-not $PSBoundParameters.ContainsKey('InstallBases')) {
        $InstallBases = @([Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('ProgramFilesX86')) |
            Where-Object { $_ }
    }

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
    # Nor any installed app that is not running: under Program Files, or a
    # Velopack install (Update.exe in the directory above the exe's own).
    # Both are read off the run's own exe path only.
    foreach ($base in @($InstallBases)) {
        $b = ConvertTo-WinttyPathKey $base
        if ($b -and @($mine | Where-Object { $_.StartsWith($b + '\', [StringComparison]::Ordinal) }).Count -gt 0) {
            $why.Add("the exe under test sits under $base, where installed apps live; launch a build output or a temp publish")
        }
    }
    # String work plus one existence probe: Join-Path and Split-Path would
    # throw on a drive that does not exist, and a throw is not a verdict.
    $installDir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($ExePath)))
    if ($installDir -and [System.IO.File]::Exists([System.IO.Path]::Combine($installDir, 'Update.exe'))) {
        $why.Add("the exe under test is an installed app: $installDir holds Velopack's Update.exe; launch a build output or a temp publish")
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
    foreach ($name in 'XDG_STATE_HOME', 'XDG_CACHE_HOME') {
        $value = & $read $name
        if (-not $value) {
            $why.Add("$name is not set, so libghostty's native state (crash envelopes, sentry, caches) would land in the per-user %LOCALAPPDATA% tree")
        }
        elseif (-not (Test-WinttyPathUnder $value $TempRoot)) {
            $why.Add("$name '$value' is not under the temp directory")
        }
    }

    # Every line, not the one the app happens to act on: the election reads
    # the first, and a guard reading any single line proves nothing about
    # the others.
    $single = Get-WinttyConfigValues $ConfigText 'windows-single-instance'
    $notOff = @($single | Where-Object { $_ -cne 'false' })
    if ($single.Count -eq 0 -or $notOff.Count -gt 0) {
        $why.Add("the staged config sets windows-single-instance to '$(if ($single.Count -eq 0) { '(default: true)' } else { $single -join "', '" })', so the launch could forward to, or take forwards from, another instance; stage 'windows-single-instance = false' and no other value for it")
    }

    # The quick terminal's hotkey is session-global: left at Ctrl+`, the
    # launch takes the user's chord when it registers first, and fails to
    # register (and logs a warning) when the user's instance holds it.
    $chord = Get-WinttyHarnessQuickTerminalKey
    $chords = Get-WinttyConfigValues $ConfigText 'quick-terminal-key'
    if ($chords.Count -eq 0 -or @($chords | Where-Object { $_ -ine $chord }).Count -gt 0) {
        $why.Add("the staged config binds quick-terminal-key to '$(if ($chords.Count -eq 0) { '(default: ctrl+backquote)' } else { $chords -join "', '" })', a session-global hotkey the user's Wintty holds; stage 'quick-terminal-key = $chord' and no other value for it (Start-SeamSession does when the config binds none)")
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
        # The build's own AUMID wins: a passed one only fills in where the
        # exe's cannot be read (a NativeAOT publish), and one that disagrees
        # with what the build carries is a caller that got the wrong build.
        $ownAumId = Get-WinttyBuildAumid $ExePath
        if ($ownAumId) {
            if ($AumId -and $AumId -ine $ownAumId) {
                $why.Add("the AUMID passed for this build ($AumId) is not the one its Ghostty.Core.dll carries ($ownAumId)")
            }
            $AumId = $ownAumId
        }
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

# The up-front check every harness takes: refuse only an instance of THIS
# build, which shares the exe with the run and which a sweep by exe path
# could not tell apart from the run's own. Whether the launch may go ahead
# beside anything else is the guard's call, made right before the launch
# (Start-SeamSession does it).
function Assert-NoWinttyFrom {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [string]$Context = 'This harness',
        # The running instances ({ Id, Path }); the live process table when
        # omitted. A seam for the tests.
        [object[]]$Instances
    )
    # Resolved against the PowerShell location, as Start-SeamSession's
    # Resolve-Path does: GetFullPath alone would use the process directory,
    # which a harness that changed location no longer shares.
    $ExePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
    if (-not $PSBoundParameters.ContainsKey('Instances')) { $Instances = Get-WinttyInstances }
    $mine = Get-WinttyExeSpellings $ExePath
    $own = @($Instances | Where-Object { $_.Path -and $mine -contains (ConvertTo-WinttyPathKey $_.Path) })
    if ($own.Count -eq 0) { return }
    throw ("close the Wintty running from $ExePath first (pid: $(($own | ForEach-Object { $_.Id }) -join ', ')). " +
        "$Context launches that exe itself, and only ever stops what it started.")
}

# For a harness whose verdict rests on wall-clock budgets or frame timing.
# Another Wintty rendering beside the run competes for the same CPU, GPU and
# compositor and can push a figure over its budget. That skews a number
# without crossing any state, so it warns rather than refuses, and names
# the pids so a missed budget can be read against them.
function Write-WinttyTimingNeighbourWarning {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [string]$Context = 'This harness',
        # The running instances ({ Id, Path }); the live process table when
        # omitted. A seam for the tests.
        [object[]]$Instances
    )
    $others = @(Get-WinttyOtherInstances -ExePath $ExePath -Instances $(if ($PSBoundParameters.ContainsKey('Instances')) { $Instances } else { Get-WinttyInstances }))
    if ($others.Count -eq 0) { return }
    Write-Warning ("$Context measures timings, and Wintty pid(s) $(($others | ForEach-Object { $_.Id }) -join ', ') " +
        'run beside it: a budget missed here may be their load rather than the build''s. Close them for a clean figure.')
}

# The running instances that are not from $ExePath (resolved against the
# PowerShell location). An instance whose path cannot be read counts as
# another one: it cannot be shown to be this build's.
function Get-WinttyOtherInstances {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [AllowEmptyCollection()][object[]]$Instances = @()
    )
    $ExePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
    $mine = Get-WinttyExeSpellings $ExePath
    return @($Instances | Where-Object { $null -ne $_ -and -not ($_.Path -and $mine -contains (ConvertTo-WinttyPathKey $_.Path)) })
}

# ---- owned state trees --------------------------------------------------------

# A state tree a run hands to its launches, and may hand to a child script,
# is marked as that run's: a marker file inside it holds a random token, and
# the token travels in WINTTY_STATE_BASE_TOKEN beside WINTTY_STATE_BASE.
# Start-SeamSession adopts a caller's tree only when the two agree. A
# WINTTY_STATE_BASE alone proves nothing: every pane of a harness-launched
# Wintty inherits the one its app runs on, so a harness started from such a
# shell would otherwise adopt a live instance's tree and share its
# crash.log, session and window state. The token is kept out of the app's
# environment for exactly that reason (Start-SeamSession strips it).
function Get-WinttyStateOwnerMarkerName { return '.wintty-state-owner' }

# Mints a fresh owned tree under the temp directory, or under $Under when
# given, and returns { Path, Token }. It sets nothing; the caller exports
# both variables when it wants a child to adopt the tree.
function New-WinttyOwnedStateBase {
    param([string]$Under = [System.IO.Path]::GetTempPath(), [string]$Prefix = 'wintty-state-')
    $bytes = [byte[]]::new(16)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $token = [System.Convert]::ToHexString($bytes).ToLowerInvariant()
    $path = Join-Path $Under ($Prefix + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $path (Get-WinttyStateOwnerMarkerName)), $token)
    return [pscustomobject]@{ Path = $path; Token = $token }
}

# Whether $Path is an owned tree under the temp directory whose marker holds
# $Token. A missing, empty or unreadable marker is a no.
function Test-WinttyOwnedStateBase([string]$Path, [string]$Token, [string]$TempRoot = [System.IO.Path]::GetTempPath()) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Token)) { return $false }
    if (-not (Test-WinttyPathUnder $Path $TempRoot)) { return $false }
    $marker = Join-Path $Path (Get-WinttyStateOwnerMarkerName)
    $held = try { [System.IO.File]::ReadAllText($marker).Trim() } catch { '' }
    return $held.Length -gt 0 -and $held -ceq $Token.Trim()
}

# ---- a verdict before the expensive part ---------------------------------------

# The coexistence verdict a launch of $ExePath would get, computed before a
# harness builds or publishes anything, so a refusal costs seconds rather
# than a build. The environment is a synthetic, fully isolated one (paths
# under temp that are not created): what is judged is everything the
# harness's own staging cannot change, namely the exe, the build's AUMID
# and the running instances, plus the config text when one is given. The
# real launch still goes through the guard.
function Test-WinttyCoexistencePreflight {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [string]$AumId,
        [AllowEmptyString()][string]$ConfigText = (Add-WinttyHarnessConfigDefaults "windows-single-instance = false`n"),
        [object[]]$Instances,
        [System.Collections.IDictionary]$Registrations
    )
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('wintty-preflight-' + [guid]::NewGuid().ToString('N'))
    $check = @{
        ExePath     = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
        ConfigText  = $ConfigText
        Environment = @{
            WINTTY_TEST_CONFIG = '1'
            XDG_CONFIG_HOME    = $root
            WINTTY_STATE_BASE  = (Join-Path $root 'state')
            XDG_STATE_HOME     = (Join-Path $root 'xdg-state')
            XDG_CACHE_HOME     = (Join-Path $root 'xdg-cache')
        }
    }
    if ($AumId) { $check.AumId = $AumId }
    if ($PSBoundParameters.ContainsKey('Instances')) { $check.Instances = $Instances }
    if ($PSBoundParameters.ContainsKey('Registrations')) { $check.Registrations = $Registrations }
    return Test-WinttyCoexistence @check
}

# ---- session-wide settings ------------------------------------------------------

# A harness that changes something the whole user session shares (system
# parameters such as animations or High Contrast, the light/dark theme and
# accent keys, the wallpaper, the clipboard, or every other app's windows)
# changes it under the user's Wintty too, and every other app. Isolation
# cannot move any of these, so the only safe place for such a change is a
# desktop with no other Wintty on it. Both helpers refuse beside one, with
# the reason; the gate scan in SeamClient.Tests.ps1 fails a harness that
# writes such state without going through them.

# Refuses (throws) when a Wintty other than $ExePath's is running. For a
# harness whose change spans its whole run and whose own finally restores
# it; call it before the first change.
function Assert-WinttySessionStateFree {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        # What is changed, for the message: 'the desktop wallpaper'.
        [Parameter(Mandatory)][string]$What,
        [string]$Context = 'This harness',
        [object[]]$Instances
    )
    $others = @(Get-WinttyOtherInstances -ExePath $ExePath -Instances $(if ($PSBoundParameters.ContainsKey('Instances')) { $Instances } else { Get-WinttyInstances }))
    if ($others.Count -eq 0) { return }
    throw ("HARNESS: skipped: $Context changes $What, which the whole session shares, and Wintty pid(s) " +
        "$(($others | ForEach-Object { $_.Id }) -join ', ') run beside it; close them to run it. Nothing was changed.")
}

# The scoped form: refuses like the above, then runs $Change and always
# runs $Restore after it, in a finally. -SkipBeside turns the refusal into a
# printed skip that returns $false, for a change the run can do without.
# Returns $true when $Change ran.
function Invoke-WinttySessionStateChange {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$What,
        [Parameter(Mandatory)][scriptblock]$Change,
        [scriptblock]$Restore,
        [string]$Context = 'This harness',
        [switch]$SkipBeside,
        [object[]]$Instances
    )
    $check = @{ ExePath = $ExePath; What = $What; Context = $Context }
    if ($PSBoundParameters.ContainsKey('Instances')) { $check.Instances = $Instances }
    try { Assert-WinttySessionStateFree @check }
    catch {
        if (-not $SkipBeside) { throw }
        Write-Host ($_.Exception.Message -replace '^HARNESS: skipped: ', 'skipped: ')
        return $false
    }
    try { $null = & $Change }
    finally { if ($Restore) { $null = & $Restore } }
    return $true
}
