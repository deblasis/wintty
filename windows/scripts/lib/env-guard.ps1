#requires -Version 7
<#
    Snapshot, restore, and verify the machine state the GUI harnesses touch.

    Why this exists, from two real incidents (2026-08-28):

      A verification leg that toggles High Contrast through
      SPI_SETHIGHCONTRAST crashed mid-run and left the machine in High
      Contrast. There was no snapshot to restore from, so the recovery was
      whatever the operator remembered to click.

      An ad-hoc "fix" for focus-follows-hover tried to turn it OFF and turned
      it ON instead. SystemParametersInfo SETs of flat values carry the VALUE
      in pvParam, not a pointer to it; passing a reference made the OS read
      the pointer's own bits as the value, and any heap address is nonzero,
      which is TRUE. The GETs are the opposite: they write through pvParam,
      so they need a real pointer.

    So every SET here goes through one guarded path, and every restore is
    followed by a read-back that must equal the snapshot or the restore
    throws. A restore that "probably worked" is what the incidents had.

    Scope - only what the harnesses are known to move, nothing more:

      High Contrast      SPI_GETHIGHCONTRAST flags, plus the HKCU Themes
                         state around them (CurrentTheme, the HighContrast
                         subkey's Pre-High Contrast Scheme, Preload's
                         default value when that key exists on older
                         layouts)
      window tracking    SPI_GETACTIVEWINDOWTRACKING, SPI_GETACTIVEWNDTRKZORDER,
                         SPI_GETACTIVEWNDTRKTIMEOUT
      desktop            Control Panel\Colors -> Background;
                         Control Panel\Desktop -> WallPaper, TileWallpaper,
                         WallpaperStyle
      app/system theme   Personalize -> AppsUseLightTheme,
                         SystemUsesLightTheme

    Dot-source it:

        . (Join-Path $PSScriptRoot 'lib/env-guard.ps1')

    As a script it runs -SelfTest (SPI-backed round-trip on hover time, which
    is user-invisible) or -Restore (the default snapshot path; this is what
    `just env-restore` calls after a crashed harness).
#>
param(
    [switch]$SelfTest,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$script:EnvGuardSnapshotPath = Join-Path $env:TEMP 'wintty-env-guard.json'

# ---- native ----------------------------------------------------------------
#
# One Add-Type, guarded so a second dot-source in the same session (a harness
# dot-sourcing this after another harness already did) does not collide with
# its own type.
if (-not ('WinttyEnvGuard.Native' -as [type])) {
    Add-Type -Namespace WinttyEnvGuard -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

// The footgun this library exists to prevent, stated once here where the
// overloads sit next to each other:
//
// Which parameter carries the value is decided PER ACTION, and that is the
// trap. The three tracking SETs want the value widened into pvParam itself
// (verified live 2026-08-28: pvParam = 1 read back 1, pvParam = 0 read back
// 0); passing the ADDRESS of the value there - the natural habit from the
// GETs - makes the OS read the pointer's own bits as the value, and any heap
// address is nonzero, so an intended "off" silently means "on". That is the
// 2026-08-28 focus-follows-hover incident, word for word. Hover time is the
// mirror image: its value rides in uiParam, and a value placed in pvParam is
// refused with ERROR_INVALID_PARAMETER.
//
// The ref uint overload is for GETs, which WRITE through pvParam and
// therefore need a real pointer.
//
// CharSet.Unicode on every overload, so the W entry points are what these
// bind to and the SendMessageTimeout string lParam marshals wide.
//
// HIGHCONTRAST deliberately has NO struct overload here. Its GET and SET go
// through the IntPtr overload above with the struct laid out by hand in
// ZEROED unmanaged memory: the scheme member is a string pointer the OS
// dereferences on the SET path, and any garbage left in that field is a
// crash. See Get-HighContrastFlags / Set-HighContrastFlags.
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
}

# SPI actions and the write-through flags. SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
# (3) so a restore persists across reboots and notifies running apps rather
# than only fixing the live query.
$script:SPI_GETHIGHCONTRAST        = 0x0042
$script:SPI_SETHIGHCONTRAST        = 0x0043
$script:SPI_GETACTIVEWINDOWTRACKING = 0x1000
$script:SPI_SETACTIVEWINDOWTRACKING = 0x1001
$script:SPI_GETACTIVEWNDTRKZORDER  = 0x100C
$script:SPI_SETACTIVEWNDTRKZORDER  = 0x100D
$script:SPI_GETACTIVEWNDTRKTIMEOUT = 0x2002
$script:SPI_SETACTIVEWNDTRKTIMEOUT = 0x2003
$script:SPI_GETMOUSEHOVERTIME      = 0x0066
$script:SPI_SETMOUSEHOVERTIME      = 0x0067
$script:SPIF_PERSIST_AND_BROADCAST = 3

$script:ThemesKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes'
$script:PersonalizeKey = Join-Path $script:ThemesKey 'Personalize'
$script:ColorsKey   = 'HKCU:\Control Panel\Colors'
$script:DesktopKey  = 'HKCU:\Control Panel\Desktop'

# ---- small helpers ----------------------------------------------------------

# '(default)' is the registry provider's alias for the nameless default
# value, and only the provider honors it: raw .NET wants the empty string
# there, and GetValueKind('(default)') throws even on a key that has a default
# value. Translate once so the kind read below cannot fail on a value the
# provider just handed us, and keep it inside its own guard: a value whose
# kind cannot be read is an error, not a slot to silently skip.
function Get-RegValueOrNull([string]$Key, [string]$Name) {
    $valueName = if ($Name -eq '(default)') { '' } else { $Name }
    $v = try { Get-ItemPropertyValue -LiteralPath $Key -Name $Name -ErrorAction Stop } catch { $null }
    if ($null -eq $v) { return $null }
    $kind = try { (Get-Item -LiteralPath $Key).GetValueKind($valueName) } catch { throw "ENV_GUARD: GetValueKind failed for $Key :: $valueName : $_" }
    return [ordered]@{ kind = "$kind"; value = $v }
}

# Flat SPI GET: pointer out, uint in.
function Get-SpiUint([uint32]$Action) {
    [uint32]$out = 0
    $ok = [WinttyEnvGuard.Native]::SystemParametersInfo($Action, 0, [ref]$out, 0)
    if (-not $ok) { throw "ENV_GUARD: SystemParametersInfo GET 0x{0:X4} failed" -f $Action }
    return $out
}

# Flat SPI SET for the actions whose value rides in pvParam (the tracking
# booleans and the tracking timeout): the IntPtr is the value widened, never
# the address of anything. See the comment on the IntPtr overload above.
function Set-SpiUint([uint32]$Action, [uint32]$Value) {
    $ok = [WinttyEnvGuard.Native]::SystemParametersInfo(
        $Action, 0, [IntPtr][int64]$Value, $script:SPIF_PERSIST_AND_BROADCAST)
    if (-not $ok) { throw "ENV_GUARD: SystemParametersInfo SET 0x{0:X4} failed (IsLastWin32Error={1})" -f $Action, [System.Runtime.InteropServices.Marshal]::GetLastWin32Error() }
}

# Hover time's mirror-image convention: the value rides in uiParam and a value
# placed in pvParam is refused with ERROR_INVALID_PARAMETER, which is how the
# first draft of this file was caught.
function Set-SpiHoverTime([uint32]$Value) {
    $ok = [WinttyEnvGuard.Native]::SystemParametersInfo(
        $script:SPI_SETMOUSEHOVERTIME, $Value, [IntPtr]::Zero, $script:SPIF_PERSIST_AND_BROADCAST)
    if (-not $ok) { throw "ENV_GUARD: SPI_SETMOUSEHOVERTIME failed (IsLastWin32Error={0})" -f [System.Runtime.InteropServices.Marshal]::GetLastWin32Error() }
}

# HIGHCONTRAST laid out by hand: cbSize and dwFlags as two uint32, then the
# scheme pointer. The block is ZEROED before every call, which is not
# tidiness: AllocHGlobal hands back whatever was in the heap, so an unzeroed
# scheme pointer is a garbage address, and SPI_SETHIGHCONTRAST dereferences
# it. That was a nondeterministic segfault in this harness until the zeroing
# went in - the garbage only lands in the field some of the time.
function Get-HighContrastFlags {
    $size = [IntPtr]::Size + 8
    $p = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        [System.Runtime.InteropServices.Marshal]::Copy([byte[]]::new($size), 0, $p, $size)
        [System.Runtime.InteropServices.Marshal]::WriteInt32($p, 0, $size)
        $ok = [WinttyEnvGuard.Native]::SystemParametersInfo($script:SPI_GETHIGHCONTRAST, [uint32]$size, $p, 0)
        if (-not $ok) { throw 'ENV_GUARD: SPI_GETHIGHCONTRAST failed' }
        return [uint32][System.Runtime.InteropServices.Marshal]::ReadInt32($p, 4)
    }
    finally { [System.Runtime.InteropServices.Marshal]::FreeHGlobal($p) }
}

function Set-HighContrastFlags([uint32]$Flags) {
    $size = [IntPtr]::Size + 8
    $p = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        [System.Runtime.InteropServices.Marshal]::Copy([byte[]]::new($size), 0, $p, $size)
        [System.Runtime.InteropServices.Marshal]::WriteInt32($p, 0, $size)
        [System.Runtime.InteropServices.Marshal]::WriteInt32($p, 4, [int32]$Flags)
        # The scheme member stays zeroed: naming one here would make the SET
        # switch themes as a side effect of restoring the flags.
        $ok = [WinttyEnvGuard.Native]::SystemParametersInfo(
            $script:SPI_SETHIGHCONTRAST, [uint32]$size, $p, $script:SPIF_PERSIST_AND_BROADCAST)
        if (-not $ok) { throw 'ENV_GUARD: SPI_SETHIGHCONTRAST failed' }
    }
    finally { [System.Runtime.InteropServices.Marshal]::FreeHGlobal($p) }
}

# Registry writes do not notify anyone. Broadcast WM_SETTINGCHANGE the way the
# settings UIs do, with the section string the listeners match on, so apps that
# watch the desktop colour or the personalization values re-read them now
# rather than the next time they happen to restart.
function Send-SettingChange([string]$Section) {
    [UIntPtr]$result = [UIntPtr]::Zero
    [void][WinttyEnvGuard.Native]::SendMessageTimeout(
        [IntPtr]0xFFFF, 0x001A, [UIntPtr]::Zero, $Section, 0x0002, 1000, [ref]$result)
}

# ---- snapshot ---------------------------------------------------------------

# Read the machine back into the shape a snapshot stores. Save-EnvSnapshot and
# the post-restore comparison both call this, so what goes into the file and
# what the read-back judges are by construction the same readers, not a cached
# copy of what one of them wrote.
function Read-EnvCurrent {
    return [ordered]@{
        highContrast = [ordered]@{ flags = Get-HighContrastFlags }
        tracking  = [ordered]@{
            enabled = Get-SpiUint $script:SPI_GETACTIVEWINDOWTRACKING
            zOrder  = Get-SpiUint $script:SPI_GETACTIVEWNDTRKZORDER
            timeout = Get-SpiUint $script:SPI_GETACTIVEWNDTRKTIMEOUT
        }
        themes    = [ordered]@{
            currentTheme = Get-RegValueOrNull $script:ThemesKey 'CurrentTheme'
            preHighContrastScheme = Get-RegValueOrNull (Join-Path $script:ThemesKey 'HighContrast') 'Pre-High Contrast Scheme'
            # Older layouts kept the pending scheme under Themes\Preload's
            # default value; captured when present, skipped when not.
            preload = if (Test-Path (Join-Path $script:ThemesKey 'Preload')) {
                Get-RegValueOrNull (Join-Path $script:ThemesKey 'Preload') '(default)'
            } else { $null }
        }
        desktop   = [ordered]@{
            background     = Get-RegValueOrNull $script:ColorsKey 'Background'
            wallpaper      = Get-RegValueOrNull $script:DesktopKey 'WallPaper'
            tileWallpaper  = Get-RegValueOrNull $script:DesktopKey 'TileWallpaper'
            wallpaperStyle = Get-RegValueOrNull $script:DesktopKey 'WallpaperStyle'
        }
        personalize = [ordered]@{
            appsUseLightTheme   = Get-RegValueOrNull $script:PersonalizeKey 'AppsUseLightTheme'
            systemUsesLightTheme = Get-RegValueOrNull $script:PersonalizeKey 'SystemUsesLightTheme'
        }
    }
}

function Save-EnvSnapshot {
    param(
        # Default overwrites: the point is a well-known place `just env-restore`
        # can find after a harness crashed, not an archive of every run.
        [string]$Path = $script:EnvGuardSnapshotPath
    )

    $snap = Read-EnvCurrent
    $snap['takenUtc'] = (Get-Date).ToUniversalTime().ToString('o')
    $snap | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding utf8
    return $Path
}

# ---- restore ----------------------------------------------------------------

function Restore-EnvSnapshot {
    param(
        [string]$Path = $script:EnvGuardSnapshotPath
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "ENV_GUARD: no snapshot at $Path; run a harness that takes one, or Save-EnvSnapshot first"
    }
    $snap = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable

    # --- High Contrast: write the themes state back first, then the SPI with
    # the original flags. That ordering is what the themes system itself does,
    # so the pin and the registry never disagree about whether HC is on.
    foreach ($entry in @(
        @{ key = $script:ThemesKey;                              name = 'CurrentTheme';               slot = $snap['themes']['currentTheme'] },
        @{ key = Join-Path $script:ThemesKey 'HighContrast';     name = 'Pre-High Contrast Scheme';   slot = $snap['themes']['preHighContrastScheme'] },
        @{ key = Join-Path $script:ThemesKey 'Preload';          name = '(default)';                  slot = $snap['themes']['preload'] }
    )) {
        if ($null -ne $entry.slot) {
            if ($entry.name -eq '(default)') {
                # Same alias trap as the reader, plus one more: the provider
                # hands Get-Item back a READ-ONLY key, so the write goes
                # through an explicitly writable OpenSubKey with the empty
                # name, or it fails (Set-ItemProperty would instead create a
                # value literally named "(default)").
                $subKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
                    ($entry.key -replace '^HKCU:\\', ''), $true)
                if ($null -eq $subKey) { throw "ENV_GUARD: cannot open $($entry.key) for write" }
                try {
                    $subKey.SetValue('', $entry.slot['value'],
                        [Microsoft.Win32.RegistryValueKind]$entry.slot['kind'])
                }
                finally { $subKey.Close() }
            }
            else {
                Set-ItemProperty -LiteralPath $entry.key -Name $entry.name -Value $entry.slot['value'] -Type $entry.slot['kind']
            }
        }
    }

    # --- the SPI half of the same state, after the registry writes above.
    Set-HighContrastFlags ([uint32]($snap['highContrast']['flags']))

    # --- window tracking, SPI-backed, value in pvParam (see the overload
    # comment): flat SETs with the persisted-and-broadcast flags.
    Set-SpiUint $script:SPI_SETACTIVEWINDOWTRACKING ([uint32]($snap['tracking']['enabled']))
    Set-SpiUint $script:SPI_SETACTIVEWNDTRKZORDER   ([uint32]($snap['tracking']['zOrder']))
    Set-SpiUint $script:SPI_SETACTIVEWNDTRKTIMEOUT  ([uint32]($snap['tracking']['timeout']))

    # --- desktop and personalization, registry-backed: write, then one
    # broadcast per section the listeners match on.
    foreach ($entry in @(
        @{ key = $script:ColorsKey;     name = 'Background';     slot = $snap['desktop']['background'];     section = 'Control Panel\Colors' },
        @{ key = $script:DesktopKey;    name = 'WallPaper';      slot = $snap['desktop']['wallpaper'];      section = 'Control Panel\Desktop' },
        @{ key = $script:DesktopKey;    name = 'TileWallpaper';  slot = $snap['desktop']['tileWallpaper'];  section = 'Control Panel\Desktop' },
        @{ key = $script:DesktopKey;    name = 'WallpaperStyle'; slot = $snap['desktop']['wallpaperStyle']; section = 'Control Panel\Desktop' },
        @{ key = $script:PersonalizeKey; name = 'AppsUseLightTheme';    slot = $snap['personalize']['appsUseLightTheme'];    section = 'Personalize' },
        @{ key = $script:PersonalizeKey; name = 'SystemUsesLightTheme'; slot = $snap['personalize']['systemUsesLightTheme']; section = 'Personalize' }
    )) {
        if ($null -ne $entry.slot) {
            Set-ItemProperty -LiteralPath $entry.key -Name $entry.name -Value $entry.slot['value'] -Type $entry.slot['kind']
        }
    }
    Send-SettingChange 'Control Panel\Colors'
    Send-SettingChange 'Control Panel\Desktop'
    Send-SettingChange 'Personalize'

    # --- the read-back. This is the guarantee the whole file is here for:
    # a restore that returns without this comparison passing has left the
    # machine with nothing but a hope, which is exactly what both 2026-08-28
    # incidents did. Any mismatch throws with the setting, the expected and the
    # actual, so the harness exits 1 and the operator knows which value to
    # fix by hand rather than hunting for what "something" left behind.
    Compare-EnvToSnapshot $snap
}

function Compare-EnvToSnapshot {
    param([Parameter(Mandatory)]$Snapshot)

    $current = Read-EnvCurrent
    $failures = @()

    foreach ($group in 'highContrast', 'tracking', 'themes', 'desktop', 'personalize') {
        foreach ($name in $Snapshot[$group].Keys) {
            $expected = $Snapshot[$group][$name]
            $actual = $current[$group][$name]
            # Both sides are the @{ kind; value } registry shape or a plain
            # SPI number; compare the value either way.
            $expectedV = if ($expected -is [hashtable] -or $expected -is [System.Collections.Specialized.OrderedDictionary]) { $expected['value'] } else { $expected }
            $actualV   = if ($actual   -is [hashtable] -or $actual   -is [System.Collections.Specialized.OrderedDictionary]) { $actual['value'] }   else { $actual }
            # "$expected" so a DWord read back as [int] and stored as [long]
            # by the JSON round-trip compares by value, not by type.
            if ("$expectedV" -ne "$actualV") {
                $failures += ('{0}.{1}: expected [{2}], actual [{3}]' -f $group, $name, $expectedV, $actualV)
            }
        }
    }

    if ($failures.Count -gt 0) {
        throw ('ENV_GUARD: restore did not hold for ' + $failures.Count + ' setting(s): ' + ($failures -join '; '))
    }
}

# ---- self-test --------------------------------------------------------------

# Runs only when this file is executed as a script, never when dot-sourced:
# a library dot-sourced into a harness must not move state on import.
if ($MyInvocation.InvocationName -ne '.') {
    if ($SelfTest) {
        # One benign knob: hover time. SPI-backed like the settings that
        # matter, user-invisible at +10ms, and NOT High Contrast, wallpaper,
        # or anything else an operator would see move.
        $original = Get-SpiUint $script:SPI_GETMOUSEHOVERTIME
        $scratch = Join-Path $env:TEMP ('wintty-env-guard-selftest-{0}.json' -f (Get-Date -Format 'HHmmss'))
        try {
            [void](Save-EnvSnapshot -Path $scratch)

            Set-SpiHoverTime ([uint32]($original + 10))
            $moved = Get-SpiUint $script:SPI_GETMOUSEHOVERTIME
            if ($moved -ne ($original + 10)) {
                throw "SELFTEST FAIL: hover time read back $moved after setting $($original + 10)"
            }

            Set-SpiHoverTime $original
            $back = Get-SpiUint $script:SPI_GETMOUSEHOVERTIME
            if ($back -ne $original) {
                throw "SELFTEST FAIL: hover time was $original, set back to $back"
            }

            # The full restore + read-back path, against the snapshot taken at
            # the top: state this self-test never touched has to round-trip
            # exactly or the restore's own guarantee is not real.
            Restore-EnvSnapshot -Path $scratch
            Write-Host 'SELFTEST OK'
        }
        finally {
            # Put the knob back no matter which way the assertions above
            # went, then drop the scratch snapshot.
            Set-SpiHoverTime $original
            Remove-Item -LiteralPath $scratch -Force -ErrorAction SilentlyContinue
        }
        exit 0
    }
    elseif ($Restore) {
        Restore-EnvSnapshot
        Write-Host "env restored from $script:EnvGuardSnapshotPath (read-back verified)"
        exit 0
    }
    else {
        Write-Host 'env-guard is a library. Dot-source it for Save-EnvSnapshot / Restore-EnvSnapshot,'
        Write-Host 'or run with -SelfTest or -Restore.'
        exit 1
    }
}
