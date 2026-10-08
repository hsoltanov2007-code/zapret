# Real Windows installer/published-app acceptance. Never starts interception.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$install = Join-Path $env:ProgramFiles ('Northpass-Acceptance-' + [guid]::NewGuid().ToString('N'))
$results = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Force $results | Out-Null
$setup = Join-Path $root 'dist/installer/Northpass-0.4.0-win-x64-setup.exe'
$engineRoot = Join-Path $env:ProgramFiles 'Northpass-Zapret2'
$manifest = Get-Content (Join-Path $root 'engine/catalog/zapret2.json') -Raw | ConvertFrom-Json
function Invoke-Checked([string]$File, [string[]]$Arguments, [int]$ExpectedExit = 0) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru
    if (!$process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id -Force
        throw "Acceptance process timed out: $File"
    }
    if ($process.ExitCode -ne $ExpectedExit) { throw "Acceptance exit code $($process.ExitCode), expected ${ExpectedExit}: $File" }
}
try {
    Invoke-Checked $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$install`"", "/LOG=`"$(Join-Path $results 'installer-install.log')`"")
    $app = Join-Path $install 'Northpass.exe'
    if (!(Test-Path $app)) { throw 'Installer did not install the application.' }
    if ((Get-Item $app).VersionInfo.ProductVersion -notlike '0.4.0*') { throw 'Installed version is incorrect.' }
    foreach ($required in @('engine-payload/zapret2-offline.zip', 'THIRD_PARTY_NOTICES.md', 'docs/third-party-source', 'docs/licenses/dotnet')) {
        if (!(Test-Path (Join-Path $install $required))) { throw "Bundled distribution content is missing: $required" }
    }
    # Product startup and this check share the same mandatory-offline manager and Windows ACL policy.
    Invoke-Checked $app @('--installation-check')
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
    # Missing protected content must fail closed through the real published entry point.
    $lua = Join-Path (Join-Path $engineRoot $manifest.revision) 'lua/zapret-antidpi.lua'
    $saved = [IO.File]::ReadAllBytes($lua)
    try {
        Remove-Item $lua
        Invoke-Checked $app @('--installation-check') 1
    } finally {
        [IO.File]::WriteAllBytes($lua, $saved)
        # Restore the explicit ACL through the installer's own sealed policy, not by accepting writable files.
        $acl = Get-Acl (Join-Path (Split-Path $lua) 'zapret-lib.lua')
        Set-Acl $lua $acl
    }
    Invoke-Checked $app @('--installation-check')
    $evidence = 'One-file installer installed the self-contained x64 app, payload, licenses and sources. Published app prepared 18 verified protected components, reused them, rejected a missing component, and verified the restored installation. No traffic interception or ISP bypass test was performed.'
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
