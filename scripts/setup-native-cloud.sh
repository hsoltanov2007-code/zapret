#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ ! -x /workspace/.northpass-tools/python-native/cmake/data/bin/cmake ]]; then
    python3 -m pip install --no-deps --only-binary=:all: --require-hashes --target /workspace/.northpass-tools/python-native -r "$repo_root/native/tools.txt"
fi
cat > /workspace/.northpass-tools/native-env.sh <<'ENV'
source /workspace/.northpass-tools/env.sh
export PATH=/workspace/.northpass-tools/python-native/cmake/data/bin:$PATH
ENV
source /workspace/.northpass-tools/native-env.sh
cmake --version
c++ --version
cmake -S "$repo_root/native/NorthpassCore" -B /workspace/.northpass-tools/native-build -DCMAKE_BUILD_TYPE=Debug -DNORTHPASS_SANITIZERS=ON
cmake --build /workspace/.northpass-tools/native-build --parallel 2
ctest --test-dir /workspace/.northpass-tools/native-build --output-on-failure
