# Fixture: a launch variable the scan cannot resolve: fail closed.
$app = Get-BuiltApp
Start-Process -FilePath $app
