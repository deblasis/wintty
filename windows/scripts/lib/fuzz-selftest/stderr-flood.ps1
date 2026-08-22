#requires -Version 7
# More than a pipe buffer holds, on stderr, and then a clean exit.
#
# A runner that drains only the stream it reads back leaves this child blocked
# in its own write: the pipe fills at 64 KB, nobody empties it, and the harness
# never reaches its exit. The budget kills it, the row reads "could not run",
# and a build with nothing wrong with it is reported as untested. Everything
# else in this directory says its piece in a line or two, so nothing else here
# can fill anything - and a real harness driving a GUI for minutes has plenty
# to say on stderr.
#
# Written through [Console]::Error rather than Write-Error, which would be a
# non-terminating error record and would end the run under this suite's
# $ErrorActionPreference instead of filling the pipe.
param([string]$ExePath, [Parameter(Mandatory)][string]$OutDir)

$line = 'y' * 250
for ($i = 0; $i -lt 1000; $i++) { [Console]::Error.WriteLine("$i $line") }
Write-Host 'selftest: stderr flood'
exit 0
