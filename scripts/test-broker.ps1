# Real medium-integrity WPF host -> protected native elevated bootstrap -> managed
# privileged worker -> reviewed native driver. Handoff replaces only the interactive
# UAC click, not token/ACL/image/hash/IPC checks. No Windows policy is changed.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$install = Join-Path $env:ProgramFiles ('Northpass-Broker-Acceptance-' + [guid]::NewGuid().ToString('N'))
$results = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Force $install,$results | Out-Null
Copy-Item (Join-Path $root 'dist/Northpass/*') $install -Recurse -Force
function Seal-Installation([string]$Directory) {
    & "$env:SystemRoot/System32/icacls.exe" $Directory /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /T | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Protected application ACL failed.' }
    & "$env:SystemRoot/System32/icacls.exe" $Directory /setowner '*S-1-5-32-544' /T | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Protected application ownership failed.' }
}
try {
    Seal-Installation $install
    $bootstrap = Join-Path $install 'broker/Northpass.Broker.exe'
    $prepare = Start-Process $bootstrap -ArgumentList '--install' -PassThru -Wait
    if ($prepare.ExitCode -ne 0) { throw "Broker offline installation failed: $($prepare.ExitCode)" }
    foreach ($mode in @('native','flowseal','repair','replay')) {
        if ($mode -eq 'repair') {
            $nativeRoot = Join-Path $env:ProgramFiles 'Northpass-Native-0.3'
            $selected = (Get-Content (Join-Path $nativeRoot 'selection.json') -Raw | ConvertFrom-Json).Current
            $damaged = Join-Path $nativeRoot ($selected + '/bin/WinDivert.dll')
            [System.IO.File]::WriteAllBytes($damaged, [byte[]]@(0))
        }
        $handoff = Join-Path $env:TEMP ('northpass-handoff-' + [guid]::NewGuid().ToString('N') + '.json')
        $evidence = Join-Path $results ('broker-' + $mode + '.json')
        $fixture = Join-Path $root 'tests/Northpass.TestChild/bin/Release/net8.0/Northpass.TestChild.dll'
        $arguments = "--broker-check --broker-mode $mode --broker-test-handoff `"$handoff`" --evidence `"$evidence`""
        $launcher = Start-Process dotnet -ArgumentList @("`"$fixture`"",'medium-launch',"`"$(Join-Path $install 'Northpass.exe')`"","`"$($arguments.Replace('"','\"'))`"") -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (!(Test-Path $handoff)) {
            if ($launcher.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Actual medium-integrity UI did not publish its broker request.' }
            Start-Sleep -Milliseconds 50
        }
        $owner = Get-Content $handoff -Raw | ConvertFrom-Json
        $helper = Start-Process $bootstrap -ArgumentList @('--owner',$owner.Owner,'--created',$owner.Created,'--pipe',$owner.Pipe,'--test-no-traffic') -PassThru
        Set-Content ($handoff + '.pid') $helper.Id -NoNewline
        if (!$launcher.WaitForExit(120000)) { throw 'Medium UI broker acceptance timed out.' }
        if ($launcher.ExitCode -ne 0) { $errorText = if (Test-Path $evidence) { Get-Content $evidence -Raw } else { 'No UI evidence was written.' }; throw "Medium UI failed: $errorText" }
        if (!$helper.WaitForExit(15000) -or ($mode -ne 'replay' -and $helper.ExitCode -ne 0) -or ($mode -eq 'replay' -and $helper.ExitCode -eq 0)) { throw 'Owned elevated broker did not clean up successfully.' }
        $proof = Get-Content $evidence -Raw | ConvertFrom-Json
        if ($proof.UiAdministrator -or !$proof.AuthenticatedElevatedWorker -or $proof.Ipv6UnchangedDatagrams -ne 64 -or !$proof.Metrics.KernelLossUnknown) { throw 'Real privilege/network evidence is invalid.' }
        Write-Host "::notice title=Broker $mode integration::Actual medium-integrity published WPF host authenticated the elevated native bootstrap/worker, verified offline components, forwarded 64 unchanged dedicated ::1 UDP datagrams, read numeric metrics and shut down owned children. Flowseal used filter=false. UAC interaction was replaced by a pre-elevated CI handoff; interactive approval/denial remains manual."
        Remove-Item $handoff,($handoff+'.pid') -Force
    }
} catch {
    $message = $_.ToString().Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A')
    Write-Host "::error title=Privileged broker acceptance failed::$message"
    throw
} finally {
    # Remove only the private app checkout after owned processes have exited.
    if (Test-Path $install) { Remove-Item $install -Recurse -Force }
}
