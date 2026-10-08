$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\Northpass\Northpass.csproj'
$output = Join-Path $root 'dist\Northpass'
Write-Host 'Publishing Northpass for Windows x64...'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
New-Item -ItemType Directory -Force (Join-Path $output 'profiles') | Out-Null
Copy-Item (Join-Path $root 'profiles\*.json') (Join-Path $output 'profiles') -Force
Write-Host "Completed: $output"
Write-Host 'winws2.exe and its dependencies must be supplied separately from the official distribution.'
