#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

if [[ -z "${RID:-}" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)  RID="linux-x64" ;;
    Linux-arm64)   RID="linux-arm64" ;;
    Darwin-x86_64) RID="osx-x64" ;;
    Darwin-arm64)  RID="osx-arm64" ;;
    *)             RID="win-x64" ;;
  esac
fi
EXT=""
[[ "$RID" == win-* ]] && EXT=".exe"

mkdir -p bin

echo "==> Publishing ThroughlineBuild.Cli ($RID)"
dotnet publish src/ThroughlineBuild.Cli -r "$RID" -c Release --nologo -v q
cp "src/ThroughlineBuild.Cli/bin/Release/net10.0/$RID/publish/build$EXT" "bin/build$EXT"

echo "==> Publishing token-audit ($RID)"
dotnet publish src/tools/token-audit.cs -r "$RID" -c Release --nologo -v q
cp "src/tools/artifacts/token-audit/token-audit$EXT" "bin/token-audit$EXT"

echo "==> Publishing analyze-event-log ($RID)"
dotnet publish src/tools/analyze-event-log.cs -r "$RID" -c Release --nologo -v q
cp "src/tools/artifacts/analyze-event-log/analyze-event-log$EXT" "bin/analyze-event-log$EXT"

echo
echo "Done. Binaries copied to bin/:"
ls -1 "bin/build$EXT" "bin/token-audit$EXT" "bin/analyze-event-log$EXT"
