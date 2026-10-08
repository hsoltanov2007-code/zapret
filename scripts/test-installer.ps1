# Real Windows installer/published-app acceptance. Native idle check captures no traffic.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$install = Join-Path $env:ProgramFiles ('Northpass-Acceptance-' + [guid]::NewGuid().ToString('N'))
$results = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Force $results | Out-Null
$setup = Join-Path $root 'dist/installer/Northpass-0.7.0-win-x64-setup.exe'
$catalogs = @(
    @{ Name = 'flowseal'; Root = 'Northpass-Flowseal' }
)
function Invoke-Checked([string]$File, [string[]]$Arguments, [int]$ExpectedExit = 0) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru
    if (!$process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id -Force
        throw "Acceptance process timed out: $File"
    }
    if ($process.ExitCode -ne $ExpectedExit) { throw "Acceptance exit code $($process.ExitCode), expected ${ExpectedExit}: $File" }
}
try {
    # Inert fixture files exercise actual in-place obsolete-file cleanup.
    New-Item -ItemType Directory -Force (Join-Path $install 'engine-payload'), (Join-Path $install 'profiles'), (Join-Path $install 'docs/third-party-source') | Out-Null
    foreach ($obsolete in @('Northpass.Engine.Zapret2.dll', 'engine-payload/zapret2-offline.zip', 'profiles/zapret2-reviewed-example.json', 'profiles/example-template.json', 'docs/third-party-source/zapret2-1.0.5.2-source.zip')) {
        Set-Content (Join-Path $install $obsolete) 'Legacy product file fixture only; never executable.'
    }
    Invoke-Checked $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$install`"", "/LOG=`"$(Join-Path $results 'installer-install.log')`"")
    $app = Join-Path $install 'Northpass.exe'
    if (!(Test-Path $app)) { throw 'Installer did not install the application.' }
    if ((Get-Item $app).VersionInfo.ProductVersion -notlike '0.7.0*') { throw 'Installed version is incorrect.' }
    foreach ($required in @('engine-payload/flowseal-offline.zip', 'engine-payload/native-offline.zip', 'Northpass.Engine.Native.dll', 'THIRD_PARTY_NOTICES.md', 'docs/third-party-source', 'docs/native-source/NorthpassCore-source.zip', 'docs/native-source/windivert-2.2.2-source.zip', 'docs/licenses/WinDivert-README.txt', 'docs/licenses/dotnet')) {
        if (!(Test-Path (Join-Path $install $required))) { throw "Bundled distribution content is missing: $required" }
    }
    foreach ($removed in @('Northpass.Engine.Zapret2.dll', 'engine-payload/zapret2-offline.zip', 'profiles/zapret2-reviewed-example.json', 'profiles/example-template.json', 'docs/third-party-source/zapret2-1.0.5.2-source.zip')) {
        if (Test-Path (Join-Path $install $removed)) { throw "Legacy product content was installed: $removed" }
    }
    # Product startup and this check share the same mandatory-offline manager and Windows ACL policy.
    Invoke-Checked $app @('--installation-check')
    Invoke-Checked $app @('--native-check')
    Invoke-Checked $app @('--native-ipc-check')
    $nativeManifest = Get-Content (Join-Path $root 'dist/native/native-manifest.json') -Raw | ConvertFrom-Json
    $nativeRoot = Join-Path $env:ProgramFiles 'Northpass-Native-0.2'
    foreach ($component in $nativeManifest.components) {
        $path = Join-Path (Join-Path $nativeRoot $nativeManifest.revision) $component.path
        if ((Get-Item $path).Length -ne $component.size -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $component.sha256 -or !(Get-Acl $path).AreAccessRulesProtected) { throw "Native published installation failed integrity/ACL verification: $($component.path)" }
    }
    $nativeSelection = Join-Path $nativeRoot 'selection.json'
    $nativeActivated = (Get-Item $nativeSelection).LastWriteTimeUtc
    Invoke-Checked $app @('--native-check')
    if ((Get-Item $nativeSelection).LastWriteTimeUtc -ne $nativeActivated) { throw 'Published native bootstrap did not reuse its installed components.' }
    $nativeDll = Join-Path (Join-Path $nativeRoot $nativeManifest.revision) 'bin/WinDivert.dll'
    $nativeSaved = [IO.File]::ReadAllBytes($nativeDll); $nativeAcl = Get-Acl $nativeDll
    try {
        Remove-Item $nativeDll
        Invoke-Checked $app @('--native-check') 1
    } finally {
        [IO.File]::WriteAllBytes($nativeDll, $nativeSaved)
        Set-Acl $nativeDll $nativeAcl
    }
    Invoke-Checked $app @('--native-check')
    foreach ($catalog in $catalogs) {
        $engineRoot = Join-Path $env:ProgramFiles $catalog.Root
        $manifest = Get-Content (Join-Path $root ('engine/catalog/' + $catalog.Name + '.json')) -Raw | ConvertFrom-Json
        $selection = Join-Path $engineRoot 'selection.json'
        $state = Get-Content $selection -Raw | ConvertFrom-Json
        if ($state.Current -ne $manifest.revision) { throw 'Published application installed an unreviewed revision.' }
        foreach ($component in $manifest.components) {
            $path = Join-Path (Join-Path $engineRoot $manifest.revision) $component.path
            if ((Get-Item $path).Length -ne $component.size -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $component.sha256) { throw "Installed component integrity failed: $($component.path)" }
            if (!(Get-Acl $path).AreAccessRulesProtected) { throw "Component permissions were not sealed: $($component.path)" }
        }
        $activated = (Get-Item $selection).LastWriteTimeUtc
        Invoke-Checked $app @('--installation-check')
        if ((Get-Item $selection).LastWriteTimeUtc -ne $activated) { throw 'Subsequent launch did not reuse the installed components.' }
        # Missing content must fail closed through the real published entry point.
        $target = 'bin/tls_clienthello_www_google_com.bin'
        $missing = Join-Path (Join-Path $engineRoot $manifest.revision) $target
        $saved = [IO.File]::ReadAllBytes($missing)
        $acl = Get-Acl $missing
        try {
            Remove-Item $missing
            Invoke-Checked $app @('--installation-check') 1
        } finally {
            [IO.File]::WriteAllBytes($missing, $saved)
            Set-Acl $missing $acl
        }
        Invoke-Checked $app @('--installation-check')
    }
    $evidence = 'One-file installer installed the self-contained x64 app, Flowseal fallback and original native pass-through payloads, licences and corresponding sources; obsolete named fixture files were removed. Published app verified/reused Flowseal and native protected components, rejected missing components, verified restoration, and initialized/stopped the native idle driver through IDpiEngine. No internet traffic or ISP bypass test was performed.'
    Set-Content (Join-Path $results 'installer-evidence.txt') $evidence
    Write-Host "::notice title=Installed application acceptance::$evidence"
} finally {
    $uninstall = Join-Path $install 'unins000.exe'
    if (Test-Path $uninstall) {
        Invoke-Checked $uninstall @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $results 'installer-uninstall.log')`"")
        if (Test-Path (Join-Path $install 'Northpass.exe')) { throw 'Uninstaller left the application executable behind.' }
        Write-Host '::notice title=Uninstall acceptance::The actual silent uninstaller removed the application. Protected engine revisions and user data are retained by design.'
    }
}
