$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    dotnet restore tests/Northpass.Tests/Northpass.Tests.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Portable test restore failed.' }
    dotnet test tests/Northpass.Tests/Northpass.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Portable tests failed.' }
    dotnet restore tests/Northpass.Windows.Tests/Northpass.Windows.Tests.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Windows test restore failed.' }
    dotnet test tests/Northpass.Windows.Tests/Northpass.Windows.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Windows UI smoke test failed.' }
} finally { Pop-Location }
