#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

LOCKFILE_SNAPSHOT_DIR=""

# Local publish restores are RID- and SDK-patch-sensitive, so they can rewrite
# tracked NuGet lockfiles just because this script ran on another host. Preserve
# the caller's pre-run lockfile state; dependency updates belong in restore/test
# workflows, not in the local installer.
snapshot_lockfiles() {
  if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    return
  fi

  LOCKFILE_SNAPSHOT_DIR="$(mktemp -d)"
  git ls-files -z -- '*/packages.lock.json' > "$LOCKFILE_SNAPSHOT_DIR/paths.z"
  while IFS= read -r -d '' path; do
    mkdir -p "$LOCKFILE_SNAPSHOT_DIR/$(dirname "$path")"
    cp "$path" "$LOCKFILE_SNAPSHOT_DIR/$path"
  done < "$LOCKFILE_SNAPSHOT_DIR/paths.z"
}

restore_lockfiles() {
  if [[ -z "$LOCKFILE_SNAPSHOT_DIR" || ! -d "$LOCKFILE_SNAPSHOT_DIR" ]]; then
    return
  fi

  while IFS= read -r -d '' path; do
    if [[ -f "$LOCKFILE_SNAPSHOT_DIR/$path" ]]; then
      cp "$LOCKFILE_SNAPSHOT_DIR/$path" "$path"
    fi
  done < "$LOCKFILE_SNAPSHOT_DIR/paths.z"
  rm -rf "$LOCKFILE_SNAPSHOT_DIR"
}

snapshot_lockfiles
trap restore_lockfiles EXIT

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
