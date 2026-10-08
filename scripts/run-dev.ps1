$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    dotnet run --project 'src\Northpass.App\Northpass.App.csproj'
    if ($LASTEXITCODE -ne 0) { throw "Northpass exited with code $LASTEXITCODE" }
} finally { Pop-Location }
