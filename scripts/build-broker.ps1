param([switch]$Preview)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worker = Join-Path $root 'dist/broker-worker'
$helper = Join-Path $root 'dist/Northpass/broker'
if (Test-Path $worker) { Remove-Item $worker -Recurse -Force }
dotnet publish (Join-Path $root 'src/Northpass.Broker/Northpass.Broker.csproj') -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o $worker
if ($LASTEXITCODE -ne 0) { throw 'Managed network worker publish failed.' }
python (Join-Path $root 'scripts/prepare-broker.py') --worker $worker
if ($LASTEXITCODE -ne 0) { throw 'Worker hash header generation failed.' }
cmake -S (Join-Path $root 'native/NorthpassCore') -B (Join-Path $root 'dist/native-build') -A x64 "-DNORTHPASS_WINDIVERT_ROOT=$root/dist/native-sdk" "-DNORTHPASS_BROKER_HEADER=$worker"
if ($LASTEXITCODE -ne 0) { throw 'Native broker bootstrap configure failed.' }
cmake --build (Join-Path $root 'dist/native-build') --config Release --target NorthpassBrokerBootstrap --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Native broker bootstrap build failed.' }
New-Item -ItemType Directory -Force $helper | Out-Null
Get-ChildItem $worker -File | Where-Object { $_.Extension -ne '.hpp' } | Copy-Item -Destination $helper -Force
Copy-Item (Join-Path $root 'dist/native-build/Release/NorthpassBrokerBootstrap.exe') (Join-Path $helper 'Northpass.Broker.exe') -Force
python (Join-Path $root 'scripts/prepare-broker.py') --catalog $helper
if ($LASTEXITCODE -ne 0) { throw 'Immutable helper catalog generation failed.' }
