#requires -Version 7
# frame-style, over the combinations that decide what the chrome is made of.
#
# Two keys meet on one surface. window-theme picks the chrome's HUE - the
# desktop's or the terminal palette's - and frame-style picks its MATERIAL:
# solid, frosted or crystal, inheriting background-style when unset. They are
# orthogonal in the config and not in the paint, which is where the defects
# live: pinning the palette off made frame-style unobservable under
# window-theme=wintty, and passing the strips a fill the title row did not get
# left one layout opaque and the other not for a single config.
#
# What it checks, and both layers can fail:
#
#   contrast     the title row's ink against the title row's own fill, and the
#                tab strip's text against its own, at WCAG 4.5:1. Ported from
#                ThemeResolution.RelativeLuminance / ContrastRatio, including
#                the 0.03928 linearisation. That file also carries a cheaper
#                BT.709 "is this dark" estimate; scoring with that one gives
#                plausible numbers that disagree with what the product decided
#                its own ink from, so it is deliberately not used here.
#
#   material     solid must not paint the same chrome as frosted for one
#                config. Compared with the same channel delta the tab-close
#                harness uses, relatively: the harness never learns what colour
#                a frosted frame is meant to be, only that it is not the solid
#                one.
#
# Three product behaviours are known and intended, and are NOT asserted the
# other way round:
#
#   - frosted and crystal are the same frame. There is one SystemBackdrop per
#     window and a translucent frame can only cover it or reveal it, so both
#     mean "reveal". Their delta is measured and reported, never asserted.
#   - a translucent frame over a solid background degrades to solid: nothing is
#     behind it to come through. That one IS asserted, as an equality.
#   - High Contrast pins the frame solid, so solid and frosted agree there.
#     Detected and reported; the material layer stands down rather than
#     reporting the pin as a defect.
#
# Anti-vacuity: the controls run FIRST, before any case is judged, and all but
# the first against a real capture. A run whose detector is broken passes
# everything by finding nothing, which is the failure this suite exists not to
# have.
#
#   ground       this harness's copy of BackdropGround.Estimate answers what the
#                one in the build under test answers, over both desktop poles,
#                every backdrop style and every tint opacity the config reaches
#   stability    two captures of one unchanged window differ in no chrome pixel
#   comparator   the selected tab's fill differs from an unselected tab's by
#                more than the delta the material layer asserts on
#   ink-seen     the title text region yields an ink/fill luminance gap far
#                larger than an ink-free strip of the same chrome does
#   ink-invented that ink-free strip scores below the contrast floor
#
# Two measurement constraints, both learned the hard way and both able to
# manufacture findings on a correct build:
#
#   The window BORDER BAND is excluded from every comparison. DWM composites
#   the rounded corner and the shadow against whatever is on the desktop
#   behind the window, so two captures of an identical config differed in
#   ~2400 pixels, all of them in the border, with an interior diff of exactly
#   zero. Every region is clipped to the interior before anything reads it.
#
#   Translucent chrome samples the desktop, so it gets a long settle. A capture
#   taken seconds after a desktop light/dark change catches a compositor
#   mid-transition and reports colours the app never chose. Opaque chrome is
#   unaffected, which is exactly why a short settle looks fine until the case
#   that is not opaque.
#
# A translucent frame is a composite, and the third constraint is that the
# composite can legitimately be the opaque frame's own shade. The palette tints
# it and the luminosity blend pulls it back to the system base for the active
# desktop polarity, so a palette sitting near that base - a dark one on a dark
# desktop, a near-white one on a light desktop - makes solid and frosted agree
# on a build that wired frame-style through perfectly. A failed material
# comparison is therefore judged against what the product MEANS the composite
# to be, estimated with BackdropGround.Estimate's own arithmetic off the
# palette the case actually loaded, and a comparison where nothing was meant to
# move leaves with 1 rather than being filed as a defect. The raw screen behind
# the window is reported and decides nothing: it is one input to the acrylic
# and the weakest, and turning on it is what filed a working build as broken.
#
# This harness READS the desktop polarity and High Contrast and never sets
# either. It runs unattended as part of the suite, and a harness that flips the
# desktop theme out from under whoever is at the keyboard is not one.
#
# The vertical layout is staged, because it is the only one with a window title
# row AND a tab strip as separate surfaces; the horizontal strip is not
# sampled.
#
# THEME AXIS (#792). Each case stages a `theme` alongside `window-theme` and
# `frame-style`, and the spanning set runs twice: once per half of the built-in
# pair, staged as real theme files under the same root the config lives in.
# A name is the only thing both halves of the app can resolve - libghostty
# loads `theme = <name>` from the XDG themes directory (theme.zig) and the C#
# shell resolves the same name against the config root's themes directories
# (ThemeSearchPath) - so the palette that fills the terminal also frames it.
# The catalogue is enumerated under that same staging, which makes it the
# catalogue the launched processes can actually reach rather than the user's
# own; a name the staging cannot resolve was exactly how the theme axis used
# to be silently inert.
#
# The launches pass `--config-file=<staged path>` (#787) so the config reaches
# libghostty by name as well as by discovery. The XDG_CONFIG_HOME override
# STAYS, and not out of sentiment: the Windows shell - the half that owns
# window-theme, frame-style and the vertical layout - reads every file-derived
# key through ghostty_config_open_path, which is computed from the XDG default
# path and does not know `--config-file` exists. Drop the override and the two
# halves of one window read two different configs.
#
# Seeded: -Seed replays the case order and every randomized draw.
#
# Exit codes: 0 clean, 2 findings in the build under test, 1 could not run.
param(
    [Parameter(Mandatory)][string]$ExePath,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Seed = 0,
    # Extra cases beyond the spanning set, drawn from the seeded RNG: a theme
    # from the staged catalogue and the three keys from their value sets. They
    # get the contrast layer and liveness only - the material layer needs a
    # matched pair of configs, which a random draw does not give.
    [int]$Random = 0
)
. (Join-Path $PSScriptRoot 'lib/wintty-process.ps1')
$ErrorActionPreference = 'Stop'

# Same convention as the other harnesses here: a PRODUCT_FAIL throw is a defect
# in the build and has to leave with 2, and anything else is a run that judged
# nothing and leaves with 1 for the runner to retry. `break` rethrows the rest
# so a genuine harness failure keeps its 1.
trap {
    if ("$_" -like 'PRODUCT_FAIL*') {
        Write-Host "$_" -ForegroundColor Red
        exit 2
    }
    break
}

New-Item -ItemType Directory -Force -Path $OutDir, (Join-Path $OutDir 'shots') | Out-Null

# Resolved once, up front. The suite and the recipe both pass a path relative
# to the repo root, and every launch below either hands it to a child process
# with its own working directory or to Stop-WinttyStartedAfter, which compares
# it against a running process's fully qualified image path - a relative
# spelling matches nothing there, so the sweep would silently skip the windows
# this run opened.
if (-not (Test-Path -LiteralPath $ExePath)) { throw "HARVEST_MISS: missing exe: $ExePath" }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path

if ($Seed -eq 0) { $Seed = Get-Random -Minimum 1 -Maximum 999999 }
$rng = [System.Random]::new($Seed)
Write-Host "seed=$Seed"

# The band DWM owns. Everything inside it is composited against the desktop
# behind the window - the rounded corner, the shadow, the resize grip - so it
# belongs to whatever is on screen rather than to the build under test. The top
# is thinner than the sides because there is no shadow above an active window,
# and the title row starts immediately under it.
$BorderInsetX = 12
$BorderInsetTop = 8
$BorderInsetBottom = 12

# Two samples this far apart in any one channel are different fills; anything
# closer is the same fill. The same single boundary the tab-close harness uses,
# so no measurement can land in a band where the harness has no answer.
$ChannelDelta = 20

# WCAG AA for body text. The product's own resolver scores its ink against this.
$ContrastFloor = 4.5

# Colours are bucketed before the ink is looked for. Subpixel antialiasing
# smears a glyph edge across dozens of near-identical colours, so an unbucketed
# histogram has no mode worth the name and the rarest colour in it is always a
# fringe pixel rather than the ink. Five bits a channel merges the fringes and
# still separates ink from fill by a wide margin.
$InkQuantShift = 3

# How much of a region a colour has to cover before it can be called its ink. A
# single outlying pixel is a compression artifact or an icon corner; a glyph
# stroke is not. Expressed as a floor and a fraction because the two regions
# sampled differ in size by an order of magnitude.
function Get-InkFloor([int]$PixelCount) { return [Math]::Max(6, [int]($PixelCount / 400)) }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class FsRegionStat {
    public int Count;
    public int MeanR, MeanG, MeanB;
    public int FillR, FillG, FillB, FillCount;
    public int InkR, InkG, InkB, InkCount;
    public double Contrast;
    public double LumGap;
}

public static class FSz {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential)] public struct HIGHCONTRAST { public uint cbSize; public uint dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")] static extern bool SystemParametersInfo(uint a, uint p, ref HIGHCONTRAST v, uint w);
    public delegate bool EnumProc(IntPtr h, IntPtr lp);

    public class WinRect { public int L,T,R,B; public int W { get { return R-L; } } public int Hh { get { return B-T; } } }
    public static IntPtr P(long hwnd) { return new IntPtr(hwnd); }
    public static WinRect RectOf(long hwnd) {
        var h = P(hwnd); RECT r;
        if (!IsWindow(h) || !GetWindowRect(h, out r)) return null;
        var wr = new WinRect { L=r.L,T=r.T,R=r.R,B=r.B };
        return (wr.W < 80 || wr.Hh < 80) ? null : wr;
    }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }

    // The same query HighContrastDetector.IsActive() makes, so this reads the
    // state the product itself branches on rather than a registry key that can
    // disagree with it.
    public static bool HighContrastOn() {
        var hc = new HIGHCONTRAST();
        hc.cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST));
        if (!SystemParametersInfo(0x0042, hc.cbSize, ref hc, 0)) return false;
        return (hc.dwFlags & 0x00000001) != 0;
    }

    // Ported from Ghostty.Core.Windows.ThemeResolution.RelativeLuminance, and
    // deliberately not from IsBackgroundDark in the same file: that one is a
    // BT.709 estimate on raw sRGB with no linearisation, and it disagrees with
    // this by enough to score a readable row as unreadable and the reverse.
    static double Linearize(int channel) {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
    public static double RelativeLuminance(int r, int g, int b) {
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }
    // Ported from ThemeResolution.ContrastRatio. Order-independent, 1.0 to 21.0.
    public static double ContrastRatio(int ar, int ag, int ab, int br, int bg, int bb) {
        double la = RelativeLuminance(ar, ag, ab);
        double lb = RelativeLuminance(br, bg, bb);
        double hi = Math.Max(la, lb);
        double lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    // The fill of a region is the colour most of it is; its ink is whatever
    // sits farthest from that fill in luminance while still covering enough of
    // the region to be a glyph rather than a fringe. Both come back as the mean
    // of their bucket, so the returned colour is a real colour off the screen
    // rather than a bucket centre.
    //
    // A region with no ink in it answers ink == fill, which is a contrast of
    // 1.0. That is the answer the ink-invented control asserts on, so it has to
    // be reachable rather than defended against.
    public static FsRegionStat Measure(int[] px, int strideInts, int x0, int y0, int x1, int y1, int shift, int floorPixels) {
        int levels = 256 >> shift;
        int buckets = levels * levels * levels;
        int[] count = new int[buckets];
        long[] sr = new long[buckets];
        long[] sg = new long[buckets];
        long[] sb = new long[buckets];
        long tr = 0, tg = 0, tb = 0;
        int n = 0;
        for (int y = y0; y < y1; y++) {
            int row = y * strideInts;
            for (int x = x0; x < x1; x++) {
                int v = px[row + x];
                int r = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, b = v & 0xFF;
                tr += r; tg += g; tb += b; n++;
                int key = (((r >> shift) * levels) + (g >> shift)) * levels + (b >> shift);
                count[key]++; sr[key] += r; sg[key] += g; sb[key] += b;
            }
        }
        var st = new FsRegionStat();
        st.Count = n;
        if (n == 0) return st;
        st.MeanR = (int)(tr / n); st.MeanG = (int)(tg / n); st.MeanB = (int)(tb / n);

        int fill = 0;
        for (int i = 1; i < buckets; i++) if (count[i] > count[fill]) fill = i;
        st.FillCount = count[fill];
        st.FillR = (int)(sr[fill] / count[fill]);
        st.FillG = (int)(sg[fill] / count[fill]);
        st.FillB = (int)(sb[fill] / count[fill]);

        double lumFill = RelativeLuminance(st.FillR, st.FillG, st.FillB);
        int ink = fill; double best = -1.0;
        for (int i = 0; i < buckets; i++) {
            // The empty-bucket test is not implied by the floor: a floor of
            // zero admits every bucket the region never painted, and the mean
            // below divides by the count.
            if (i == fill || count[i] == 0 || count[i] < floorPixels) continue;
            int r = (int)(sr[i] / count[i]), g = (int)(sg[i] / count[i]), b = (int)(sb[i] / count[i]);
            double d = Math.Abs(RelativeLuminance(r, g, b) - lumFill);
            if (d > best) { best = d; ink = i; }
        }
        st.InkCount = count[ink];
        st.InkR = (int)(sr[ink] / count[ink]);
        st.InkG = (int)(sg[ink] / count[ink]);
        st.InkB = (int)(sb[ink] / count[ink]);
        st.LumGap = Math.Abs(RelativeLuminance(st.InkR, st.InkG, st.InkB) - lumFill);
        st.Contrast = ContrastRatio(st.InkR, st.InkG, st.InkB, st.FillR, st.FillG, st.FillB);
        return st;
    }

    // Pixels differing by at least delta in any channel. Both captures have to
    // be the same size and are read over the same rect, so this says nothing
    // about a window that moved - which is why the material layer compares
    // region means across launches and keeps this for one window that did not.
    public static int DiffCount(int[] a, int[] b, int strideInts, int x0, int y0, int x1, int y1, int delta) {
        int n = 0;
        for (int y = y0; y < y1; y++) {
            int row = y * strideInts;
            for (int x = x0; x < x1; x++) {
                int va = a[row + x], vb = b[row + x];
                if (va == vb) continue;
                int dr = Math.Abs(((va >> 16) & 0xFF) - ((vb >> 16) & 0xFF));
                int dg = Math.Abs(((va >> 8) & 0xFF) - ((vb >> 8) & 0xFF));
                int db = Math.Abs((va & 0xFF) - (vb & 0xFF));
                if (Math.Max(dr, Math.Max(dg, db)) >= delta) n++;
            }
        }
        return n;
    }
}
'@

$UIA = [System.Windows.Automation.AutomationElement]
$TREE = [System.Windows.Automation.TreeScope]::Descendants
$CTRL = [System.Windows.Automation.ControlType]

# ---- window and UIA -------------------------------------------------------

function Get-WinUiWindows([uint32]$ProcId) {
    $hits = [System.Collections.Generic.List[object]]::new()
    $cb = [FSz+EnumProc]{
        param($h, $lp)
        [uint32]$o = 0; [void][FSz]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $ProcId -or -not [FSz]::IsWindowVisible($h)) { return $true }
        if ([FSz]::ClassOf($h) -ne 'WinUIDesktopWin32WindowClass') { return $true }
        $hwnd64 = $h.ToInt64()
        $rc = [FSz]::RectOf($hwnd64)
        if ($null -eq $rc) { return $true }
        $hits.Add([pscustomobject]@{ Hwnd64 = $hwnd64; Title = [FSz]::TitleOf($h); Area = ($rc.W * $rc.Hh) })
        return $true
    }
    [void][FSz]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits | Sort-Object Area -Descending
}

function Test-SplashVisible([int]$ProcId) {
    $script:splashSeen = $false
    $cb = [FSz+EnumProc]{
        param($hwnd, $lp)
        [uint32]$owner = 0; [void][FSz]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -ne $ProcId) { return $true }
        if ([FSz]::ClassOf($hwnd) -eq 'WinttySplash' -and [FSz]::IsWindowVisible($hwnd)) { $script:splashSeen = $true }
        return $true
    }
    [void][FSz]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:splashSeen
}

function Wait-Ready($proc) {
    $dl = (Get-Date).AddSeconds(45)
    $got = $null
    while ((Get-Date) -lt $dl) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh(); if ($proc.HasExited) { throw "PRODUCT_FAIL startup exit=$($proc.ExitCode)" }
        $got = @(Get-WinUiWindows ([uint32]$proc.Id)) | Select-Object -First 1
        if ($got) { break }
    }
    if (-not $got) { throw 'HARVEST_MISS: no WinUI hwnd' }
    $dl = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $dl) {
        $proc.Refresh(); if ($proc.HasExited) { throw 'PRODUCT_FAIL exited during splash' }
        if (Test-SplashVisible $proc.Id) { Start-Sleep -Milliseconds 200; continue }
        Start-Sleep -Milliseconds 900
        if (-not (Test-SplashVisible $proc.Id)) { return $got }
    }
    throw 'HARVEST_MISS: splash never dropped'
}

function Get-UiaRoot([int64]$Hwnd64) {
    try { return $UIA::FromHandle([FSz]::P($Hwnd64)) } catch { return $null }
}

function Find-ById($root, [string]$Id) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $Id)
    return $root.FindFirst($TREE, $cond)
}

function Find-ByName($root, [string]$Name) {
    if ($null -eq $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $Name)
    return $root.FindFirst($TREE, $cond)
}

# ---- capture --------------------------------------------------------------

function Get-Shot([int64]$Hwnd64) {
    $rc = [FSz]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: degenerate window rect' }
    $bmp = New-Object System.Drawing.Bitmap $rc.W, $rc.Hh, ([System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, $bmp.Size)
    $g.Dispose()
    $lock = $bmp.LockBits(
        (New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
    $strideInts = [int]($lock.Stride / 4)
    $px = New-Object int[] ($strideInts * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($lock)
    return [pscustomobject]@{
        Bmp = $bmp; Px = $px; StrideInts = $strideInts
        L = $rc.L; T = $rc.T; W = $bmp.Width; H = $bmp.Height
    }
}

function Save-Shot($shot, [string]$Name) {
    if ($null -eq $shot) { return }
    $shot.Bmp.Save((Join-Path $OutDir "shots\$Name.png"))
}

function Close-Shot($shot) {
    if ($null -ne $shot) { $shot.Bmp.Dispose() }
}

# Every region is clipped to this before anything reads it. See the header: the
# band outside it is DWM's, and comparing it reports the desktop.
function Get-Interior($shot) {
    return [pscustomobject]@{
        X0 = $BorderInsetX
        Y0 = $BorderInsetTop
        X1 = $shot.W - $BorderInsetX
        Y1 = $shot.H - $BorderInsetBottom
    }
}

function New-Region([string]$Name, [int]$X, [int]$Y, [int]$W, [int]$H) {
    return [pscustomobject]@{ Name = $Name; X = $X; Y = $Y; W = $W; H = $H }
}

function Get-ClippedBounds($shot, $Region) {
    $in = Get-Interior $shot
    $x0 = [Math]::Max([int]$Region.X, $in.X0)
    $y0 = [Math]::Max([int]$Region.Y, $in.Y0)
    $x1 = [Math]::Min([int]($Region.X + $Region.W), $in.X1)
    $y1 = [Math]::Min([int]($Region.Y + $Region.H), $in.Y1)
    if (($x1 - $x0) -lt 3 -or ($y1 - $y0) -lt 3) { return $null }
    return @($x0, $y0, $x1, $y1)
}

function Measure-Region($shot, $Region) {
    $b = Get-ClippedBounds $shot $Region
    if ($null -eq $b) {
        throw ("HARVEST_MISS: region '$($Region.Name)' has nothing left after the border band is " +
               "excluded (asked for $($Region.W)x$($Region.H) at $($Region.X),$($Region.Y) in a $($shot.W)x$($shot.H) window)")
    }
    $area = ($b[2] - $b[0]) * ($b[3] - $b[1])
    $st = [FSz]::Measure($shot.Px, $shot.StrideInts, $b[0], $b[1], $b[2], $b[3], $InkQuantShift, (Get-InkFloor $area))
    return [pscustomobject]@{
        Name     = $Region.Name
        Pixels   = $st.Count
        Mean     = [pscustomobject]@{ R = $st.MeanR; G = $st.MeanG; B = $st.MeanB }
        Fill     = [pscustomobject]@{ R = $st.FillR; G = $st.FillG; B = $st.FillB }
        FillCount = $st.FillCount
        Ink      = [pscustomobject]@{ R = $st.InkR;  G = $st.InkG;  B = $st.InkB  }
        InkCount = $st.InkCount
        Contrast = [Math]::Round($st.Contrast, 3)
        LumGap   = [Math]::Round($st.LumGap, 5)
    }
}

# The mean colour of a rectangle of SCREEN, with no window of ours over it.
# Used only to disambiguate a failed material comparison: see Compare-Material.
function Get-ScreenMeanAt([int]$X, [int]$Y, [int]$W, [int]$H) {
    if ($W -lt 2 -or $H -lt 2) { return $null }
    $bmp = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($X, $Y, 0, 0, $bmp.Size)
        $g.Dispose()
        $lock = $bmp.LockBits(
            (New-Object System.Drawing.Rectangle 0, 0, $W, $H),
            [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
        $strideInts = [int]($lock.Stride / 4)
        $px = New-Object int[] ($strideInts * $H)
        [System.Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $px, 0, $px.Length)
        $bmp.UnlockBits($lock)
        $st = [FSz]::Measure($px, $strideInts, 0, 0, $W, $H, $InkQuantShift, [Math]::Max(6, ($W * $H)))
        return [pscustomobject]@{ Mean = [pscustomobject]@{ R = $st.MeanR; G = $st.MeanG; B = $st.MeanB } }
    }
    finally { $bmp.Dispose() }
}

function New-Rgb([int]$R, [int]$G, [int]$B) { return [pscustomobject]@{ R = $R; G = $G; B = $B } }

function Get-ChannelDelta($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return -1 }
    $dr = [Math]::Abs($a.R - $b.R)
    $dg = [Math]::Abs($a.G - $b.G)
    $db = [Math]::Abs($a.B - $b.B)
    return [Math]::Max($dr, [Math]::Max($dg, $db))
}

function Get-MeanDelta($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return -1 }
    return Get-ChannelDelta $a.Mean $b.Mean
}

function Format-Rgb($c) { return ('{0},{1},{2}' -f $c.R, $c.G, $c.B) }

# ---- what a translucent frame will composite to ---------------------------
#
# A frosted frame does not paint a colour, it paints a COMPOSITE: the terminal
# palette laid over Fluent's base colour for the active desktop polarity, at
# the configured tint opacity. The material layer needs that number, because a
# composite that lands on the shade a solid frame already paints makes the two
# frames indistinguishable on a build that wired frame-style through perfectly.
#
# Mirrored from Ghostty.Core.Shell.BackdropGround.Estimate, which is the
# product's own model of the same surface, down to the resolvers it defers to:
# RootBackgroundResolver for the styles that are painted rather than
# composited, and AcrylicTintResolver for the default tint opacity. It has to
# be the product's arithmetic rather than a plausible one of this harness's
# own: the two quantities compared below sit a handful of counts apart in
# exactly the case that matters, so a model off by a little files defects the
# build does not have, or excuses ones it does. A control cross-checks this
# copy against the shipped Ghostty.Core.dll before any case is judged.

# Fluent's SolidBackgroundFillColorBase, the colour the luminosity blend pulls
# an acrylic surface back towards. BackdropGround.SystemBaseLight/Dark.
$SystemBaseLight = (New-Rgb 0xF3 0xF3 0xF3)
$SystemBaseDark = (New-Rgb 0x20 0x20 0x20)

# RootBackgroundResolver.OpaqueChromeArgb: what a painted root grid takes when
# the terminal palette is not driving it.
$OpaqueChromeLight = (New-Rgb 0xF3 0xF3 0xF3)
$OpaqueChromeDark = (New-Rgb 0x0C 0x0C 0x0C)

# AcrylicTintResolver.DefaultTintOpacity. Correct only while nothing staged
# here writes a key that moves it, which Write-CaseConfig refuses to do.
$DefaultTintOpacity = 0.3

# BackdropGround's own Mix. Truncation after adding a half, which is NOT what
# [Math]::Round does with this input - that one sends a .5 to the even
# neighbour and would disagree with the product on every second channel.
function Get-MixChannel([int]$Over, [int]$Under, [double]$Alpha) {
    return [int][Math]::Floor(($Alpha * $Over) + ((1.0 - $Alpha) * $Under) + 0.5)
}

function Get-BackdropGround($Palette, [bool]$OsDark, [string]$GroundStyle, [double]$TintOpacity) {
    if ($null -eq $Palette) { return $null }
    $base = if ($OsDark) { $SystemBaseDark } else { $SystemBaseLight }

    # Case-SENSITIVE, all the way down. Every comparison the product makes on a
    # backdrop style is ordinal against a value BackdropStyles.TryNormalize has
    # already lowercased, so a style that is not lowercase is not frosted there.
    # PowerShell's -eq is case-insensitive and answered that 'Frosted' was,
    # which is this copy drifting from the model it exists to reproduce - the
    # control below is what caught it.
    #
    # Nothing tints the chrome under crystal - no tint, no luminosity blend, no
    # Fluent base underneath - so there is no composite and the base stands
    # alone. Not a prediction of what is behind the window, which is why the
    # product does not claim crystal is estimated either.
    if ($GroundStyle -ceq 'crystal') { return $base }

    # RootBackgroundResolver leaves the root transparent for frosted and
    # crystal only. Every other style paints it, and a painted root IS the
    # ground rather than something to blend towards.
    if ($GroundStyle -cne 'frosted') {
        return $(if ($OsDark) { $OpaqueChromeDark } else { $OpaqueChromeLight })
    }

    $t = [Math]::Min(1.0, [Math]::Max(0.0, $TintOpacity))
    return (New-Rgb (Get-MixChannel $Palette.R $base.R $t) `
                    (Get-MixChannel $Palette.G $base.G $t) `
                    (Get-MixChannel $Palette.B $base.B $t))
}

# MainWindow.ChromeGroundStyle: frame-style can cover the backdrop, not replace
# it. A solid FRAME is its own ground; the other two leave whatever
# background-style put behind them showing. An unset frame-style inherits
# background-style, the same fold the config layer applies upstream.
#
# Not modelled: low power and background-opacity both flatten the effective
# backdrop to solid without touching the config. Neither is staged by this
# harness, and a run under low power would be estimating a surface the window
# is not drawn on.
function Get-ChromeGroundStyle($Case) {
    $frame = if ($Case.frameStyle) { $Case.frameStyle } else { $Case.background }
    if ($frame -eq 'solid') { return 'solid' }
    return $Case.background
}

# ---- the surfaces this harness samples ------------------------------------
#
# Derived from UIA rather than written down as pixel offsets: the title row's
# height and the strip's width both move with DPI and with the caption inset,
# and a hardcoded band would drift onto the terminal on one machine and onto
# the border on another - reporting the desktop, or the terminal's own
# background, as chrome.
#
# Four regions, and they are not interchangeable:
#
#   titleText   the window title TextBlock's own rect. Ink and fill both come
#               out of here, which is what makes the contrast score a claim
#               about a real pair rather than about two surfaces that happen to
#               be adjacent.
#   titleBare   the title row at the same height, left of the text and right of
#               the icon. No ink, by construction. It is the chrome sample the
#               material layer compares across configs, and the ink-invented
#               control's subject.
#   tabText     the label INSIDE the selected tab row, not the row. The row
#               rect also holds the tab icon and the close glyph, and either
#               would be picked up as the row's ink - scoring an icon's
#               contrast and calling it the tab title.
#   stripBare   the strip below the last tab row: strip chrome with no row on
#               it, which is the second material sample.
function Get-Surfaces([int64]$Hwnd64) {
    $rc = [FSz]::RectOf($Hwnd64)
    if ($null -eq $rc) { throw 'HARVEST_MISS: degenerate window rect' }
    $root = Get-UiaRoot $Hwnd64
    if ($null -eq $root) { throw 'HARVEST_MISS: no UIA root' }

    $titleEl = Find-ById $root 'VerticalTitleText'
    if ($null -eq $titleEl) {
        throw ('HARVEST_MISS: no VerticalTitleText under the window, so the title row was never ' +
               'staged; this harness writes vertical-tabs = true and reads the row it creates')
    }
    $tr = $titleEl.Current.BoundingRectangle
    if ($tr.Width -le 4 -or $tr.Height -le 4) {
        throw "HARVEST_MISS: the title text rect is $([int]$tr.Width)x$([int]$tr.Height), which nothing can be read off"
    }

    $navEl = Find-ById $root 'NavView'
    if ($null -eq $navEl) { throw 'HARVEST_MISS: no NavView, so the vertical strip is not up' }
    $nr = $navEl.Current.BoundingRectangle

    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CTRL::ListItem)
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($el in $navEl.FindAll($TREE, $cond)) {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 4 -or $r.Height -le 4) { continue }
        $sel = $null
        try {
            $pat = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sel = [bool]$pat.Current.IsSelected
        } catch { $sel = $null }
        $rows.Add([pscustomobject]@{ El = $el; Name = $el.Current.Name; Rect = $r; Selected = $sel })
    }
    if ($rows.Count -eq 0) { throw 'HARVEST_MISS: no tab rows in the vertical strip' }
    $rows = @($rows | Sort-Object { $_.Rect.Y })

    # Window-relative, because that is the frame every region and every clip is
    # expressed in. Converting once here rather than at each use is what keeps a
    # window that moved between launches from silently shifting a sample.
    function ToLocal($r) {
        return @([int]($r.X - $rc.L), [int]($r.Y - $rc.T), [int]$r.Width, [int]$r.Height)
    }

    $t = ToLocal $tr
    $n = ToLocal $nr

    $selected = @($rows | Where-Object { $_.Selected })
    $tabRow = if ($selected.Count -ge 1) { $selected[0] } else { $rows[0] }
    # The label, not the row. A row rect spans the icon and the close glyph as
    # well, and the extractor would take whichever of those sits farthest from
    # the fill as the row's ink - reporting an icon's contrast under the name of
    # the tab title.
    $labelCond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $CTRL::Text)
    $label = $tabRow.El.FindFirst($TREE, $labelCond)
    if ($null -eq $label) {
        throw ('HARVEST_MISS: the selected tab row carries no text element, so the strip is collapsed ' +
               'to icons and there is no tab-strip text to score')
    }
    $tb = ToLocal $label.Current.BoundingRectangle
    $lastRow = $rows[$rows.Count - 1]
    $lb = ToLocal $lastRow.Rect

    # The terminal body, which is painted straight from `background` - the same
    # value BackdropGround.Estimate takes as its palette. Read off the window
    # rather than out of a theme file because only the process knows what its
    # config actually resolved to: a theme name that does not resolve leaves the
    # palette somewhere the name does not predict, and an estimate built on the
    # name would then be an estimate of a different window.
    #
    # Centred, because the prompt sits top left and the status band along the
    # bottom, and the middle of an idle terminal is bare background. Deliberately
    # NOT one of Regions: the stability control asserts that no chrome pixel
    # moves between two captures, and the terminal is not chrome.
    $termX0 = $n[0] + $n[2] + 8
    $termY0 = $t[1] + $t[3] + 8
    $termX1 = $rc.W - 8
    $termY1 = $rc.Hh - 8
    $palette = $null
    if (($termX1 - $termX0) -ge 64 -and ($termY1 - $termY0) -ge 64) {
        $pw = [Math]::Min(160, [int](($termX1 - $termX0) / 2))
        $ph = [Math]::Min(120, [int](($termY1 - $termY0) / 2))
        $palette = New-Region 'terminalBody' `
            ([int](($termX0 + $termX1 - $pw) / 2)) ([int](($termY0 + $termY1 - $ph) / 2)) $pw $ph
    }

    # Left of the title text and right of the strip column, inset so neither the
    # icon badge nor the text's own antialiasing can reach it. Falls back to the
    # band right of the text when the layout leaves no room on the left, and
    # refuses rather than sampling a sliver.
    $bareW = $t[0] - ($n[0] + $n[2]) - 8
    if ($bareW -ge 24) {
        $bare = New-Region 'titleBare' ($n[0] + $n[2] + 4) ($t[1] + 2) ([Math]::Min($bareW, 120)) ([Math]::Max(6, $t[3] - 4))
    } else {
        $bare = New-Region 'titleBare' ($t[0] + $t[2] + 8) ($t[1] + 2) 90 ([Math]::Max(6, $t[3] - 4))
    }

    return [pscustomobject]@{
        Rect = $rc
        TabRowName = $tabRow.Name
        TabRowCount = $rows.Count
        Rows = $rows
        Palette = $palette
        Regions = @(
            (New-Region 'titleText' $t[0] $t[1] $t[2] $t[3])
            $bare
            (New-Region 'tabText' $tb[0] $tb[1] $tb[2] $tb[3])
            # A band under the last row, inside the strip. Height is bounded so
            # it cannot run off the bottom of a short strip into the status area.
            (New-Region 'stripBare' ($n[0] + 4) ($lb[1] + $lb[3] + 6) ([Math]::Max(8, $n[2] - 8)) 40)
        )
    }
}

# The palette the process actually loaded, read off the terminal body.
#
# Answers the region's FILL - the colour most of it is - rather than its mean,
# so a glyph or the cursor that wandered into the box cannot drag the number.
# Refuses outright when that fill does not cover most of the region, because a
# region that is not mostly one colour is not bare terminal background and what
# it would answer is not the palette. A refusal makes the material layer say it
# cannot judge, which is the honest outcome: an estimate off a colour that is
# not the palette is worse than no estimate.
function Get-PaletteSample($shot, $surfaces) {
    if ($null -eq $surfaces.Palette) { return $null }
    if ($null -eq (Get-ClippedBounds $shot $surfaces.Palette)) { return $null }
    $m = Measure-Region $shot $surfaces.Palette
    if ($m.Pixels -le 0 -or $m.FillCount -lt [int]($m.Pixels * 0.75)) { return $null }
    return (New-Rgb $m.Fill.R $m.Fill.G $m.Fill.B)
}

function Measure-Surfaces($shot, $surfaces) {
    $out = [ordered]@{}
    foreach ($rgn in $surfaces.Regions) { $out[$rgn.Name] = Measure-Region $shot $rgn }
    return $out
}

# ---- environment, read only -----------------------------------------------
#
# Both of these are INPUTS. This harness runs unattended in the suite, so it
# reports the desktop's polarity and High Contrast and never writes either -
# a harness that flips the desktop theme mid-run changes what every other
# harness on the machine is looking at, and takes several seconds of
# compositor transition with it.
function Get-DesktopPolarity {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    $v = try { (Get-ItemProperty -LiteralPath $key -Name 'AppsUseLightTheme' -ErrorAction Stop).AppsUseLightTheme } catch { $null }
    # Absent means light: the value is only written once the user has changed it
    # away from the default, which is exactly the machine most likely to run
    # this. Reported as 'light (default)' so a reader can tell the two apart.
    if ($null -eq $v) { return 'light-default' }
    return $(if ([int]$v -eq 0) { 'dark' } else { 'light' })
}

# ---- config staging -------------------------------------------------------

$crashPath = Join-Path $env:LOCALAPPDATA 'Wintty\crash.log'
$crashStamp = if (Test-Path $crashPath) { (Get-Item $crashPath).LastWriteTimeUtc } else { [datetime]::MinValue }

$originalXdgSet = Test-Path Env:XDG_CONFIG_HOME
$originalXdg = if ($originalXdgSet) { $env:XDG_CONFIG_HOME } else { $null }
$originalNoColorSet = Test-Path Env:NO_COLOR
$originalNoColor = if ($originalNoColorSet) { $env:NO_COLOR } else { $null }

$polarity = Get-DesktopPolarity
$highContrast = [FSz]::HighContrastOn()
Write-Host "desktop=$polarity highContrast=$highContrast (both read, neither set)"

# ---- config staging -------------------------------------------------------
#
# Created before the catalogue below, because the catalogue is taken under it.

$stage = Join-Path $env:TEMP ('wintty-frame-fuzz-{0:HHmmss}' -f (Get-Date))
$tempXdg = Join-Path $stage 'xdg'
New-Item -ItemType Directory -Force -Path (Join-Path $tempXdg 'wintty') | Out-Null
$configPath = Join-Path $tempXdg 'wintty\config.wintty'

# The built-in pair, staged as theme files. The colours are wintty_theme.zig's
# own (the pair the product overlays when no theme is configured), written
# under the themes directory both halves of the app search:
#   libghostty   $XDG_CONFIG_HOME/wintty/themes      (theme.zig, Location.user)
#   C# chrome    <config root>/wintty/themes         (ThemeSearchPath)
# which are the same directory under this staging. Without these files a
# `theme =` line names something neither half can resolve, and the theme axis
# measures nothing while looking like it ran.
$themesDir = Join-Path $tempXdg 'wintty\themes'
New-Item -ItemType Directory -Force -Path $themesDir | Out-Null

$winttyThemeLight = @'
background = #f4f6fb
foreground = #1e2333
cursor-color = #1668c4
selection-background = #cfe0f5
selection-foreground = #141828
palette = 0=#1e2333
palette = 1=#c0334a
palette = 2=#1f7a4d
palette = 3=#8a6410
palette = 4=#1668c4
palette = 5=#7a3fbf
palette = 6=#0f6e80
palette = 7=#b4bacb
palette = 8=#666e81
palette = 9=#a82a3e
palette = 10=#186540
palette = 11=#73530c
palette = 12=#0f55a6
palette = 13=#65329f
palette = 14=#0b5a69
palette = 15=#cfd5e3
'@
$winttyThemeDark = @'
background = #131620
foreground = #d5d9e5
cursor-color = #4babef
selection-background = #2b3350
selection-foreground = #f2f4fa
palette = 0=#2a2f3d
palette = 1=#f0787f
palette = 2=#7fd69b
palette = 3=#edc77a
palette = 4=#4babef
palette = 5=#b98cf0
palette = 6=#5bd5e8
palette = 7=#d5d9e5
palette = 8=#7a8296
palette = 9=#ff9aa0
palette = 10=#9ce6b4
palette = 11=#ffd99a
palette = 12=#7bc5ff
palette = 13=#d3abff
palette = 14=#8ae7f5
palette = 15=#f2f4fa
'@
[IO.File]::WriteAllText((Join-Path $themesDir 'wintty-light'), $winttyThemeLight + "`r`n")
[IO.File]::WriteAllText((Join-Path $themesDir 'wintty-dark'), $winttyThemeDark + "`r`n")

# ---- theme catalogue ------------------------------------------------------
# Enumerated UNDER the staging, by handing the child the same XDG_CONFIG_HOME
# the case launches get. The catalogue this answers is the one the launched
# processes can actually resolve; enumerating the user's own instead was how a
# theme name the staging could not resolve got into the config, leaving the
# axis silently inert. --plain is load-bearing: without it the TUI takes over
# as soon as stdout is a terminal, and what comes back is a screenful of
# escape sequences.
function Get-ThemeCatalogue([string]$Exe) {
    $names = [System.Collections.Generic.List[string]]::new()
    $out = ''
    try {
        # Started with the stream redirected rather than called as `& $Exe`,
        # which comes back empty: this is a GUI-subsystem binary, and its CLI
        # path writes to a stdout the shell never gets a handle to. An empty
        # catalogue is a legitimate answer here, so the difference does not show
        # up as an error - the run just quietly stops fuzzing themes.
        $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
        $psi.ArgumentList.Add('+list-themes')
        $psi.ArgumentList.Add('--plain')
        # The child reads this dictionary rather than the harness process's
        # own environment, so the staging never leaks into anything else here.
        $psi.EnvironmentVariables['XDG_CONFIG_HOME'] = $tempXdg
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $p = [System.Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEnd()
        [void]$p.StandardError.ReadToEnd()
        [void]$p.WaitForExit(30000)
    } catch {
        return @()
    }
    foreach ($line in ($out -split "`r?`n")) {
        $t = "$line".Trim()
        if (-not $t) { continue }
        # The plain listing prints "<name> (<source>)". Anything that is not
        # that shape is not a theme name and is dropped rather than guessed at:
        # a name this harness invents goes into a config file, and a theme that
        # does not resolve is a different case from the one being fuzzed.
        if ($t -match '^(?<n>.+?)\s+\([\w_]+\)$') { $names.Add($Matches.n.Trim()) }
    }
    return @($names | Select-Object -Unique)
}

$catalogue = @(Get-ThemeCatalogue $ExePath)
Write-Host "themes=$($catalogue.Count)"

# The pair has to be IN that catalogue. This is the acceptance gate for the
# whole theme axis: a staging that cannot enumerate its own themes is the old
# defect wearing a new directory, and every case after this would measure
# defaults while claiming to measure themes.
foreach ($must in @('wintty-light', 'wintty-dark')) {
    if ($catalogue -notcontains $must) {
        throw ("HARVEST_MISS: the staged theme '$must' is not in the catalogue the process sees under " +
               "the staging ($($catalogue.Count) name(s)$(if ($catalogue.Count -gt 0) { ': ' + ($catalogue -join ', ') })), " +
               'so no case could load it and the theme axis would be silently inert')
    }
}

# ---- the gate -------------------------------------------------------------
# Above the top-level try and above the staged config and every case launch:
# refusing over an open Wintty is the most common way this run ends.
Assert-NoWintty

<#
    One case is one config plus what this harness expects of it.

    theme         a name from the staged catalogue, or $null to write no theme
                  line. Every staged name resolves for BOTH halves of the app:
                  libghostty and the C# chrome search the same staged themes
                  directory (see the staging block above).
    windowTheme   'system' or 'wintty'
    frameStyle    'solid' | 'frosted' | 'crystal', or $null to leave it unset
                  so it inherits background-style
    background    'frosted' | 'solid'
#>
function Write-CaseConfig($Case) {
    $body = [System.Collections.Generic.List[string]]::new()
    $body.Add('# staged by frame-style-fuzz.ps1')
    # Not single-instance: this harness launches once per case in a row, and a
    # survivor from the previous case would adopt the next launch and be judged
    # against a config it never read.
    $body.Add('windows-single-instance = false')
    $body.Add('window-save-state = never')
    # The vertical layout is the only one with a window title row and a tab
    # strip as separate surfaces, which is what the contrast layer needs two of.
    $body.Add('vertical-tabs = true')
    # A blinking cursor is a pixel that changes on its own, and the stability
    # control asserts that no chrome pixel does. The chrome regions do not
    # contain the cursor, so this is belt and braces for the whole-interior
    # number the control also reports.
    $body.Add('cursor-style-blink = false')
    if ($Case.theme) { $body.Add('theme = ' + $Case.theme) }
    $body.Add('window-theme = ' + $Case.windowTheme)
    $body.Add('background-style = ' + $Case.background)
    if ($Case.frameStyle) { $body.Add('frame-style = ' + $Case.frameStyle) }

    # The material layer estimates what a translucent frame composites to, and
    # that estimate is pinned to AcrylicTintResolver's DEFAULT tint opacity and
    # to the tint colour falling back to the palette. Both are only the right
    # numbers while nothing staged here moves them, so this is checked rather
    # than remembered - a key added above would otherwise leave the estimate
    # quietly describing a window nobody launched.
    foreach ($line in $body) {
        $key = ("$line" -split '=', 2)[0].Trim()
        if ($key -in @('background-tint-opacity', 'background-tint-color',
                       'background-opacity', 'background-blur-follows-opacity')) {
            throw ("HARVEST_MISS: the staged config writes '$key', which moves the acrylic tuning the " +
                   'material layer estimates against; teach Get-BackdropGround about it before staging it')
        }
    }
    [IO.File]::WriteAllText($configPath, ($body -join "`r`n") + "`r`n")
}

# How long a translucent frame is given to stop being a transition. The
# compositor keeps moving for seconds after a window with a SystemBackdrop
# appears, and a capture taken inside that window reports a colour the app
# never chose. Opaque chrome settles far sooner, which is why a short settle
# looks correct right up until the case that is not opaque.
$SettleMs = 4500

function Add-Tab([int64]$Hwnd64) {
    $btn = Find-ByName (Get-UiaRoot $Hwnd64) 'New tab'
    if ($null -eq $btn) { return $false }
    try {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } catch { return $false }
    Start-Sleep -Milliseconds 900
    return $true
}

# The vertical strip comes up collapsed to a 48px icon rail, and a collapsed
# rail has no tab title on it to score. Expanding is therefore a prerequisite
# for the contrast layer, not a flourish.
#
# Checked by what the toggle SAYS afterwards rather than by whether the invoke
# threw. The button is in the tree in both states, which is the shape of check
# that quietly can only pass: Ensure-VerticalLayout in the tab-colours harness
# bailed out early on exactly that and still reported a clean parity check.
function Expand-Sidebar([int64]$Hwnd64) {
    $root = Get-UiaRoot $Hwnd64
    $btn = Find-ById $root 'PaneToggleButton'
    if ($null -eq $btn) { throw 'HARVEST_MISS: no PaneToggleButton, so the strip cannot be expanded' }
    if ("$($btn.Current.Name)" -eq 'Collapse sidebar') { return }
    try {
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } catch {
        throw "HARVEST_MISS: the sidebar toggle refused the invoke: $_"
    }
    Start-Sleep -Milliseconds 1200
    $again = Find-ById (Get-UiaRoot $Hwnd64) 'PaneToggleButton'
    $name = if ($null -ne $again) { "$($again.Current.Name)" } else { '<gone>' }
    if ($name -ne 'Collapse sidebar') {
        throw ("HARVEST_MISS: the sidebar toggle still reads '$name' after being invoked, so the strip " +
               'never expanded and there is no tab title on it to score')
    }
}

<#
    One launch, one case. Returns the measured surfaces plus everything the
    report wants to say about the run that produced them.

    -ExtraTabs opens tabs before measuring, which the comparator control needs
    and the ordinary cases do not.
    -Stability takes a second capture after a further settle and diffs it
    against the first.
#>
function Invoke-Case($Case, [string]$Exe, [int]$ExtraTabs = 0, [switch]$Stability, [switch]$KeepOpen) {
    Write-CaseConfig $Case

    $proc = $null
    $shotA = $null
    $shotB = $null
    # Set only on the return statement, so the finally below can tell a live
    # handoff from a throw: -KeepOpen transfers ownership to the caller via the
    # return value, and a throw never assigns it there, so an un-set flag means
    # nobody owns the window and this function is the last one that can take
    # it down. Otherwise one flaky splash drop strands a Wintty that blocks
    # every later harness's Assert-NoWintty.
    $handedOff = $false
    $stamp = Get-WinttyLaunchStamp
    try {
        $env:XDG_CONFIG_HOME = $tempXdg
        # NO_COLOR raises a banner that covers a third of the window and moves
        # the layout under it. Nothing this harness samples is behind it, but a
        # banner appearing on some machines and not others is a difference in
        # what got measured that nothing in the report would explain. Cleared
        # for the children and restored in the outer finally, same as XDG.
        Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue
        # --config-file hands libghostty the staged config by name (#787; long
        # `=` form only), on top of the XDG discovery below it. Started through
        # ProcessStartInfo rather than Start-Process because ArgumentList
        # quoting of paths with spaces is the .NET side's to do: Start-Process
        # joins the list with spaces and passes them through unquoted.
        $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
        $psi.ArgumentList.Add("--config-file=$configPath")
        $psi.WorkingDirectory = Split-Path $Exe
        $proc = [System.Diagnostics.Process]::Start($psi)
        [void](Wait-Ready $proc)
        $main = @(Get-WinUiWindows ([uint32]$proc.Id)) | Select-Object -First 1
        if (-not $main) { throw 'HARVEST_MISS: window vanished after ready' }
        $hwnd64 = [int64]$main.Hwnd64

        Expand-Sidebar $hwnd64
        for ($i = 0; $i -lt $ExtraTabs; $i++) {
            if (-not (Add-Tab $hwnd64)) { throw "HARVEST_MISS: the New tab button would not open tab $($i + 2)" }
        }

        Start-Sleep -Milliseconds $SettleMs
        $proc.Refresh()
        if ($proc.HasExited) { throw "PRODUCT_FAIL: exited while settling on case '$($Case.id)': exit=$($proc.ExitCode)" }

        $surfaces = Get-Surfaces $hwnd64
        $shotA = Get-Shot $hwnd64
        $measured = Measure-Surfaces $shotA $surfaces
        # Off the same capture as the chrome, so the palette and the chrome it
        # is estimated against are the same instant of the same window.
        $paletteSample = Get-PaletteSample $shotA $surfaces

        # A capture of a window that has not drawn yet. The terminal body is
        # the theme's own background, and neither half of the staged pair is
        # near black (#f4f6fb, #131620), so a sample reading at pure black is
        # not a colour anything chose: the surface had not been composited
        # when the screen was read. Scoring regions off that capture filed
        # contrast defects on a working build (2026-08-28: two consecutive
        # cases at ink 0,0,0 on fill 0,0,0, both clean on the relaunch), so it
        # is refused rather than judged and the case loop below retries it
        # once before the run gives the area up.
        #
        # Not under High Contrast: there the terminal can legitimately paint a
        # system scheme colour, black included, and the material layer this
        # sample feeds has already stood down - refusing cases over a colour
        # the OS chose would turn the HC stand-down into an exit 1.
        if (-not $highContrast -and $null -ne $paletteSample -and $paletteSample.R -le 8 -and
            $paletteSample.G -le 8 -and $paletteSample.B -le 8) {
            throw ("HARVEST_MISS: unpainted window - the terminal body read " +
                   "$($paletteSample.R),$($paletteSample.G),$($paletteSample.B), which no theme this " +
                   'staging loads paints, so the window had not drawn when the screen was captured')
        }

        # Where each region sat ON THE SCREEN, kept so the material layer can
        # come back to the same rectangle after every window is down and read
        # what is behind it. See Compare-Material: a translucent frame is a
        # function of the desktop, so a desktop sitting at the opaque frame's
        # own shade makes the two indistinguishable on a build that is working.
        $abs = @{}
        foreach ($rgn in $surfaces.Regions) {
            $b = Get-ClippedBounds $shotA $rgn
            if ($null -eq $b) { continue }
            $abs[$rgn.Name] = @(($shotA.L + $b[0]), ($shotA.T + $b[1]), ($b[2] - $b[0]), ($b[3] - $b[1]))
        }

        $stabilityReport = $null
        if ($Stability) {
            Start-Sleep -Milliseconds 1600
            $shotB = Get-Shot $hwnd64
            if ($shotB.W -ne $shotA.W -or $shotB.H -ne $shotA.H) {
                throw 'HARVEST_MISS: the window resized between the two stability captures'
            }
            $in = Get-Interior $shotA
            $chromeDiff = 0
            foreach ($rgn in $surfaces.Regions) {
                $b = Get-ClippedBounds $shotA $rgn
                if ($null -eq $b) { continue }
                $chromeDiff += [FSz]::DiffCount($shotA.Px, $shotB.Px, $shotA.StrideInts, $b[0], $b[1], $b[2], $b[3], 1)
            }
            # The whole frame and the interior, side by side, because the gap
            # between them IS the border-band claim this harness is built on.
            # Reported rather than asserted: what the border does is DWM's, and
            # a harness that failed on it would be reporting the desktop.
            $wholeDiff = [FSz]::DiffCount($shotA.Px, $shotB.Px, $shotA.StrideInts, 0, 0, $shotA.W, $shotA.H, 1)
            $interiorDiff = [FSz]::DiffCount($shotA.Px, $shotB.Px, $shotA.StrideInts, $in.X0, $in.Y0, $in.X1, $in.Y1, 1)
            $stabilityReport = [ordered]@{
                chromeRegionsDiff = $chromeDiff
                interiorDiff = $interiorDiff
                wholeFrameDiff = $wholeDiff
                borderBandDiff = $wholeDiff - $interiorDiff
            }
        }

        $handedOff = $true
        return [pscustomobject]@{
            Hwnd = $hwnd64
            Proc = $proc
            Stamp = $stamp
            Surfaces = $surfaces
            Measured = $measured
            Palette = $paletteSample
            AbsRegions = $abs
            Stability = $stabilityReport
            Shot = $shotA
            WindowSize = ('{0}x{1}' -f $shotA.W, $shotA.H)
        }
    }
    catch {
        Close-Shot $shotA
        throw
    }
    finally {
        Close-Shot $shotB
        # -not $KeepOpen: this function owns the window. -KeepOpen without the
        # handoff: the return never ran, so the caller never received the
        # process and cannot take it down itself.
        if (-not $KeepOpen -or -not $handedOff) {
            if ($null -ne $proc) {
                $proc.Refresh()
                if (-not $proc.HasExited) { try { $proc.Kill($true); [void]$proc.WaitForExit(3000) } catch { } }
            }
            Stop-WinttyStartedAfter -Since $stamp -ExePath $Exe
            Start-Sleep -Milliseconds 700
        }
    }
}

function Stop-Case($result, [string]$Exe) {
    if ($null -eq $result) { return }
    Close-Shot $result.Shot
    if ($null -ne $result.Proc) {
        $result.Proc.Refresh()
        if (-not $result.Proc.HasExited) { try { $result.Proc.Kill($true); [void]$result.Proc.WaitForExit(3000) } catch { } }
    }
    Stop-WinttyStartedAfter -Since $result.Stamp -ExePath $Exe
    Start-Sleep -Milliseconds 700
}

# ---- the spanning set -----------------------------------------------------
#
# Fixed and deterministic: every code path the two keys reach, once each.
# Both window-theme values against all three frame-style values, plus the
# unset case that inherits background-style, plus the pair that proves the
# degrade rule. The theme is pinned so the material comparisons below are
# between two configs that differ in exactly one key.
function New-Case([string]$Id, [string]$WindowTheme, $FrameStyle, [string]$Background, $Theme) {
    return [ordered]@{
        id = $Id; windowTheme = $WindowTheme; frameStyle = $FrameStyle
        background = $Background; theme = $Theme
    }
}

# The spanning set takes the half of the pair OPPOSITE the desktop, and the
# choice is measured rather than aesthetic: a translucent frame composites the
# palette over the system base for the active polarity, so a palette sitting on
# the same side as the desktop lands within a few counts of the opaque frame's
# own shade (wintty-dark on a dark desktop: the tint at the default opacity
# composites to 28,29,32 against an opaque 12,12,12 - a separation the
# comparator's own 20-count threshold cannot resolve, verified live 2026-08-28
# where a build matching its own model to within 2 counts still scored 18).
# The opposite half moves the composite 70+ counts, which is the side the
# must-differ rules can actually judge. The matching half still runs, as the
# mirrored set below, for the contrast layer and the degrade rule, which are
# palette-independent.
$spanTheme = if ($polarity -eq 'dark') { 'wintty-light' } else { 'wintty-dark' }
$altTheme = if ($polarity -eq 'dark') { 'wintty-dark' } else { 'wintty-light' }
Write-Host "spanning theme=$spanTheme (opposite the $polarity desktop), mirrored theme=$altTheme"

$spanning = @(
    New-Case 'sys-solid'      'system' 'solid'   'frosted' $spanTheme
    New-Case 'sys-frosted'    'system' 'frosted' 'frosted' $spanTheme
    New-Case 'sys-crystal'    'system' 'crystal' 'frosted' $spanTheme
    New-Case 'wt-solid'       'wintty' 'solid'   'frosted' $spanTheme
    New-Case 'wt-frosted'     'wintty' 'frosted' 'frosted' $spanTheme
    New-Case 'wt-crystal'     'wintty' 'crystal' 'frosted' $spanTheme
    New-Case 'wt-inherit'     'wintty' $null     'frosted' $spanTheme
    New-Case 'wt-over-solid'  'wintty' 'frosted' 'solid'   $spanTheme
    New-Case 'wt-solid-solid' 'wintty' 'solid'   'solid'   $spanTheme
)

# The same nine configs on the other half of the pair: the theme axis across
# the whole grid. Every case differs from its un-suffixed twin in `theme` and
# nothing else, so a theme comparison is a matched pair the same way a material
# comparison is, and the material rules below can be judged within one theme.
$spanning += @(
    New-Case 'sys-solid-alt'      'system' 'solid'   'frosted' $altTheme
    New-Case 'sys-frosted-alt'    'system' 'frosted' 'frosted' $altTheme
    New-Case 'sys-crystal-alt'    'system' 'crystal' 'frosted' $altTheme
    New-Case 'wt-solid-alt'       'wintty' 'solid'   'frosted' $altTheme
    New-Case 'wt-frosted-alt'     'wintty' 'frosted' 'frosted' $altTheme
    New-Case 'wt-crystal-alt'     'wintty' 'crystal' 'frosted' $altTheme
    New-Case 'wt-inherit-alt'     'wintty' $null     'frosted' $altTheme
    New-Case 'wt-over-solid-alt'  'wintty' 'frosted' 'solid'   $altTheme
    New-Case 'wt-solid-solid-alt' 'wintty' 'solid'   'solid'   $altTheme
)

$frameValues = @('solid', 'frosted', 'crystal')
$backgroundValues = @('frosted', 'solid')
$windowThemeValues = @('system', 'wintty')

$randomCases = @()
for ($i = 0; $i -lt $Random; $i++) {
    $theme = if ($catalogue.Count -gt 0) { $catalogue[$rng.Next(0, $catalogue.Count)] } else { $null }
    # An unset frame-style is one of the draws, not a separate mode: inheriting
    # background-style is a code path like any other and belongs in the spread.
    $fs = if ($rng.Next(0, 4) -eq 0) { $null } else { $frameValues[$rng.Next(0, $frameValues.Count)] }
    $randomCases += New-Case ("rnd-$i") $windowThemeValues[$rng.Next(0, $windowThemeValues.Count)] `
        $fs $backgroundValues[$rng.Next(0, $backgroundValues.Count)] $theme
}

$findings = [System.Collections.Generic.List[string]]::new()
$caseErrors = [System.Collections.Generic.List[string]]::new()
$rows = [System.Collections.Generic.List[object]]::new()
$controls = [ordered]@{}
$materials = [System.Collections.Generic.List[object]]::new()
$pipeReport = [ordered]@{ attempted = $false }
$measuredByCase = @{}
$absByCase = @{}
$paletteByCase = @{}
$caseById = @{}
$detectorProven = $false

# ---- named pipe theme preview --------------------------------------------
#
# ThemePreviewService listens on ghostty-theme-preview-<pid> and accepts
# PREVIEW:<name> and CONFIRM:<name>. It updates ShellThemeService, which is not
# necessarily every surface this harness samples - so what it does is MEASURED
# here and reported, and nothing is asserted on it. If it does not move the
# sampled surfaces, a fresh launch per case is the only honest way to change
# theme, which is what the cases above already do.
function Test-ThemePipe([int]$ProcId, [string]$ThemeName, $Before, $Surfaces, [int64]$Hwnd64) {
    $result = [ordered]@{
        attempted = $true; theme = $ThemeName; connected = $false
        wrote = $false; error = $null; moved = $null; deltas = [ordered]@{}
    }
    $pipe = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', "ghostty-theme-preview-$ProcId",
            [System.IO.Pipes.PipeDirection]::Out)
        $pipe.Connect(4000)
        $result.connected = $true
        $bytes = [System.Text.Encoding]::UTF8.GetBytes("PREVIEW:$ThemeName`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $result.wrote = $true
    }
    catch {
        $result.error = "$_"
        return $result
    }
    finally {
        if ($null -ne $pipe) { try { $pipe.Dispose() } catch { } }
    }

    Start-Sleep -Milliseconds $SettleMs
    $after = $null
    try {
        $after = Get-Shot $Hwnd64
        $now = Measure-Surfaces $after $Surfaces
        $moved = $false
        foreach ($k in $Before.Keys) {
            $d = Get-MeanDelta $Before[$k] $now[$k]
            $result.deltas[$k] = $d
            if ($d -ge $ChannelDelta) { $moved = $true }
        }
        $result.moved = $moved
    }
    catch { $result.error = "$_" }
    finally { Close-Shot $after }
    return $result
}

# ---- control: the backdrop estimate against the product's own -------------
#
# Get-BackdropGround decides whether a failed material comparison is a defect
# or a run this harness cannot judge, and it does that with a COPY of
# BackdropGround.Estimate's arithmetic. A copy that has drifted from the
# original is worse than no guard at all: it excuses defects the build has, or
# files ones it does not. So the copy is run against the shipped
# Ghostty.Core.dll before any case is judged, over a spread that covers both
# desktop poles, all three backdrop styles, a style nobody has added yet, the
# palettes that sit on top of the system base and either side of it, and every
# tint opacity the config can reach.
#
# The assembly is loaded from beside the exe under test, so what is checked is
# the model in the BUILD being fuzzed rather than one in some other checkout.
# When it cannot be loaded the run says so and carries on with the copy: a
# harness that refuses to run because a dll moved is a harness nobody runs.
function Test-BackdropGroundMirror([string]$Exe) {
    $report = [ordered]@{ loaded = $false; error = $null; checked = 0; mismatches = @() }
    $dll = Join-Path (Split-Path $Exe) 'Ghostty.Core.dll'
    $method = $null
    try {
        $asm = [System.Reflection.Assembly]::LoadFrom($dll)
        $type = $asm.GetType('Ghostty.Core.Shell.BackdropGround')
        if ($null -eq $type) { throw 'no Ghostty.Core.Shell.BackdropGround in the assembly' }
        $method = $type.GetMethod('Estimate')
        if ($null -eq $method) { throw 'BackdropGround has no Estimate' }
        $report.loaded = $true
    }
    catch {
        $report.error = "$dll : $_"
        return $report
    }

    $bad = [System.Collections.Generic.List[string]]::new()
    # 'solid' and the unknown style are both here on purpose: the product folds
    # everything that is not frosted or crystal to the painted root, and a copy
    # that only handled the three named styles would agree on all three and
    # disagree on the fourth. 'Frosted' is here because that is the drift this
    # control actually caught - PowerShell's -eq called it frosted and the
    # product's ordinal comparison did not.
    foreach ($rgb in @(0x000000, 0xFFFFFF, 0x282C34, 0xF4F6FB, 0x1E1E2E, 0xF3F3F3, 0x202020, 0x0C0C0C, 0x808080)) {
        foreach ($osDark in @($true, $false)) {
            foreach ($style in @('frosted', 'crystal', 'solid', 'Frosted', 'CRYSTAL', 'something-nobody-has-added-yet')) {
                foreach ($op in @(0.0, 0.3, 0.5, 0.9, 1.0)) {
                    $report.checked++
                    $theirs = [uint32]$method.Invoke($null, @([uint32]$rgb, [bool]$osDark, [string]$style, [double]$op))
                    $mine = Get-BackdropGround (New-Rgb (($rgb -shr 16) -band 0xFF) (($rgb -shr 8) -band 0xFF) ($rgb -band 0xFF)) `
                                               $osDark $style $op
                    $minePacked = [uint32](($mine.R -shl 16) -bor ($mine.G -shl 8) -bor $mine.B)
                    if ($minePacked -ne $theirs) {
                        $bad.Add(('palette {0:X6} osDark={1} style={2} tint={3}: this harness says {4:X6}, the product says {5:X6}' `
                                  -f $rgb, $osDark, $style, $op, $minePacked, $theirs))
                    }
                }
            }
        }
    }
    $report.mismatches = @($bad | Select-Object -First 8)
    $report.mismatchCount = $bad.Count
    return $report
}

# ---- run ------------------------------------------------------------------
try {
    # ---- controls, before anything is judged ------------------------------
    #
    # Four of them, and each can fail on its own. Together they say the capture
    # is stable, the comparator can report a difference, the ink extractor can
    # find ink, and it does not invent ink where there is none. A run that
    # cannot show all four leaves with 1: what follows would be a page of
    # absences from a detector that never demonstrated it can see a presence.
    Write-Host '--- controls'

    # Runs first because it needs no window and gates everything the material
    # layer's ambiguity path is built on.
    $controls.backdropGroundMirror = Test-BackdropGroundMirror $ExePath
    if ($controls.backdropGroundMirror.loaded) {
        if ($controls.backdropGroundMirror.mismatchCount -gt 0) {
            throw ("HARVEST_MISS: this harness's copy of BackdropGround.Estimate disagrees with the one in " +
                   "the build under test on $($controls.backdropGroundMirror.mismatchCount) of " +
                   "$($controls.backdropGroundMirror.checked) inputs, so it cannot tell a frame-style defect " +
                   'from a composite that legitimately matches the opaque frame. First disagreements: ' +
                   ($controls.backdropGroundMirror.mismatches -join '; '))
        }
        Write-Host ("    backdrop-ground mirror: $($controls.backdropGroundMirror.checked) inputs agree with " +
                    'the shipped Ghostty.Core.dll')
    } else {
        Write-Host ("    backdrop-ground mirror: NOT cross-checked ($($controls.backdropGroundMirror.error))") `
                   -ForegroundColor Yellow
    }

    $controlCase = New-Case 'control' 'wintty' 'solid' 'frosted' $spanTheme
    $control = $null
    try {
        # Two tabs, because the comparator control compares a selected row
        # against an unselected one and one tab has no unselected row.
        $control = Invoke-Case $controlCase $ExePath -ExtraTabs 1 -Stability -KeepOpen

        $controls.windowSize = $control.WindowSize
        $controls.stability = $control.Stability
        $controls.tabRows = $control.Surfaces.TabRowCount

        # 1. Stability. Zero chrome pixels may change between two captures of
        #    one window nobody touched. A non-zero number here means every
        #    material comparison below is measuring the machine.
        if ($control.Stability.chromeRegionsDiff -ne 0) {
            throw ("HARVEST_MISS: two captures of one unchanged window differ in " +
                   "$($control.Stability.chromeRegionsDiff) chrome pixel(s), so nothing compared across " +
                   "configs would be a claim about the config " +
                   "(interior $($control.Stability.interiorDiff), border band $($control.Stability.borderBandDiff))")
        }

        # 2. The comparator fires. A selected tab row and an unselected one are
        #    painted differently by a build that works - the tab-close harness
        #    is what asserts that, and it is borrowed here as a difference this
        #    harness knows must be on the screen. If the comparator cannot see
        #    it, it cannot see solid against frosted either.
        $rowsAll = @($control.Surfaces.Rows)
        $sel = @($rowsAll | Where-Object { $_.Selected })
        $unsel = @($rowsAll | Where-Object { $_.Selected -eq $false })
        if ($sel.Count -ne 1 -or $unsel.Count -lt 1) {
            throw ("HARVEST_MISS: the control window has $($sel.Count) selected and $($unsel.Count) " +
                   'unselected tab rows, so the comparator has no known difference to be proven against')
        }
        $rc = $control.Surfaces.Rect
        function RowProbe($row, [string]$name) {
            $r = $row.Rect
            # A few px in from the row's left edge: past the strip inset, well
            # left of the icon lane, and clear of the label.
            return New-Region $name ([int]($r.X - $rc.L + 4)) ([int]($r.Y - $rc.T + $r.Height / 2 - 3)) 10 6
        }
        $selStat = Measure-Region $control.Shot (RowProbe $sel[0] 'ctlSelected')
        $unselStat = Measure-Region $control.Shot (RowProbe $unsel[0] 'ctlUnselected')
        $rowDelta = Get-MeanDelta $selStat $unselStat
        $controls.comparator = [ordered]@{
            selected = (Format-Rgb $selStat.Mean); unselected = (Format-Rgb $unselStat.Mean)
            delta = $rowDelta; needs = $ChannelDelta
        }
        if ($rowDelta -lt $ChannelDelta) {
            throw ("HARVEST_MISS: the selected tab row and an unselected one measure the same fill " +
                   "(delta $rowDelta, needs $ChannelDelta), so this harness cannot show its comparator " +
                   'fires and every material result below would be an unproven absence. ' +
                   'mouse-fuzz-tab-close-selection is the harness that owns that assertion; run it')
        }

        # 3 and 4. The ink extractor, both ways. The title text region must
        #    yield a luminance gap far larger than an ink-free strip of the same
        #    chrome does, and that ink-free strip must not itself score above
        #    the contrast floor. A blank capture fails the first; an extractor
        #    that promotes antialiasing noise to ink fails the second.
        $textStat = $control.Measured['titleText']
        $bareStat = $control.Measured['titleBare']
        $controls.ink = [ordered]@{
            textGap = $textStat.LumGap; bareGap = $bareStat.LumGap
            textContrast = $textStat.Contrast; bareContrast = $bareStat.Contrast
            textFill = (Format-Rgb $textStat.Fill); textInk = (Format-Rgb $textStat.Ink)
            bareFill = (Format-Rgb $bareStat.Fill); bareInk = (Format-Rgb $bareStat.Ink)
            floor = $ContrastFloor
        }
        if ($textStat.LumGap -lt ($bareStat.LumGap * 3.0) -or $textStat.LumGap -le 0.0) {
            throw ("HARVEST_MISS: the title text region's ink/fill luminance gap is $($textStat.LumGap) " +
                   "against $($bareStat.LumGap) for a strip of the same chrome with no text on it, so the " +
                   'extractor is not separating ink from fill and every contrast score below is noise')
        }
        if ($bareStat.Contrast -ge $ContrastFloor) {
            throw ("HARVEST_MISS: a strip of chrome with no text on it scores $($bareStat.Contrast):1, " +
                   "at or above the $($ContrastFloor):1 floor this harness judges ink by, so the extractor " +
                   'is inventing ink and a genuinely unreadable row would score as readable')
        }
        $detectorProven = $true
        Write-Host ("    stability chrome=$($control.Stability.chromeRegionsDiff) interior=$($control.Stability.interiorDiff) " +
                    "border=$($control.Stability.borderBandDiff)")
        Write-Host ("    comparator rowDelta=$rowDelta (needs $ChannelDelta)")
        Write-Host ("    ink textGap=$($textStat.LumGap) bareGap=$($bareStat.LumGap) bareContrast=$($bareStat.Contrast)")

        # The pipe probe rides on the control window, which is already up and
        # already measured. Nothing is asserted on the result.
        if ($catalogue.Count -gt 0) {
            $other = @($catalogue | Where-Object { $_ -ne $spanTheme })
            if ($other.Count -gt 0) {
                $pick = $other[$rng.Next(0, $other.Count)]
                Write-Host "    pipe: PREVIEW:$pick"
                $pipeReport = Test-ThemePipe $control.Proc.Id $pick $control.Measured $control.Surfaces $control.Hwnd
                Write-Host ("    pipe connected=$($pipeReport.connected) moved=$($pipeReport.moved) " +
                            "deltas=" + (($pipeReport.deltas.Keys | ForEach-Object { "$_=$($pipeReport.deltas[$_])" }) -join ','))
            }
        }
    }
    finally {
        Stop-Case $control $ExePath
    }

    # ---- cases -------------------------------------------------------------
    # The spanning set runs in a seeded order so a finding that depends on what
    # ran before it replays, and the randomized cases follow it.
    $order = @($spanning | Sort-Object { $rng.Next() }) + $randomCases
    Write-Host ('order=' + (($order | ForEach-Object { $_.id }) -join ','))

    foreach ($case in $order) {
        $label = "$($case.id) window-theme=$($case.windowTheme) frame-style=$(if ($case.frameStyle) { $case.frameStyle } else { '<unset>' }) background-style=$($case.background) theme=$(if ($case.theme) { $case.theme } else { '<none>' })"
        Write-Host "--- $label"

        # Per case, so one case that could not run does not throw away what the
        # cases before it found. Findings outrank a case that could not run,
        # which is the order fuzz-suite.ps1 puts them in too. A HARVEST_MISS is
        # relaunched once before it is given up on: with a launch per case the
        # exposure is to the machine's races - a window captured before it drew
        # (2026-08-28: ink 0,0,0 on fill 0,0,0 on two consecutive cases), a
        # UIA tree asked for its NavView before it was populated - and a
        # second read of the same config settles them. PRODUCT_FAIL is never
        # retried: that is the build answering, not the machine.
        $res = $null
        try {
            for ($attempt = 1; $attempt -le 2; $attempt++) {
                try {
                    $res = Invoke-Case $case $ExePath
                    break
                }
                catch {
                    if ($attempt -eq 1 -and "$_" -like 'HARVEST_MISS:*') {
                        Write-Host "    case '$($case.id)' could not be read (racing the machine); relaunching once: $_" -ForegroundColor Yellow
                        continue
                    }
                    throw
                }
            }
        }
        catch {
            $note = "case '$($case.id)': $_"
            if ("$_" -like 'PRODUCT_FAIL*') { $findings.Add($note) } else { $caseErrors.Add($note) }
            Write-Host "    $note" -ForegroundColor Yellow
            $rows.Add([ordered]@{ case = $case.id; config = $case; error = "$_" })
            continue
        }

        try {
            $m = $res.Measured
            $measuredByCase[$case.id] = $m
            $absByCase[$case.id] = $res.AbsRegions
            $paletteByCase[$case.id] = $res.Palette
            $caseById[$case.id] = $case

            $row = [ordered]@{
                case = $case.id
                config = $case
                windowSize = $res.WindowSize
                tabRows = $res.Surfaces.TabRowCount
                # What the material layer estimates this case's translucent
                # chrome from, recorded whether or not any comparison needed it.
                palette = $(if ($null -ne $res.Palette) { Format-Rgb $res.Palette } else { $null })
                regions = [ordered]@{}
            }
            foreach ($k in $m.Keys) {
                $row.regions[$k] = [ordered]@{
                    mean = (Format-Rgb $m[$k].Mean)
                    fill = (Format-Rgb $m[$k].Fill)
                    ink = (Format-Rgb $m[$k].Ink)
                    contrast = $m[$k].Contrast
                    lumGap = $m[$k].LumGap
                }
            }

            # The contrast layer. Both surfaces, each against its own fill.
            foreach ($pair in @(
                @{ region = 'titleText'; what = 'the title row' }
                @{ region = 'tabText';   what = 'the tab strip' })) {
                $st = $m[$pair.region]
                if ($st.Contrast -lt $ContrastFloor) {
                    $findings.Add(("case '$($case.id)' ($label): $($pair.what) paints its text at " +
                                   "$($st.Contrast):1 against its own fill, under the $($ContrastFloor):1 floor " +
                                   "(ink $(Format-Rgb $st.Ink) on fill $(Format-Rgb $st.Fill))"))
                    Save-Shot $res.Shot ("contrast-" + $case.id)
                }
            }

            Write-Host ("    titleText=$($m['titleText'].Contrast):1 tabText=$($m['tabText'].Contrast):1 " +
                        "titleBare=$(Format-Rgb $m['titleBare'].Mean) stripBare=$(Format-Rgb $m['stripBare'].Mean) " +
                        "palette=$(if ($null -ne $res.Palette) { Format-Rgb $res.Palette } else { '<unread>' })")
            $rows.Add($row)
        }
        finally {
            Stop-Case $res $ExePath
        }
    }

    # ---- the material layer ------------------------------------------------
    #
    # Cross-case, so it runs once every case has been measured rather than
    # inside the loop: each comparison needs two configs that differ in exactly
    # one key, and the loop only ever holds one of them.
    #
    # Every comparison is RELATIVE. The harness never learns what colour a
    # frosted frame is meant to be, only whether it is the solid one - which is
    # what makes this a check on the key being wired through rather than on the
    # theme.
    # -Defect names the config key a failed must-differ comparison indicts, so
    # the finding says what actually broke for a theme comparison too.
    function Compare-Material([string]$A, [string]$B, [string]$Rule, [bool]$MustDiffer, [string]$Defect = 'frame-style') {
        if (-not $measuredByCase.ContainsKey($A) -or -not $measuredByCase.ContainsKey($B)) {
            $caseErrors.Add("material '$Rule' needs both '$A' and '$B' and one of them did not run")
            return
        }
        $deltas = [ordered]@{}
        $maxDelta = -1
        foreach ($rgn in @('titleBare', 'stripBare')) {
            $d = Get-MeanDelta $measuredByCase[$A][$rgn] $measuredByCase[$B][$rgn]
            $deltas[$rgn] = $d
            if ($d -gt $maxDelta) { $maxDelta = $d }
        }
        $verdict = if ($MustDiffer) {
            if ($maxDelta -ge $ChannelDelta) { 'ok' } else { 'failed' }
        } else {
            if ($maxDelta -lt $ChannelDelta) { 'ok' } else { 'failed' }
        }
        $materials.Add([ordered]@{
            rule = $Rule; a = $A; b = $B; mustDiffer = $MustDiffer
            deltas = $deltas; maxDelta = $maxDelta; needs = $ChannelDelta
            asserted = (-not $highContrast); verdict = $verdict
        })
        Write-Host ("    material $Rule ($A vs $B): maxDelta=$maxDelta needs=$ChannelDelta -> $verdict" +
                    $(if ($highContrast) { ' [reported only: High Contrast]' } else { '' }))

        # High Contrast pins the frame solid, so under it solid and frosted
        # agree by design and the degrade rule is unobservable. Measured and
        # reported anyway - what it does under HC is worth having on the record
        # - but not asserted, because asserting it would report the pin as a
        # defect on exactly the machines the pin exists for.
        if ($highContrast -or $verdict -eq 'ok') { return }

        if ($MustDiffer) {
            # A translucent frame does not paint a colour, it paints a
            # COMPOSITE, and this comparison has one way to be wrong that is not
            # the build's fault: a composite the product means to land on the
            # shade the opaque frame already paints makes solid and frosted
            # measure alike on a build that wired frame-style through perfectly.
            # That is a run this harness cannot judge, not a defect.
            #
            # What decides it is the composite the product INTENDS, not the raw
            # screen behind the window. The screen is only one of the acrylic's
            # inputs; the tint pulls the surface towards the palette and the
            # luminosity blend towards the system base for the active desktop
            # polarity, and between them a dark palette on a dark desktop lands
            # a few counts from the opaque shade while the raw screen sits well
            # clear of it. Reading that raw screen as proof the desktop was not
            # to blame is what filed this comparison as a defect on a working
            # build.
            #
            # Estimated from the palette the window under test actually loaded,
            # with BackdropGround.Estimate's own arithmetic - see
            # Get-BackdropGround, and the control that holds the two together.
            $palette = $paletteByCase[$B]
            $ground = $null
            $groundStyle = $null
            if ($null -ne $palette -and $caseById.ContainsKey($B)) {
                $groundStyle = Get-ChromeGroundStyle $caseById[$B]
                $ground = Get-BackdropGround $palette ($polarity -eq 'dark') $groundStyle $DefaultTintOpacity
            }

            # The screen behind, read for the RECORD only. Every window is down
            # by the time this runs, so the same rectangle now holds what the
            # frosted frame was sampling, and having it in the artifact is worth
            # the capture even though no verdict turns on it any more.
            $behind = [ordered]@{}
            foreach ($rgn in $deltas.Keys) {
                $box = $absByCase[$A][$rgn]
                if ($null -eq $box) { continue }
                $desk = Get-ScreenMeanAt $box[0] $box[1] $box[2] $box[3]
                if ($null -eq $desk) { continue }
                $behind[$rgn] = (Format-Rgb $desk.Mean)
            }
            $materials[$materials.Count - 1].desktopBehind = $behind

            # Recorded whatever it decides, so the next comparison that comes
            # out wrong is diagnosable from this file rather than by capturing
            # the same two configs again by hand.
            $estimate = [ordered]@{
                palette = $(if ($null -ne $palette) { Format-Rgb $palette } else { $null })
                paletteFrom = $B
                osDark = ($polarity -eq 'dark')
                systemBase = (Format-Rgb $(if ($polarity -eq 'dark') { $SystemBaseDark } else { $SystemBaseLight }))
                groundStyle = $groundStyle
                tintOpacity = $DefaultTintOpacity
                ground = $(if ($null -ne $ground) { Format-Rgb $ground } else { $null })
                opaqueFrom = $A
                deltaToOpaque = [ordered]@{}
            }
            $ambiguous = $null -ne $ground
            foreach ($rgn in $deltas.Keys) {
                if ($null -eq $ground) { break }
                $d = Get-ChannelDelta $ground $measuredByCase[$A][$rgn].Mean
                $estimate.deltaToOpaque[$rgn] = $d
                # One region the translucent frame was meant to move and did not
                # is a defect, whatever the other regions were meant to do. Only
                # a comparison where NOTHING was meant to move is unjudgeable.
                if ($d -ge $ChannelDelta) { $ambiguous = $false }
            }
            $materials[$materials.Count - 1].estimatedGround = $estimate

            if ($null -eq $ground) {
                $materials[$materials.Count - 1].verdict = 'unjudgeable'
                $caseErrors.Add(("material '$Rule' cannot be judged: '$A' and '$B' measure alike (max channel " +
                                 "delta $maxDelta) and this run could not read the palette '$B' was painted " +
                                 'from, so there is nothing to estimate what a translucent frame there would ' +
                                 'composite to, and no way to tell a frame-style defect from a composite that ' +
                                 "legitimately matches the opaque frame. The terminal body of '$B' did not come " +
                                 'back a single flat colour: re-run with nothing writing to the terminal'))
                return
            }
            if ($ambiguous) {
                $materials[$materials.Count - 1].verdict = 'indistinguishable'
                $other = if ($polarity -eq 'dark') { 'light' } else { 'dark' }
                $caseErrors.Add(("material '$Rule' cannot be judged with this palette on a $polarity desktop: " +
                                 "'$A' and '$B' measure alike (max channel delta $maxDelta), and a translucent " +
                                 "frame here is meant to composite to $(Format-Rgb $ground) - the palette " +
                                 "$(Format-Rgb $palette) tinted at $DefaultTintOpacity over the $polarity system " +
                                 "base $($estimate.systemBase) - which is within $ChannelDelta of the shade the " +
                                 'opaque frame paints (' +
                                 (($estimate.deltaToOpaque.Keys | ForEach-Object { "$_=$($estimate.deltaToOpaque[$_])" }) -join ', ') +
                                 '). A build that wired frame-style through is indistinguishable here from one ' +
                                 'that never got the key. Re-run with a palette further from the system base, ' +
                                 "or with the desktop in $other polarity, which moves the base out from under " +
                                 'the palette. Moving the window will not help on its own: the tint and the ' +
                                 'luminosity blend are what put the composite there. The screen behind read ' +
                                 (($behind.Keys | ForEach-Object { "$_=$($behind[$_])" }) -join ', ')))
                return
            }
            $findings.Add(("$Defect is not reaching the chrome: '$A' and '$B' differ only in " +
                           "$Defect and paint the same chrome (max channel delta $maxDelta, needs " +
                           "$ChannelDelta; " + (($deltas.Keys | ForEach-Object { "$_=$($deltas[$_])" }) -join ', ') +
                           "), and a translucent frame here was meant to composite to " +
                           "$(Format-Rgb $ground) from palette $(Format-Rgb $palette), clear of the shade " +
                           "'$A' paints (" +
                           (($estimate.deltaToOpaque.Keys | ForEach-Object { "$_=$($estimate.deltaToOpaque[$_])" }) -join ', ') +
                           "), so neither the palette nor the desktop is what makes them alike. The screen " +
                           'behind read ' + (($behind.Keys | ForEach-Object { "$_=$($behind[$_])" }) -join ', ')))
        } else {
            $findings.Add(("$Rule does not hold: '$A' and '$B' should paint the same chrome and differ by " +
                           "$maxDelta (" + (($deltas.Keys | ForEach-Object { "$_=$($deltas[$_])" }) -join ', ') + ")"))
        }
    }

    Write-Host '--- material'
    Compare-Material 'sys-solid' 'sys-frosted' 'solid differs from frosted under window-theme=system' $true
    Compare-Material 'wt-solid' 'wt-frosted' 'solid differs from frosted under window-theme=wintty' $true
    # The degrade rule, as an equality: a translucent frame over a solid
    # background has nothing behind it to reveal and takes its opaque shade.
    Compare-Material 'wt-over-solid' 'wt-solid-solid' 'a translucent frame over a solid backdrop degrades to solid' $false

    # The degrade rule on the mirrored (desktop-matching) half of the pair too:
    # it is an equality between two solid backgrounds, so the palette does not
    # enter it and it is judgeable on either half.
    Compare-Material 'wt-over-solid-alt' 'wt-solid-solid-alt' 'a translucent frame over a solid backdrop degrades to solid (mirrored theme)' $false

    # Solid against frosted on the MIRRORED half is measured and reported, not
    # asserted. The mirrored theme sits on the same side as the desktop, and
    # the estimate above predicts what that does: the tint composites the
    # palette back to within the comparator's own resolution of the opaque
    # frame's shade (measured live 2026-08-28: maxDelta 18 against a model that
    # puts the intended separation at exactly the 20-count threshold). Filing
    # that as a defect would be scoring the threshold, not the build.
    foreach ($pair in @(
        @{ a = 'sys-solid-alt'; b = 'sys-frosted-alt'; what = 'window-theme=system' }
        @{ a = 'wt-solid-alt';  b = 'wt-frosted-alt';  what = 'window-theme=wintty' })) {
        if (-not $measuredByCase.ContainsKey($pair.a) -or -not $measuredByCase.ContainsKey($pair.b)) { continue }
        $deltas = [ordered]@{}
        foreach ($rgn in @('titleBare', 'stripBare')) {
            $deltas[$rgn] = Get-MeanDelta $measuredByCase[$pair.a][$rgn] $measuredByCase[$pair.b][$rgn]
        }
        $materials.Add([ordered]@{
            layer = 'material'
            rule = "solid against frosted on the desktop-matching theme under $($pair.what), reported only: the tint composites this palette to within the threshold of the opaque shade"
            a = $pair.a; b = $pair.b; mustDiffer = $null
            deltas = $deltas; maxDelta = ($deltas.Values | Measure-Object -Maximum).Maximum
            needs = $ChannelDelta; asserted = $false; verdict = 'not asserted'
        })
        Write-Host ("    $($pair.a) vs $($pair.b): " +
                    (($deltas.Keys | ForEach-Object { "$_=$($deltas[$_])" }) -join ', ') + ' [reported only: matching-theme composite]')
    }

    # ---- the theme layer ---------------------------------------------------
    #
    # Same config, both halves of the pair. This is the axis #792 added, and
    # it gets its own comparator for the solid frame because the composite
    # excuse the material layer leans on does not apply there: under
    # window-theme=wintty a solid frame is PAINTED from the theme
    # (RootBackgroundResolver answers the shell-theme background directly, not
    # a blend), and the staged pair's backgrounds (#f4f6fb, #131620) are more
    # than 140 counts apart in every channel. A build where the two themes
    # paint the same solid wintty chrome has not applied the theme, full stop.
    #
    # Under High Contrast the frame is pinned and the layer stands down and
    # reports, exactly like the material one.
    function Compare-ThemeAxis([string]$A, [string]$B, [string]$Rule) {
        if (-not $measuredByCase.ContainsKey($A) -or -not $measuredByCase.ContainsKey($B)) {
            $caseErrors.Add("theme '$Rule' needs both '$A' and '$B' and one of them did not run")
            return
        }
        $deltas = [ordered]@{}
        $maxDelta = -1
        foreach ($rgn in @('titleBare', 'stripBare')) {
            $d = Get-MeanDelta $measuredByCase[$A][$rgn] $measuredByCase[$B][$rgn]
            $deltas[$rgn] = $d
            if ($d -gt $maxDelta) { $maxDelta = $d }
        }
        $verdict = if ($maxDelta -ge $ChannelDelta) { 'ok' } else { 'failed' }
        $materials.Add([ordered]@{
            layer = 'theme'; rule = $Rule; a = $A; b = $B; mustDiffer = $true
            deltas = $deltas; maxDelta = $maxDelta; needs = $ChannelDelta
            asserted = (-not $highContrast); verdict = $verdict
        })
        Write-Host ("    theme $Rule ($A vs $B): maxDelta=$maxDelta needs=$ChannelDelta -> $verdict" +
                    $(if ($highContrast) { ' [reported only: High Contrast]' } else { '' }))
        if ($highContrast -or $verdict -eq 'ok') { return }
        $findings.Add(("theme is not reaching the chrome: '$A' and '$B' differ only in theme and paint " +
                       "the same chrome (max channel delta $maxDelta, needs $ChannelDelta; " +
                       (($deltas.Keys | ForEach-Object { "$_=$($deltas[$_])" }) -join ', ') +
                       "). Under window-theme=wintty a solid frame is painted from the theme's own " +
                       'background rather than composited, so a light and a dark palette measuring alike ' +
                       'means neither reached the chrome'))
    }

    Write-Host '--- theme'
    Compare-ThemeAxis 'wt-solid' 'wt-solid-alt' 'the light and dark halves of the pair paint different wintty chrome'
    # Frosted chrome IS a composite, so its theme comparison goes through the
    # material layer, where the palette-tinted estimate can still say a pair
    # was never meant to be distinguishable.
    Compare-Material 'wt-frosted' 'wt-frosted-alt' 'the light and dark halves of the pair tint the frosted wintty chrome differently' $true 'theme'
    # Under window-theme=system the chrome hue is the desktop's, so the theme
    # is not asserted on: measured for the record, because a theme that moved
    # the desktop-driven chrome is worth seeing in the artifact even though
    # nothing here promises it cannot.
    foreach ($pair in @(@('sys-solid', 'sys-solid-alt'), @('sys-frosted', 'sys-frosted-alt'))) {
        if (-not $measuredByCase.ContainsKey($pair[0]) -or -not $measuredByCase.ContainsKey($pair[1])) { continue }
        $deltas = [ordered]@{}
        foreach ($rgn in @('titleBare', 'stripBare')) {
            $deltas[$rgn] = Get-MeanDelta $measuredByCase[$pair[0]][$rgn] $measuredByCase[$pair[1]][$rgn]
        }
        $materials.Add([ordered]@{
            layer = 'theme'
            rule = 'theme under window-theme=system, reported only: the chrome hue is the desktop''s, not the palette''s'
            a = $pair[0]; b = $pair[1]; mustDiffer = $null
            deltas = $deltas; maxDelta = ($deltas.Values | Measure-Object -Maximum).Maximum
            needs = $ChannelDelta; asserted = $false; verdict = 'not asserted'
        })
        Write-Host ("    theme $($pair[0]) vs $($pair[1]): " +
                    (($deltas.Keys | ForEach-Object { "$_=$($deltas[$_])" }) -join ', ') + ' [reported only]')
    }

    # frosted against crystal, both ways round, MEASURED and never asserted.
    # They are one material: there is one SystemBackdrop per window and both
    # values mean "reveal it". A harness that asserted they differ would fail
    # on a correct build, and one that asserted they match would be asserting
    # an implementation detail nobody promised.
    foreach ($pair in @(@('sys-frosted', 'sys-crystal'), @('wt-frosted', 'wt-crystal'))) {
        if (-not $measuredByCase.ContainsKey($pair[0]) -or -not $measuredByCase.ContainsKey($pair[1])) { continue }
        $deltas = [ordered]@{}
        foreach ($rgn in @('titleBare', 'stripBare')) {
            $deltas[$rgn] = Get-MeanDelta $measuredByCase[$pair[0]][$rgn] $measuredByCase[$pair[1]][$rgn]
        }
        $materials.Add([ordered]@{
            rule = 'frosted against crystal, reported only: they are one frame material'
            a = $pair[0]; b = $pair[1]; mustDiffer = $null
            deltas = $deltas; maxDelta = ($deltas.Values | Measure-Object -Maximum).Maximum
            needs = $ChannelDelta; asserted = $false; verdict = 'not asserted'
        })
        Write-Host ("    $($pair[0]) vs $($pair[1]): " +
                    (($deltas.Keys | ForEach-Object { "$_=$($deltas[$_])" }) -join ', ') + ' [reported only]')
    }

    # The verdict is worth nothing if the detector never demonstrated it works.
    # Asserted rather than left to the ordering above, because the ordering is a
    # property of the code and the code is editable.
    if (-not $detectorProven) {
        throw ('HARVEST_MISS: the controls never completed, so nothing here shows the detector works ' +
               'and every result above is unverified')
    }
}
finally {
    # Restored BEFORE the sweep, so a throw out of the sweep cannot leave the
    # caller's environment pointing at a staging directory this block is about
    # to delete.
    if ($originalXdgSet) { $env:XDG_CONFIG_HOME = $originalXdg }
    else { Remove-Item Env:XDG_CONFIG_HOME -ErrorAction SilentlyContinue }
    if ($originalNoColorSet) { $env:NO_COLOR = $originalNoColor }
    else { Remove-Item Env:NO_COLOR -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force -LiteralPath $stage -ErrorAction SilentlyContinue

    $crashGrew = (Test-Path $crashPath) -and ((Get-Item $crashPath).LastWriteTimeUtc -gt $crashStamp)
    if ($crashGrew) { $findings.Add('crash.log grew during the run') }

    # Written from the finally so the report survives a throw from anywhere
    # above it. The exit code is still decided below, on the paths that have one
    # to decide.
    [ordered]@{
        seed = $Seed
        randomCases = $Random
        desktopPolarity = $polarity
        highContrast = $highContrast
        materialAsserted = (-not $highContrast)
        themeCatalogueCount = $catalogue.Count
        spanningTheme = $spanTheme
        altTheme = $altTheme
        # The theme axis's staging, recorded so a run can be audited for which
        # names the launched processes could actually resolve.
        themeStaging = [ordered]@{
            themesDir = $themesDir
            staged = @('wintty-light', 'wintty-dark')
            configFile = $configPath
            catalogueUnderStaging = $catalogue
        }
        contrastFloor = $ContrastFloor
        channelDelta = $ChannelDelta
        # The model the material layer judges a failed comparison by. Recorded
        # in full because the estimate is the difference between a defect and a
        # run nobody can judge, and a report that only carried the verdict left
        # the last false positive here diagnosable by re-capturing the two
        # configs by hand and nothing else.
        backdropGround = [ordered]@{
            source = 'mirrors Ghostty.Core.Shell.BackdropGround.Estimate'
            systemBase = (Format-Rgb $(if ($polarity -eq 'dark') { $SystemBaseDark } else { $SystemBaseLight }))
            opaqueChrome = (Format-Rgb $(if ($polarity -eq 'dark') { $OpaqueChromeDark } else { $OpaqueChromeLight }))
            tintOpacity = $DefaultTintOpacity
            paletteRead = 'the terminal body of each case, as launched'
        }
        borderInset = [ordered]@{ x = $BorderInsetX; top = $BorderInsetTop; bottom = $BorderInsetBottom }
        settleMs = $SettleMs
        detectorProven = $detectorProven
        controls = $controls
        themePreviewPipe = $pipeReport
        material = $materials
        cases = $rows
        crashGrew = $crashGrew
        findings = $findings
        caseErrors = $caseErrors
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir 'result.json')
    Write-Host (Get-Content (Join-Path $OutDir 'result.json') -Raw)
}

if ($findings.Count -gt 0) {
    Write-Host 'PRODUCT_FAIL:' -ForegroundColor Red
    $findings | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    if ($caseErrors.Count -gt 0) {
        Write-Host 'also, cases that could not run:' -ForegroundColor Yellow
        $caseErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
    Write-Host "replay with -Seed $Seed" -ForegroundColor Red
    exit 2
}
if ($caseErrors.Count -gt 0) {
    Write-Host 'cases that could not run, so their area is untested:' -ForegroundColor Yellow
    $caseErrors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    Write-Host "replay with -Seed $Seed" -ForegroundColor Yellow
    exit 1
}
Write-Host "clean (seed $Seed)" -ForegroundColor Green
exit 0
