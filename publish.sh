#!/usr/bin/env bash
# Same as publish.ps1, for building the Windows exe from macOS/Linux.
set -euo pipefail
cd "$(dirname "$0")"

OUT="${1:-publish}"

if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
  echo "==> Running tests"
  dotnet test tests/KodizSignage.Tests/KodizSignage.Tests.csproj -c Release
fi

echo "==> Publishing win-x64 to $OUT"
rm -rf "$OUT"
dotnet publish src/KodizSignage/KodizSignage.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

ls -lh "$OUT"/KodizSignage.exe
