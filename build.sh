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

# Install a freshly built binary by writing to a temp file and atomically renaming it
# into place. The rename gives the destination a NEW inode, which sidesteps a macOS
# quirk: overwriting an ad-hoc/linker-signed binary in place leaves AMFI holding the
# previous binary's cdhash for that inode, so the replacement is SIGKILLed ("killed: 9")
# at exec even though `codesign -v` still reports the on-disk signature as valid.
install_atomic() {
  local src="$1" dst="$2"
  local tmp="${dst}.tmp.$$"
  cp "$src" "$tmp"
  chmod +x "$tmp"
  mv -f "$tmp" "$dst"
}

# Also install the binaries to a directory on PATH so they are runnable from
# anywhere (e.g. the VS Code integrated terminal). Override with INSTALL_DIR=...
INSTALL_DIR="${INSTALL_DIR:-$HOME/.local/bin}"

mkdir -p bin
mkdir -p "$INSTALL_DIR"

echo "==> Publishing ThroughlineBuild.Cli ($RID)"
dotnet publish src/ThroughlineBuild.Cli -r "$RID" -c Release --nologo -v q
install_atomic "src/ThroughlineBuild.Cli/bin/Release/net10.0/$RID/publish/build$EXT" "bin/build$EXT"

echo "==> Publishing token-audit ($RID)"
dotnet publish src/tools/token-audit.cs -r "$RID" -c Release --nologo -v q
install_atomic "src/tools/artifacts/token-audit/token-audit$EXT" "bin/token-audit$EXT"

echo "==> Publishing analyze-event-log ($RID)"
dotnet publish src/tools/analyze-event-log.cs -r "$RID" -c Release --nologo -v q
install_atomic "src/tools/artifacts/analyze-event-log/analyze-event-log$EXT" "bin/analyze-event-log$EXT"

echo
echo "==> Installing to $INSTALL_DIR"
for b in "build$EXT" "token-audit$EXT" "analyze-event-log$EXT"; do
  install_atomic "bin/$b" "$INSTALL_DIR/$b"
done

echo
echo "Done. Binaries copied to bin/ and installed to $INSTALL_DIR:"
ls -1 "bin/build$EXT" "bin/token-audit$EXT" "bin/analyze-event-log$EXT"

case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) echo; echo "Note: $INSTALL_DIR is not on your PATH; add it to run these from anywhere." ;;
esac
