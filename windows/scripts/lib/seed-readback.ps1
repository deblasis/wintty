#requires -Version 7
# Deciding whether a line the harness typed actually reached the terminal.
#
# Lives here rather than inside search-fuzz.ps1 so it can be exercised without
# a window, a build or an interactive desktop. These rules decide whether a
# run's corpus is real, and a run measuring its oracle against a corpus that
# was never typed reports findings that are not there and passes over ones
# that are - which is worth testing directly rather than only through a GUI
# harness that cannot run in CI.

# Non-overlapping occurrence count. The default folds case because that is what
# the search oracle needs: search is ASCII case-insensitive
# (src/terminal/search/sliding_window.zig uses std.ascii.indexOfIgnoreCase) and
# the sliding window advances past each hit the same way. The seed read-back
# passes Ordinal instead - a line that came back in a different case did not
# come back.
function Measure-Occurrences([string]$haystack, [string]$needle,
                             [StringComparison]$comparison = [StringComparison]::OrdinalIgnoreCase) {
    if ([string]::IsNullOrEmpty($needle)) { return 0 }
    $n = 0; $i = 0
    while ($true) {
        $j = $haystack.IndexOf($needle, $i, $comparison)
        if ($j -lt 0) { break }
        $n++; $i = $j + $needle.Length
    }
    return $n
}

# Did this send put one more copy of the seed line into the document?
#
# Returns 'landed', 'missing' or 'unreadable'. The third is not a nicety.
# Get-TerminalText turns any UIA fault into an empty string, so without it an
# unreadable BEFORE silently rewrites the question from "did the count rise"
# into "is the text present anywhere" - and the emit op retypes the same line
# every few iterations, so from the second one on the answer to that question
# is always yes and a send that landed nothing reads as success. An empty
# document is a fault in practice: the shell has printed a prompt by the time
# anything is seeded, and the document is untrimmed.
#
# Equality against a row is not on offer. The shell owns that row and repaints
# it with a prompt in front and syntax colours over the top, and a long line
# wraps; a check that fired on a legitimate repaint would be worse than no
# check, because it would be switched off. A rising containment count is the
# strongest thing that survives all of that.
#
# What it does NOT catch, stated because the previous wording claimed
# otherwise: a doubled first or last character still contains the needle, and
# so does the whole payload typed twice. A PSReadLine inline prediction can
# also draw the rest of a matching history entry into the grid before it is
# typed, which is why the harness turns prediction off rather than relying on
# this to notice.
function Test-SeedLanded([string]$before, [string]$after, [string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return 'landed' }
    if ([string]::IsNullOrEmpty($before) -or [string]::IsNullOrEmpty($after)) { return 'unreadable' }

    $ord = [StringComparison]::Ordinal
    if ((Measure-Occurrences $after $text $ord) -gt (Measure-Occurrences $before $text $ord)) {
        return 'landed'
    }
    return 'missing'
}
