# Secure broker development uses a protected installed copy. User-writable
# dotnet run output cannot become an elevated trusted network component.
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Use Administrator PowerShell to build/install, then launch Northpass normally from the Start menu.'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    ./scripts/build.ps1 -Installer
    $setup = Start-Process (Join-Path $root 'dist/installer/Northpass-0.7.0-win-x64-setup.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru -Wait
    if ($setup.ExitCode -ne 0) { throw "Development installation failed: $($setup.ExitCode)" }
    # Ask the normal desktop shell to launch the asInvoker UI, rather than
    # inheriting this terminal's elevated token. Interactive shell needed.
    $shell = New-Object -ComObject Shell.Application
    $shell.ShellExecute((Join-Path $env:ProgramFiles 'Northpass/Northpass.exe'), '', '', 'open', 1)
} finally { Pop-Location }
