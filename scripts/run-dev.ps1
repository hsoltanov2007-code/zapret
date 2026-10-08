$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator before running this script. dotnet run cannot request elevation for the WinDivert application.'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    dotnet run --project 'src\Northpass.App\Northpass.App.csproj'
    if ($LASTEXITCODE -ne 0) { throw "Northpass exited with code $LASTEXITCODE" }
} finally { Pop-Location }
