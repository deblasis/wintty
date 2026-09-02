#requires -Version 7
<#
    The scenery driver: launch lib/BackdropStage under the window a harness is
    measuring, paint a named scene on it, and put the same scene on the
    desktop wallpaper.

    Why both. crystal is DWM transparency and shows the window behind;
    frosted is acrylic and blurs the window behind; solid is Mica, which
    samples the DESKTOP WALLPAPER and ignores windows entirely. A stage alone
    measures two materials against a chosen backdrop and the third against
    whatever wallpaper the developer has. So a scene is one PNG applied
    twice: painted on the stage, and set as the wallpaper.

    Scenes are generated, not shipped: each is reproducible from its name,
    size and seed, and "photo" is procedural rather than a file to carry.

    Dot-source it:

        . (Join-Path $PSScriptRoot 'lib/backdrop-stage.ps1')
        $stage = Start-BackdropStage -X 0 -Y 0 -W 1600 -H 1100
        $png = Set-BackdropScene -Stage $stage -Scene (Get-BackdropScenes)['photo'] -SceneDir $dir -Wallpaper
        Stop-BackdropStage $stage

    The wallpaper is machine state. A caller that passes -Wallpaper owns
    putting it back: `$before = Get-DesktopWallpaper` first and
    `Set-DesktopWallpaper -Snapshot $before` in a finally, which reads the
    registry back and throws if it disagrees. lib/env-guard.ps1's snapshot
    covers the registry side for `just env-restore` after a crash, but a
    registry restore does not repaint the desktop: after a crashed
    -Wallpaper run, re-run Set-DesktopWallpaper (or log off) to see the old
    wallpaper again. Only a single static image is captured; a slideshow,
    Spotlight or per-monitor wallpaper comes back as its current image.
#>

Add-Type -AssemblyName System.Drawing

$script:BackdropStageRoot = Join-Path $PSScriptRoot 'BackdropStage'
$script:BackdropStageExe = Join-Path $script:BackdropStageRoot `
    'bin\Release\net10.0-windows10.0.19041.0\win-x64\BackdropStage.exe'

# Build on demand, once. Same arrangement as WindowCapture: the tool is not
# in Ghostty.sln, so the harness that needs it is the honest owner of the
# build.
function Assert-BackdropStageReady {
    param([switch]$Force)
    if (-not $Force -and (Test-Path -LiteralPath $script:BackdropStageExe)) {
        return $script:BackdropStageExe
    }
    Write-Host 'backdrop-stage: building BackdropStage.exe (first use)'
    $log = & dotnet build (Join-Path $script:BackdropStageRoot 'BackdropStage.csproj') `
        -c Release 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $script:BackdropStageExe)) {
        $log | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
        throw 'HARNESS: BackdropStage.exe could not be built'
    }
    return $script:BackdropStageExe
}

if (-not ('StageWin' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StageWin {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
    // Whose pixels are at a point: the proof a caller needs that a pixel it
    // is about to read belongs to the stage and not to whatever is on top.
    public static uint PidAt(int x, int y) {
        uint pid; GetWindowThreadProcessId(WindowFromPoint(new POINT { X = x, Y = y }), out pid); return pid;
    }
}
'@
}

# The whole virtual screen in device pixels (SM_XVIRTUALSCREEN and friends),
# for a caller that wants a stage overhanging every monitor at once.
function Get-VirtualScreenRect {
    return @{
        X = [StageWin]::GetSystemMetrics(76); Y = [StageWin]::GetSystemMetrics(77)
        W = [StageWin]::GetSystemMetrics(78); H = [StageWin]::GetSystemMetrics(79)
    }
}

# 128 bits of hex, the shape the stage accepts. Its own function rather than
# seam-client's New-SeamToken so a caller that only wants scenery does not
# have to load the seam client.
function New-BackdropStageToken {
    $bytes = [byte[]]::new(16)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [System.Convert]::ToHexString($bytes).ToLowerInvariant()
}

# Launch the stage at a device-pixel rect and connect its pipe. Returns the
# session every other function here takes. A launch that gets as far as a
# window and no further is killed here, not left for the operator: the
# stage's window has no taskbar entry and cannot be focused for Alt+F4.
function Start-BackdropStage {
    param(
        [Parameter(Mandatory)][int]$X,
        [Parameter(Mandatory)][int]$Y,
        [Parameter(Mandatory)][int]$W,
        [Parameter(Mandatory)][int]$H,
        [int]$TimeoutSeconds = 30
    )
    $exe = Assert-BackdropStageReady
    $token = New-BackdropStageToken

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    # --parent: the stage closes itself when this shell dies, so a Ctrl+C or
    # a throw before the caller's own cleanup cannot orphan it.
    foreach ($a in @('--x', $X, '--y', $Y, '--w', $W, '--h', $H, '--parent', $PID)) { $psi.ArgumentList.Add([string]$a) }
    # The token goes to the child alone. Setting it on this process would
    # hand it to every process launched afterwards, the app under test
    # included.
    $psi.Environment['WINTTY_BACKDROP_STAGE'] = $token
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)

    try {
        # READY <hwnd> <pid> is printed after the window is shown; a process
        # that exits first has printed why on stderr.
        $ready = $proc.StandardOutput.ReadLine()
        if ($ready -notlike 'READY *') {
            $err = $proc.StandardError.ReadToEnd()
            throw "HARNESS: the backdrop stage did not come up: '$ready' $err"
        }
        $parts = $ready.Split(' ')
        $session = @{ Proc = $proc; Token = $token; Hwnd64 = [int64]$parts[1] }
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
            '.', "wintty-backdrop-stage-$token",
            [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::CurrentUserOnly)
        $pipe.Connect($TimeoutSeconds * 1000)
        $session.Pipe = $pipe
        $session.Reader = [System.IO.StreamReader]::new($pipe)
        $session.Writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false))
        $session.Writer.AutoFlush = $true
        $session.Writer.NewLine = "`n"
        return $session
    }
    catch {
        try { if (-not $proc.HasExited) { $proc.Kill($true) } } catch { }
        throw
    }
}

# One request, one response. A refusal from the stage is a HARNESS failure,
# never a product finding: the stage is the instrument, not the subject.
function Invoke-BackdropStage {
    param([Parameter(Mandatory)]$Stage, [Parameter(Mandatory)][hashtable]$Command)
    if ($Stage.Proc.HasExited) {
        throw ("HARNESS: the backdrop stage exited (code {0}) before '{1}'" -f
            $Stage.Proc.ExitCode, $Command['op'])
    }
    $Stage.Writer.WriteLine(($Command | ConvertTo-Json -Compress -Depth 4))
    $line = $Stage.Reader.ReadLine()
    if ($null -eq $line) { throw ("HARNESS: the backdrop stage closed the pipe during '{0}'" -f $Command['op']) }
    $response = $line | ConvertFrom-Json
    if (-not $response.ok) { throw ("HARNESS: backdrop stage {0} -> {1}" -f $Command['op'], $response.error) }
    return $response
}

function Stop-BackdropStage {
    param([Parameter(Mandatory)]$Stage)
    try { if (-not $Stage.Proc.HasExited) { [void](Invoke-BackdropStage $Stage @{ op = 'quit' }) } } catch { }
    foreach ($k in 'Writer', 'Reader', 'Pipe') { if ($Stage[$k]) { try { $Stage[$k].Dispose() } catch { } } }
    if (-not $Stage.Proc.WaitForExit(5000)) { try { $Stage.Proc.Kill($true) } catch { } }
}

# ---- scenes ----------------------------------------------------------------

# The catalogue, in the order a matrix runs them. Each is what a user might
# plausibly have behind a terminal, and the set spans the cases that break
# translucent chrome differently: flat extremes, a mid grey that sits near
# every "chrome" tone, a saturated brand colour, a soft gradient, a busy
# photograph, a light editor full of text, and a high-frequency pattern that
# acrylic blurs into grey and crystal shows raw.
function Get-BackdropScenes {
    return [ordered]@{
        black   = [pscustomobject]@{ Name = 'black';   Kind = 'solid'; Mode = 'stretch'; Color = '#000000'; What = 'flat black' }
        white   = [pscustomobject]@{ Name = 'white';   Kind = 'solid'; Mode = 'stretch'; Color = '#FFFFFF'; What = 'flat white' }
        grey    = [pscustomobject]@{ Name = 'grey';    Kind = 'solid'; Mode = 'stretch'; Color = '#808080'; What = 'flat mid grey, the tone chrome most often lands near' }
        brand   = [pscustomobject]@{ Name = 'brand';   Kind = 'solid'; Mode = 'stretch'; Color = '#0067C0'; What = 'a saturated accent blue, the default Windows accent' }
        sunrise = [pscustomobject]@{ Name = 'sunrise'; Kind = 'image'; Mode = 'stretch'; Color = $null; What = 'a soft vertical gradient, night navy to pale gold' }
        photo   = [pscustomobject]@{ Name = 'photo';   Kind = 'image'; Mode = 'stretch'; Color = $null; What = 'a procedural busy photograph: overlapping colour, lines, small high-contrast marks' }
        editor  = [pscustomobject]@{ Name = 'editor';  Kind = 'image'; Mode = 'stretch'; Color = $null; What = 'a light code editor: white ground, grey gutter, coloured tokens' }
        # Tiled, not stretched: a stretch resamples the squares and turns the
        # boundaries grey, which is the acrylic blur the scene exists to be
        # measured against, manufactured by the instrument instead.
        checker = [pscustomobject]@{ Name = 'checker'; Kind = 'image'; Mode = 'tile';    Color = $null; What = 'a black and white checkerboard of 4px squares: acrylic blurs it to grey, crystal shows it raw' }
    }
}

function ConvertTo-DrawingColor([string]$Hex) {
    return [System.Drawing.ColorTranslator]::FromHtml($Hex)
}

# Render one scene to a PNG. Deterministic for a (name, size, seed), so a
# matrix row can be re-run against the same backdrop it was measured on.
function New-BackdropSceneImage {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path,
        [int]$Width = 1920,
        [int]$Height = 1080,
        [int]$Seed = 1337
    )
    $scenes = Get-BackdropScenes
    if (-not $scenes.Contains($Name)) { throw "HARNESS: unknown scene '$Name'" }
    $scene = $scenes[$Name]
    $bmp = [System.Drawing.Bitmap]::new($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        switch ($Name) {
            { $scene.Kind -eq 'solid' } {
                $g.Clear((ConvertTo-DrawingColor $scene.Color))
            }
            'sunrise' {
                # Early-sunrise colours, dark sky at the top and a pale gold
                # horizon at the bottom.
                $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                    [System.Drawing.Point]::new(0, 0), [System.Drawing.Point]::new(0, $Height),
                    [System.Drawing.Color]::Black, [System.Drawing.Color]::White)
                $blend = [System.Drawing.Drawing2D.ColorBlend]::new(4)
                $blend.Colors = @(
                    (ConvertTo-DrawingColor '#1C2541'), (ConvertTo-DrawingColor '#F28482'),
                    (ConvertTo-DrawingColor '#FFB385'), (ConvertTo-DrawingColor '#FFE8A3'))
                $blend.Positions = [single[]]@(0.0, 0.45, 0.75, 1.0)
                $brush.InterpolationColors = $blend
                $g.FillRectangle($brush, 0, 0, $Width, $Height)
                $brush.Dispose()
            }
            'photo' {
                $rng = [System.Random]::new($Seed)
                $g.Clear((ConvertTo-DrawingColor '#5B6B4F'))
                for ($i = 0; $i -lt 220; $i++) {
                    $c = [System.Drawing.Color]::FromArgb(150, $rng.Next(256), $rng.Next(256), $rng.Next(256))
                    $b = [System.Drawing.SolidBrush]::new($c)
                    $w = $rng.Next(40, 520); $h = $rng.Next(40, 520)
                    $g.FillEllipse($b, $rng.Next(-200, $Width), $rng.Next(-200, $Height), $w, $h)
                    $b.Dispose()
                }
                for ($i = 0; $i -lt 120; $i++) {
                    $c = [System.Drawing.Color]::FromArgb(220, $rng.Next(256), $rng.Next(256), $rng.Next(256))
                    $p = [System.Drawing.Pen]::new($c, $rng.Next(1, 6))
                    $g.DrawLine($p, $rng.Next($Width), $rng.Next($Height), $rng.Next($Width), $rng.Next($Height))
                    $p.Dispose()
                }
                # Small marks in the two extremes, the detail a blur has to
                # average away and a transparent frame shows as noise.
                foreach ($c in @([System.Drawing.Color]::White, [System.Drawing.Color]::Black)) {
                    $b = [System.Drawing.SolidBrush]::new($c)
                    for ($i = 0; $i -lt 400; $i++) {
                        $g.FillRectangle($b, $rng.Next($Width), $rng.Next($Height), $rng.Next(2, 9), $rng.Next(2, 9))
                    }
                    $b.Dispose()
                }
            }
            'editor' {
                $rng = [System.Random]::new($Seed)
                $g.Clear([System.Drawing.Color]::White)
                $gutter = [int]($Width * 0.025)
                $g.FillRectangle([System.Drawing.SolidBrush]::new((ConvertTo-DrawingColor '#F3F3F3')), 0, 0, $gutter, $Height)
                $g.FillRectangle([System.Drawing.SolidBrush]::new((ConvertTo-DrawingColor '#F8F8F8')), $Width - [int]($Width * 0.06), 0, [int]($Width * 0.06), $Height)
                $font = [System.Drawing.Font]::new('Consolas', [single]($Height / 60.0), [System.Drawing.GraphicsUnit]::Pixel)
                $tokens = @(
                    @('#0000FF', 'const'), @('#0000FF', 'return'), @('#0000FF', 'if'), @('#0000FF', 'fn'),
                    @('#A31515', '"text"'), @('#A31515', "'c'"), @('#008000', '// note'),
                    @('#1F1F1F', 'value'), @('#1F1F1F', 'buffer.len'), @('#1F1F1F', '= 0;'), @('#1F1F1F', '(x, y)'),
                    @('#795E26', 'render'), @('#267F99', 'Widget'), @('#098658', '4096'))
                $lineH = [single]($Height / 40.0)
                $y = [single]($lineH * 0.5)
                $line = 1
                while ($y -lt $Height) {
                    $x = [single]($gutter + 12)
                    $indent = $rng.Next(0, 4)
                    $x += $indent * $lineH * 2
                    $numBrush = [System.Drawing.SolidBrush]::new((ConvertTo-DrawingColor '#9A9A9A'))
                    $g.DrawString([string]$line, $font, $numBrush, [single]4, $y)
                    $numBrush.Dispose()
                    $n = $rng.Next(2, 8)
                    for ($i = 0; $i -lt $n; $i++) {
                        $t = $tokens[$rng.Next($tokens.Count)]
                        $b = [System.Drawing.SolidBrush]::new((ConvertTo-DrawingColor $t[0]))
                        $g.DrawString($t[1], $font, $b, $x, $y)
                        $x += $g.MeasureString($t[1] + ' ', $font).Width
                        $b.Dispose()
                    }
                    $y += $lineH
                    $line++
                }
                $font.Dispose()
            }
            'checker' {
                # HatchStyle.LargeCheckerBoard: 4px squares, 8px period, in the
                # image's own pixels. Applied tiled so those are device pixels
                # whatever the stage's size. No anti-aliasing: with it the
                # image's outer ring is half-alpha, and tiled that becomes a
                # grey seam at every tile boundary, the very grey the scene
                # must not contain.
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
                $brush = [System.Drawing.Drawing2D.HatchBrush]::new(
                    [System.Drawing.Drawing2D.HatchStyle]::LargeCheckerBoard,
                    [System.Drawing.Color]::Black, [System.Drawing.Color]::White)
                $g.FillRectangle($brush, 0, 0, $Width, $Height)
                $brush.Dispose()
            }
        }
    }
    finally { $g.Dispose() }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $Path
}

# Apply a scene to the stage, and optionally to the wallpaper. Generates the
# PNG on first use under $SceneDir, keyed by name, size and seed so a run
# with a different seed never reads the previous run's picture. Returns the
# PNG path either way, so the report can point at what was behind the window.
function Set-BackdropScene {
    param(
        [Parameter(Mandatory)]$Stage,
        [Parameter(Mandatory)]$Scene,
        [Parameter(Mandatory)][string]$SceneDir,
        [switch]$Wallpaper,
        [int]$Seed = 1337,
        [int]$Width = 1920,
        [int]$Height = 1080
    )
    # Absolute, because the path is handed to two other processes (the stage
    # and Explorer) that resolve relative paths against their own cwd.
    $SceneDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SceneDir)
    $png = Join-Path $SceneDir ('{0}-{1}x{2}-{3}.png' -f $Scene.Name, $Width, $Height, $Seed)
    if (-not (Test-Path -LiteralPath $png)) {
        [void](New-BackdropSceneImage -Name $Scene.Name -Path $png -Width $Width -Height $Height -Seed $Seed)
    }
    if ($Scene.Kind -eq 'solid') {
        [void](Invoke-BackdropStage $Stage @{ op = 'solid'; color = $Scene.Color })
    } else {
        [void](Invoke-BackdropStage $Stage @{ op = 'image'; path = $png; mode = $Scene.Mode })
    }
    if ($Wallpaper) { Set-DesktopWallpaper -Path $png }
    return $png
}

# ---- wallpaper ---------------------------------------------------------------

$script:BackdropDesktopKey = 'HKCU:\Control Panel\Desktop'
$script:SPI_SETDESKWALLPAPER = 0x0014
$script:SPIF_UPDATEINIFILE_SENDCHANGE = 0x0003

function Get-DesktopWallpaper {
    $item = Get-ItemProperty -LiteralPath $script:BackdropDesktopKey
    return @{
        Path  = [string]$item.WallPaper
        Style = [string]$item.WallpaperStyle
        Tile  = [string]$item.TileWallpaper
    }
}

# Set a wallpaper, or put one back from a Get-DesktopWallpaper snapshot with
# -Snapshot, which carries the user's own style and tiling. Style 10 is
# "Fill". An empty path clears the wallpaper to the desktop colour, which is
# what a restore needs when the snapshot had none. The registry is read back
# afterwards and a disagreement throws: a restore that "probably worked" is
# what the env guard's incidents had.
function Set-DesktopWallpaper {
    param(
        [Parameter(ParameterSetName = 'Path', Mandatory)][AllowEmptyString()][string]$Path,
        [Parameter(ParameterSetName = 'Path')][string]$Style = '10',
        [Parameter(ParameterSetName = 'Path')][string]$Tile = '0',
        [Parameter(ParameterSetName = 'Snapshot', Mandatory)][hashtable]$Snapshot
    )
    if ($PSCmdlet.ParameterSetName -eq 'Snapshot') { $Path = $Snapshot.Path; $Style = $Snapshot.Style; $Tile = $Snapshot.Tile }
    if ($Path -and -not (Test-Path -LiteralPath $Path)) { throw "HARNESS: wallpaper file not found: $Path" }
    Set-ItemProperty -LiteralPath $script:BackdropDesktopKey -Name 'WallpaperStyle' -Value $Style
    Set-ItemProperty -LiteralPath $script:BackdropDesktopKey -Name 'TileWallpaper' -Value $Tile
    $ok = [StageWin]::SystemParametersInfo(
        $script:SPI_SETDESKWALLPAPER, 0, $Path, $script:SPIF_UPDATEINIFILE_SENDCHANGE)
    if (-not $ok) {
        throw ("HARNESS: SPI_SETDESKWALLPAPER failed (Win32 {0})" -f [System.Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    $now = Get-DesktopWallpaper
    if ($now.Path -ne $Path -or $now.Style -ne $Style -or $now.Tile -ne $Tile) {
        throw ("HARNESS: the wallpaper did not read back: asked '{0}' style {1} tile {2}, registry says '{3}' style {4} tile {5}" -f
            $Path, $Style, $Tile, $now.Path, $now.Style, $now.Tile)
    }
}

# ---- one pixel -------------------------------------------------------------

# Read one screen pixel in device coordinates. The stage's own margin, read
# through this, is how a caller proves the scene it asked for is the scene
# on screen before it photographs anything; pair it with Get-WindowPidAt so
# the pixel is known to be the stage's and not a window over it.
function Get-ScreenPixel {
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y)
    $bmp = [System.Drawing.Bitmap]::new(1, 1)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($X, $Y, 0, 0, $bmp.Size)
        $c = $bmp.GetPixel(0, 0)
        return @{ R = [int]$c.R; G = [int]$c.G; B = [int]$c.B; Hex = ('#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B) }
    }
    finally { $g.Dispose(); $bmp.Dispose() }
}

function Get-WindowPidAt {
    param([Parameter(Mandatory)][int]$X, [Parameter(Mandatory)][int]$Y)
    return [StageWin]::PidAt($X, $Y)
}

function Test-PixelNear {
    param([Parameter(Mandatory)]$Pixel, [Parameter(Mandatory)][string]$Hex, [int]$Tolerance = 3)
    $c = ConvertTo-DrawingColor $Hex
    return ([Math]::Abs($Pixel.R - $c.R) -le $Tolerance) -and
           ([Math]::Abs($Pixel.G - $c.G) -le $Tolerance) -and
           ([Math]::Abs($Pixel.B - $c.B) -le $Tolerance)
}
