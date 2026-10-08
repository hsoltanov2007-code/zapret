param([switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    $project = Join-Path $root 'src\Northpass.App\Northpass.App.csproj'
    $output = Join-Path $root 'dist\Northpass'
    Write-Host 'Publishing Northpass 0.2 for Windows x64...'
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'THIRD_PARTY_NOTICES.md'), (Join-Path $root 'CHANGELOG.md') $output -Force
    Copy-Item (Join-Path $root 'docs') (Join-Path $output 'docs') -Recurse -Force
    Get-ChildItem $output -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
        $relative = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
    } | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    if ($Installer) {
        $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        $iscc = if ($compiler) { $compiler.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
        if (!(Test-Path $iscc)) { throw 'Install Inno Setup 6 from https://jrsoftware.org/isinfo.php, then rerun with -Installer.' }
        & $iscc (Join-Path $root 'installer\Northpass.iss')
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed: $LASTEXITCODE" }
    }
    Write-Host "Completed: $output\Northpass.exe"
    Write-Host 'Zapret2 must be installed separately from the official distribution. Windows runtime acceptance tests are required before release.'
} finally { Pop-Location }
