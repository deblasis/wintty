# Fixture: the base + inline composition shape (r4-8), now correct by
# design: the inline operand itself is followed, not a window overrun.
$base = @{}
$sp = $base + @{ FilePath = 'C:\wt\rev1084-att\Wintty.exe' }
Start-Process @sp
