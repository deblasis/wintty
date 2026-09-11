# Fixture: an inline hashtable operand inside a composition (R4-1).
$a = $b + @{}
$b = $a + @{ FilePath = 'C:\wt\rev1084-att\Wintty.exe' }
Start-Process @a
