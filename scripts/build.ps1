param(
    [switch]$Installer,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SignCertificateThumbprint,
    [ValidatePattern('^https://')][string]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$signer = $null
if ($SignCertificateThumbprint) {
    $signer = (Get-Command signtool.exe -ErrorAction Stop).Source
}
function Sign-NorthpassFile([string]$File) {
    if (!$signer) { return }
    & $signer sign /sha1 $SignCertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $File
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $File" }
    & $signer verify /pa $File
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $File" }
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    $project = Join-Path $root 'src\Northpass.App\Northpass.App.csproj'
    $output = Join-Path $root 'dist\Northpass'
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    Write-Host 'Publishing Northpass 0.6 for Windows x64...'
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'THIRD_PARTY_NOTICES.md'), (Join-Path $root 'CHANGELOG.md') $output -Force
    $docsOutput = Join-Path $output 'docs'
    New-Item -ItemType Directory -Force $docsOutput | Out-Null
    Copy-Item (Join-Path $root 'docs\*') $docsOutput -Recurse -Force
    # Self-contained .NET notices must accompany the exact restored runtime packs.
    $assets = Get-Content (Join-Path $root 'src\Northpass.App\obj\project.assets.json') -Raw | ConvertFrom-Json
    $packageRoots = $assets.packageFolders.PSObject.Properties.Name
    $runtimePacks = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -like '*.Runtime.win-x64' })
    if ($runtimePacks.Count -lt 2) { throw 'Could not identify both .NET and Windows Desktop runtime packs for license preservation.' }
    foreach ($pack in $runtimePacks) {
        $version = $pack.version.Trim('[', ']').Split(',')[0].Trim()
        $packagePath = $null
        foreach ($packageRoot in $packageRoots) {
            $candidate = Join-Path $packageRoot ($pack.name.ToLowerInvariant() + '/' + $version)
            if (Test-Path $candidate) { $packagePath = $candidate; break }
        }
        if (!$packagePath) { throw "Runtime pack is missing: $($pack.name) $version" }
        $noticeOutput = Join-Path $docsOutput ('licenses/dotnet/' + $pack.name + '/' + $version)
        New-Item -ItemType Directory -Force $noticeOutput | Out-Null
        $notices = @(Get-ChildItem $packagePath -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -like 'THIRD-PARTY-NOTICES*' })
        if ($notices.Count -eq 0) { throw "Runtime license was not found: $($pack.name)" }
        $notices | Copy-Item -Destination $noticeOutput -Force
    }
    # Every distributable includes the selected reviewed engine plus licences and corresponding sources.
    python (Join-Path $root 'scripts/prepare-engine.py') --output $output
    if ($LASTEXITCODE -ne 0) { throw 'Reviewed offline engine packaging failed; installer build is blocked.' }
    # Sign only Northpass's own PE files. Reviewed upstream bytes must stay exact.
    Get-ChildItem $output -File | Where-Object { $_.Name -eq 'Northpass.exe' -or $_.Name -like 'Northpass*.dll' } | ForEach-Object { Sign-NorthpassFile $_.FullName }
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
        Sign-NorthpassFile (Join-Path $root 'dist/installer/Northpass-0.6.0-win-x64-setup.exe')
    }
    Write-Host "Completed: $output\Northpass.exe"
    Write-Host 'Reviewed Flowseal offline payload and third-party sources are bundled. Clean-machine Windows acceptance is required before release.'
} finally { Pop-Location }
