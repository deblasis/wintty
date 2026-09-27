# A timing state machine variable named $start (config-gap-measure.ps1
# shape): never a launch, but the bare-alias verb once matched the word
# right after the sigil because $ is a non-word character.
$start = -1L
$starts = [System.Collections.Generic.List[double]]::new()
if ($state -eq 1 -and $start -lt 0) { $start = $t }
elseif ($state -ne 1 -and $start -ge 0) {
    $starts.Add(($t - $start) / 10000.0)
    $start = -1L
}
