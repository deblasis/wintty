#requires -Version 7
<#
    The contrast oracle's measuring instrument: WCAG ratio math, the
    rendered-pixel sampler, and the named thresholds every caller is held to.

    Why pixels and not resolved brushes. The chrome has regressed twice.
    The second time, the brush VALUES were right and the strip still painted
    a selected-row title at 1.11:1, because the code path that hands the tab
    hosts the terminal's colours never ran in a session with no config
    reload. A checker that reads resolved brushes would have measured the
    correct value of a colour nothing was painting with, and passed. So the
    instrument here reads back the same pixels a photograph of the screen
    would: composition, the Mica backdrop, opacity blending and every
    fallback path are only visible in the rendered result.

    Dot-source it:

        . (Join-Path $PSScriptRoot 'lib/contrast.ps1')
#>

Add-Type -AssemblyName System.Drawing

# One implementation of the WCAG math and one of the sampler, both in C# so
# a region scan is a LockBits pass rather than tens of thousands of
# GetPixel calls. PowerShell asks this class for the ratio it prints, so
# the number in the report and the number in the verdict cannot drift.
# Guarded rather than -ErrorAction SilentlyContinue: the guard makes a second
# dot-source a no-op, and suppression would have swallowed a real compile
# error and left every caller failing on a missing type instead.
#
# System.Collections is listed explicitly because naming any
# -ReferencedAssemblies at all replaces PowerShell's default set, so
# Dictionary<,> stops resolving the moment the drawing assemblies are added.
if (-not ('ContrastSampler' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Collections -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

public static class ContrastMath {
    // WCAG 2.1 relative luminance of an sRGB colour (the same formula the
    // zig palette test uses in src/config/wintty_theme_test.zig, so the
    // rendered oracle and the palette oracle agree on what a ratio means).
    public static double Luminance(int r, int g, int b) {
        double[] ch = new double[3];
        int[] raw = new int[] { r, g, b };
        for (int i = 0; i < 3; i++) {
            double c = raw[i] / 255.0;
            ch[i] = (c <= 0.03928) ? (c / 12.92) : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
    }

    public static double Ratio(int r1, int g1, int b1, int r2, int g2, int b2) {
        double la = Luminance(r1, g1, b1);
        double lb = Luminance(r2, g2, b2);
        double hi = Math.Max(la, lb);
        double lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }
}

// What one sampled region reports back. Ok=false with a Why is a failure to
// MEASURE, which is an environment verdict; it is never a contrast verdict,
// because a region the sampler could not read is a region nothing is known
// about.
public class ContrastSample {
    public int BgR, BgG, BgB;
    public int FgR, FgG, FgB;
    public int BgCount, FgCount, Total;
    public double BgShare;
    public bool Ok = true;
    public string Why = "";
    public double Ratio {
        get { return ContrastMath.Ratio(FgR, FgG, FgB, BgR, BgG, BgB); }
    }
    public string BgHex { get { return string.Format("#{0:X2}{1:X2}{2:X2}", BgR, BgG, BgB); } }
    public string FgHex { get { return string.Format("#{0:X2}{1:X2}{2:X2}", FgR, FgG, FgB); } }
}

public static class ContrastSampler {
    // Colours are bucketed 4 levels per channel before they are counted.
    // Two effects make an exact-RGB histogram the wrong instrument here:
    // the Mica backdrop carries a per-pixel noise texture, which spreads
    // one visual colour over dozens of neighbouring RGB values, and
    // ClearType paints sub-pixel fringes, which does the same to a glyph.
    // A bucket is wide enough to gather either back into one cluster and
    // narrow enough that a real foreground and a real background never
    // land in the same one.
    const int BucketShift = 2;

    // The smallest cluster that may be called the foreground. Below this a
    // sample is reading a stray pixel rather than painted ink. Anti-aliased
    // EDGE pixels are intermediate in luminance by construction, so they
    // lose the max-ratio search to the glyph core whenever a core exists;
    // this floor is what stops a single pixel winning when it does not.
    //
    // Four, not eight. A small glyph drawn at 70% opacity -- a group's
    // member count, a chevron -- puts only a handful of pixels at its true
    // colour, and an eight-pixel floor read those as "nothing painted" and
    // reported a legible count as a 1.02:1 failure.
    public const int MinClusterPx = 4;

    // A text element is mostly background: glyphs are a minority of its
    // area. If the largest cluster is not a clear plurality, the region is
    // not the flat-backed element the caller thinks it located (a rect
    // that spilled across a boundary, a gradient, a half-drawn frame), and
    // the honest answer is that nothing was measured.
    public const double MinBgShare = 0.20;

    public static ContrastSample Region(Bitmap bmp, int x, int y, int w, int h) {
        var sample = new ContrastSample();
        if (w <= 0 || h <= 0) { sample.Ok = false; sample.Why = "empty rect"; return sample; }
        if (x < 0 || y < 0 || x + w > bmp.Width || y + h > bmp.Height) {
            sample.Ok = false;
            sample.Why = string.Format("rect {0},{1} {2}x{3} is outside the {4}x{5} capture",
                x, y, w, h, bmp.Width, bmp.Height);
            return sample;
        }

        var counts = new Dictionary<int, long[]>();  // bucket -> {n, sumR, sumG, sumB}
        var data = bmp.LockBits(new Rectangle(x, y, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            unsafe {
                byte* baseP = (byte*)data.Scan0;
                for (int row = 0; row < h; row++) {
                    byte* p = baseP + row * data.Stride;
                    for (int col = 0; col < w; col++) {
                        int b = p[0], g = p[1], r = p[2];
                        p += 4;
                        int key = ((r >> BucketShift) << 16)
                                | ((g >> BucketShift) << 8)
                                | (b >> BucketShift);
                        long[] slot;
                        if (!counts.TryGetValue(key, out slot)) {
                            slot = new long[4];
                            counts[key] = slot;
                        }
                        slot[0]++; slot[1] += r; slot[2] += g; slot[3] += b;
                    }
                }
            }
        } finally {
            bmp.UnlockBits(data);
        }

        sample.Total = w * h;

        // The background is the plurality cluster. Its reported colour is
        // the cluster's MEAN, not a bucket midpoint: the mean is a colour
        // that was actually on screen.
        long best = -1; int bestKey = 0;
        foreach (var kv in counts) {
            if (kv.Value[0] > best) { best = kv.Value[0]; bestKey = kv.Key; }
        }
        if (best <= 0) { sample.Ok = false; sample.Why = "no pixels"; return sample; }
        var bg = counts[bestKey];
        sample.BgR = (int)(bg[1] / bg[0]);
        sample.BgG = (int)(bg[2] / bg[0]);
        sample.BgB = (int)(bg[3] / bg[0]);
        sample.BgCount = (int)bg[0];
        sample.BgShare = (double)bg[0] / sample.Total;
        if (sample.BgShare < MinBgShare) {
            sample.Ok = false;
            sample.Why = string.Format(
                "no dominant background: the largest cluster is {0:P0} of the region, under the {1:P0} floor",
                sample.BgShare, MinBgShare);
            return sample;
        }

        // The foreground is the cluster, of a size that can only be paint,
        // that sits furthest from the background in WCAG terms. For a text
        // element that is the glyph core; for a separator stroke it is the
        // stroke.
        double bestRatio = -1; long[] fg = null;
        foreach (var kv in counts) {
            if (kv.Key == bestKey) continue;
            if (kv.Value[0] < MinClusterPx) continue;
            int r = (int)(kv.Value[1] / kv.Value[0]);
            int g = (int)(kv.Value[2] / kv.Value[0]);
            int b = (int)(kv.Value[3] / kv.Value[0]);
            double ratio = ContrastMath.Ratio(r, g, b, sample.BgR, sample.BgG, sample.BgB);
            if (ratio > bestRatio) { bestRatio = ratio; fg = new long[] { kv.Value[0], r, g, b }; }
        }
        if (fg == null) {
            // A flat region. For a text surface this IS the finding the
            // caller wants to hear about (nothing was painted on it, or it
            // was painted in the background colour), so it comes back as a
            // measured 1.00 rather than as a failure to measure.
            sample.FgR = sample.BgR; sample.FgG = sample.BgG; sample.FgB = sample.BgB;
            sample.FgCount = 0;
            return sample;
        }
        sample.FgCount = (int)fg[0];
        sample.FgR = (int)fg[1]; sample.FgG = (int)fg[2]; sample.FgB = (int)fg[3];
        return sample;
    }

    // The mean colour of a region, for a caller that wants a background
    // read from a deliberately text-free band rather than inferred from
    // the plurality of a region that has text in it.
    public static ContrastSample Flat(Bitmap bmp, int x, int y, int w, int h) {
        var whole = Region(bmp, x, y, w, h);
        if (!whole.Ok) return whole;
        whole.FgR = whole.BgR; whole.FgG = whole.BgG; whole.FgB = whole.BgB;
        whole.FgCount = 0;
        return whole;
    }
}
'@ -CompilerOptions '/unsafe'
}

# ---- thresholds ------------------------------------------------------------
#
# Every constant below is named and carries the rule it comes from. A
# threshold with no source is a number somebody liked.

# WCAG 2.1 SC 1.4.3 Contrast (Minimum), normal-size text. Anything a user
# reads as words -- tab titles, group titles and counts, run labels,
# switcher tile text, the terminal's own foreground -- clears this against
# the background it is painted on. This is the same 4.5 the built-in
# palette is held to in src/config/wintty_theme_test.zig.
$script:CONTRAST_TEXT_AA = 4.5

# WCAG 2.1 SC 1.4.11 Non-text Contrast. Graphical objects needed to
# understand the control: the close X, the group chevron, a tab icon. Held
# to 3:1, not 4.5:1, because they are shapes rather than words. The chrome
# glyphs measured 1.72-1.87:1 in the regression this oracle exists to
# catch, so the gap is not academic.
$script:CONTRAST_NONTEXT = 3.0

# The palette test's distinguishability rule for fills
# (src/config/wintty_theme_test.zig: `contrast(background, color) > 1.2`).
# A separator stroke or a selection fill has the opposite job to text: it
# does not have to be readable, it has to be VISIBLE against what it sits
# on. Strictly greater, the way the palette test writes it.
$script:CONTRAST_FILL_VISIBLE = 1.2

function Get-ContrastRatio([int[]]$A, [int[]]$B) {
    return [ContrastMath]::Ratio($A[0], $A[1], $A[2], $B[0], $B[1], $B[2])
}

# The rule each surface class is judged by, in one place so the report and
# the verdict quote the same source string.
function Get-ContrastRule([string]$Class) {
    switch ($Class) {
        'text'    { return @{ Min = $script:CONTRAST_TEXT_AA;      Source = 'WCAG AA text (1.4.3)' } }
        'glyph'   { return @{ Min = $script:CONTRAST_NONTEXT;      Source = 'WCAG non-text (1.4.11)' } }
        'fill'    { return @{ Min = $script:CONTRAST_FILL_VISIBLE; Source = 'palette fill rule (>1.2)' } }
        default   { throw "contrast: unknown surface class '$Class'" }
    }
}

# 'fill' is the one strict comparison: the palette test writes it as
# `> 1.2`, and a fill exactly at the floor is not distinguishable.
function Test-ContrastPasses([double]$Ratio, [string]$Class) {
    $rule = Get-ContrastRule $Class
    if ($Class -eq 'fill') { return $Ratio -gt $rule.Min }
    return $Ratio -ge $rule.Min
}
