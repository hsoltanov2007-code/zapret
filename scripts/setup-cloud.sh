#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tools_root=/workspace/.northpass-tools
sdk_version=8.0.425
sdk_hash=934b8060a7190e5909ad1fd0785db542f487b3bbf6cdd14826b02095fdd0d0394298b1634085eff302928fccc33f7c1a7253e9b87df555fc36fce819bcd2e798
if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    echo 'Cloud setup script requires Linux x64. For Windows use docs/DEVELOPMENT.md.' >&2
    exit 1
fi
mkdir -p "$tools_root"
if [[ ! -x "$tools_root/dotnet/dotnet" || ! -d "$tools_root/dotnet/sdk/$sdk_version" ]]; then
    archive="$(mktemp /tmp/northpass-dotnet.XXXXXX.tar.gz)"
    trap 'rm -f "$archive"' EXIT
    # SHA-512 from Microsoft's official .NET 8 release metadata. Never bypass TLS/checksums.
    curl --fail --silent --show-error --location "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" -o "$archive"
    python3 - "$archive" "$sdk_hash" "$tools_root/dotnet" <<'PY'
import hashlib, sys, tarfile
from pathlib import Path
archive, expected, destination = sys.argv[1:]
hash_value = hashlib.sha512()
with open(archive, 'rb') as handle:
    for chunk in iter(lambda: handle.read(1024 * 1024), b''):
        hash_value.update(chunk)
if hash_value.hexdigest() != expected:
    raise SystemExit('Official SDK SHA-512 verification failed')
Path(destination).mkdir(parents=True, exist_ok=True)
with tarfile.open(archive) as handle:
    handle.extractall(destination, filter='data')
PY
fi
cat > "$tools_root/env.sh" <<'ENV'
export DOTNET_ROOT=/workspace/.northpass-tools/dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/workspace/.northpass-tools/cli-home
export NUGET_PACKAGES=/workspace/.northpass-tools/nuget-packages
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
ENV
source "$tools_root/env.sh"
cd "$repo_root"
dotnet restore tests/Northpass.Tests/Northpass.Tests.csproj --locked-mode
dotnet restore tests/Northpass.Windows.Tests/Northpass.Windows.Tests.csproj --locked-mode
dotnet restore src/Northpass.Broker/Northpass.Broker.csproj --locked-mode
dotnet build Northpass.sln -c Release --no-restore --nologo
