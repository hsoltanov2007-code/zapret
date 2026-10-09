# Real medium-integrity WPF host -> protected native elevated bootstrap -> managed
# privileged worker -> reviewed native driver. Handoff replaces only the interactive
# UAC click, not token/ACL/image/hash/IPC checks. No Windows policy is changed.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$install = Join-Path $env:ProgramFiles ('Northpass-Broker-Acceptance-' + [guid]::NewGuid().ToString('N'))
$results = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Force $install,$results | Out-Null
$ownedHelpers = @(); $ownedLaunchers = @()
Copy-Item (Join-Path $root 'dist/Northpass/*') $install -Recurse -Force
function Seal-Installation([string]$Directory) {
    $fixture = Join-Path $root 'tests/Northpass.TestChild/bin/Release/net8.0/Northpass.TestChild.dll'
    dotnet $fixture seal-install $Directory
    if ($LASTEXITCODE -ne 0) { throw 'Protected application ownership/ACL sealing failed.' }
}
try {
    Seal-Installation $install
    $bootstrap = Join-Path $install 'broker/Northpass.Broker.exe'
    $prepare = Start-Process $bootstrap -ArgumentList '--install' -PassThru -Wait
    if ($prepare.ExitCode -ne 0) { throw "Broker offline installation failed: $($prepare.ExitCode)" }
    foreach ($mode in @('early-worker-exit','startup-retry','native','flowseal','repair','replay','disconnect-active','parent-death','worker-crash')) {
        if ($mode -eq 'repair') {
            $nativeRoot = Join-Path $env:ProgramFiles 'Northpass-Native-0.4'
            $selected = (Get-Content (Join-Path $nativeRoot 'selection.json') -Raw | ConvertFrom-Json).Current
            $damaged = Join-Path $nativeRoot ($selected + '/bin/WinDivert.dll')
            [System.IO.File]::WriteAllBytes($damaged, [byte[]]@(0))
        }
        $handoff = Join-Path $env:TEMP ('northpass-handoff-' + [guid]::NewGuid().ToString('N') + '.json')
        $evidence = Join-Path $results ('broker-' + $mode + '.json')
        $fixture = Join-Path $root 'tests/Northpass.TestChild/bin/Release/net8.0/Northpass.TestChild.dll'
        $image = Join-Path $install 'Northpass.exe'
        if ($mode -eq 'early-worker-exit') {
            $image = Join-Path $install 'Northpass.OwnerFixture.exe'
            Copy-Item (Join-Path $install 'Northpass.exe') $image
            Seal-Installation $install
        }
        $arguments = "--broker-check --broker-mode $mode --broker-test-handoff `"$handoff`" --evidence `"$evidence`""
        $launcher = Start-Process dotnet -ArgumentList @("`"$fixture`"",'medium-launch',"`"$image`"","`"$($arguments.Replace('"','\"'))`"") -PassThru
        $ownedLaunchers += $launcher
        $previousPipe = ''; $helpers = @()
        $attemptCount = if ($mode -eq 'startup-retry') { 2 } else { 1 }
        for ($attempt = 0; $attempt -lt $attemptCount; $attempt++) {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (!(Test-Path $handoff) -or (Get-Content $handoff -Raw | ConvertFrom-Json).Pipe -eq $previousPipe) {
            if ($launcher.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Actual medium-integrity UI did not publish its broker request.' }
            Start-Sleep -Milliseconds 50
        }
        $owner = Get-Content $handoff -Raw | ConvertFrom-Json
        # Connect a real unrelated process first. The UI must reject its PID and
        # still accept the verified owned worker; no forged AUTH can control it.
        $intruder = $null
        if ($mode -notin @('early-worker-exit','startup-retry')) {
        $intruder = Start-Process dotnet -ArgumentList @("`"$fixture`"",'broker-pipe-intruder',$owner.Pipe) -PassThru -RedirectStandardOutput (Join-Path $results ('broker-intruder-' + $mode + '.txt'))
        Start-Sleep -Milliseconds 500
        }
        $helper = Start-Process $bootstrap -ArgumentList @('--owner',$owner.Owner,'--created',$owner.Created,'--pipe',$owner.Pipe,'--test-no-traffic') -PassThru
        $helpers += $helper; $ownedHelpers += $helper; $previousPipe = $owner.Pipe
        [System.IO.File]::WriteAllText($handoff + '.pid.pending', $helper.Id.ToString())
        [System.IO.File]::Move($handoff + '.pid.pending', $handoff + '.pid', $true)
        if ($intruder -and (!$intruder.WaitForExit(20000) -or $intruder.ExitCode -ne 0)) { throw 'Unauthorized broker peer was not rejected.' }
        if ($mode -eq 'startup-retry' -and $attempt -eq 0) {
            if (!$helper.WaitForExit(15000)) { throw 'Cancelled pre-authentication owned helper survived cleanup.' }
            if (Test-Path (Join-Path $env:ProgramFiles ('Northpass-BrokerData/owner-' + $owner.Pipe))) { throw 'Cancelled startup retained owned session files.' }
        }
        }
        if ($mode -eq 'worker-crash') {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (!(Test-Path ($handoff + '.crash'))) {
                if ($launcher.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Active owned worker crash request missing.' }
                Start-Sleep -Milliseconds 50
            }
            $owned = Get-Content ($handoff + '.crash') -Raw | ConvertFrom-Json
            $peer = Get-CimInstance Win32_Process -Filter "ProcessId=$($owned.Worker)"
            if ($peer.ParentProcessId -ne $helper.Id -or $peer.ExecutablePath -ne (Join-Path $install 'broker/Northpass.Broker.Worker.exe')) { throw 'Crash fixture refuses to stop an unowned process.' }
            Stop-Process -Id $owned.Worker -Force
        }
        if (!$launcher.WaitForExit(120000)) { throw 'Medium UI broker acceptance timed out.' }
        if ($launcher.ExitCode -ne 0) { $errorText = if (Test-Path $evidence) { Get-Content $evidence -Raw } else { 'No UI evidence was written.' }; throw "Medium UI failed: $errorText" }
        if (!$helper.WaitForExit(15000)) { throw 'Owned elevated broker did not clean up successfully.' }
        if ($mode -eq 'early-worker-exit') {
            $proof = Get-Content $evidence -Raw | ConvertFrom-Json
            if ($helper.ExitCode -ne 0x4e500602 -or [int]$proof.Failure.Kind -ne 4 -or [int]$proof.Failure.Stage -ne 6 -or !$proof.Failure.CleanupCompleted -or !$proof.NoEngineStarted -or $proof.Failure.ElapsedMilliseconds -ge 15000) { throw 'Early actual worker-owner rejection was masked or ownership cleanup failed.' }
            Write-Host '::notice title=Pre-authentication worker termination::Actual protected bootstrap/worker rejected a deliberately renamed desktop owner image. Client reported AuthenticationRejected / WorkerOwner, owned exit 0x4e500602 and completed cleanup before the 15-second timeout; no engine started. Deliberate security fixture, not reproduction of the reported desktop cause.'
            Remove-Item $image,$handoff,($handoff+'.pid') -Force -ErrorAction SilentlyContinue
            continue
        }
        if ($mode -in @('replay','worker-crash')) { if ($helper.ExitCode -eq 0) { throw 'Fatal security/crash fixture silently succeeded.' } }
        elseif ($mode -ne 'parent-death' -and $helper.ExitCode -ne 0) { throw "Broker exited unexpectedly: $($helper.ExitCode)" }
        $session = Join-Path $env:ProgramFiles ('Northpass-BrokerData/owner-' + $owner.Pipe)
        if (Test-Path $session) { throw 'Owned protected session files survived broker cleanup.' }
        if ($mode -eq 'startup-retry') { Write-Host '::notice title=Startup cancellation and retry::Actual medium UI cancelled after the owned elevated helper returned and before IPC authentication. Evidence lease closure stopped the owned bootstrap/job; the same client then authenticated a fresh helper and completed unchanged loopback traffic. No interactive UAC automation.' }
        $proof = Get-Content $evidence -Raw | ConvertFrom-Json
        if ($proof.UiAdministrator -or !$proof.AuthenticatedElevatedWorker -or $proof.Ipv6UnchangedDatagrams -ne 64 -or !$proof.Metrics.KernelLossUnknown) { throw 'Real privilege/network evidence is invalid.' }
        $remaining = Get-Process -Id $proof.NativePid -ErrorAction SilentlyContinue
        if ($remaining) { throw 'Owned native process survived broker/owner cleanup.' }
        Write-Host "::notice title=Broker $mode integration::Actual medium-integrity published WPF host authenticated the elevated native bootstrap/worker, verified offline components, forwarded 64 unchanged dedicated ::1 UDP datagrams, read numeric metrics and shut down owned children. Flowseal used filter=false. UAC interaction was replaced by a pre-elevated CI handoff; interactive approval/denial remains manual."
        Remove-Item $handoff,($handoff+'.pid'),($handoff+'.crash') -Force -ErrorAction SilentlyContinue
    }
} catch {
    $message = $_.ToString().Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A')
    Write-Host "::error title=Privileged broker acceptance failed::$message"
    throw
} finally {
    # Emergency fixture cleanup acts only on processes launched by this script.
    foreach ($owned in @($ownedLaunchers) + @($ownedHelpers)) {
        if (!$owned.HasExited) { Stop-Process -Id $owned.Id -Force -ErrorAction Continue; $null = $owned.WaitForExit(10000) }
    }
    # Remove only the private app checkout after owned processes have exited.
    if (Test-Path $install) { Remove-Item $install -Recurse -Force -ErrorAction Continue }
}
