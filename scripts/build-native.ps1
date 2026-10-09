param([ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SignCertificateThumbprint, [ValidatePattern('^https://')][string]$TimestampUrl = 'https://timestamp.digicert.com')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    python ./scripts/prepare-native.py --dependency-only
    if ($LASTEXITCODE -ne 0) { throw 'Verified native dependency acquisition failed.' }
    $signature = Get-AuthenticodeSignature ./dist/native-sdk/bin/WinDivert64.sys
    if ($signature.Status -ne 'Valid') { throw "Reviewed driver signature is not valid: $($signature.Status)" }
    Write-Host '::notice title=Native driver trust::Official pinned WinDivert 2.2.2 x64 driver SHA-256 and Windows Authenticode signature verified.'
    cmake -S native/NorthpassCore -B dist/native-build -A x64 "-DNORTHPASS_WINDIVERT_ROOT=$root/dist/native-sdk" -DBUILD_TESTING=ON
    if ($LASTEXITCODE -ne 0) { throw 'Native CMake configuration failed.' }
    cmake --build dist/native-build --config Release --parallel 2
    if ($LASTEXITCODE -ne 0) { throw 'Native MSVC build failed.' }
    New-Item -ItemType Directory -Force TestResults | Out-Null
    ctest --test-dir dist/native-build -C Release --output-on-failure --output-junit "$root/TestResults/native-windows.xml"
    if ($LASTEXITCODE -ne 0) { throw 'Native packet unit tests failed.' }
    $exe = Join-Path $root 'dist/native-build/Release/NorthpassCore.exe'
    if ($SignCertificateThumbprint) {
        signtool sign /sha1 $SignCertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $exe
        if ($LASTEXITCODE -ne 0) { throw 'Native code signing failed.' }
        signtool verify /pa $exe
        if ($LASTEXITCODE -ne 0) { throw 'Native code signature verification failed.' }
    }
    python ./scripts/package-native.py --executable $exe
    if ($LASTEXITCODE -ne 0) { throw 'Native offline manifest packaging failed.' }
    Write-Host '::notice title=Native Windows unit tests::29 native packet/flow/queue/metrics/IPC/transformation/reliability test groups passed on MSVC Windows x64. Original bytes only; no DPI bypass implemented.'
} finally { Pop-Location }
