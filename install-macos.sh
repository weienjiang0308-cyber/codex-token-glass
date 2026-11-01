#!/bin/bash
set -euo pipefail
if [[ "$(uname -s)" != Darwin ]]; then echo 'This installer requires macOS 14 or later.' >&2; exit 1; fi
if [[ "$(sw_vers -productVersion | cut -d. -f1)" -lt 14 ]]; then echo 'macOS 14 or later is required.' >&2; exit 1; fi
repo_root="$(cd "$(dirname "$0")" && pwd -P)"
package=''
checksum=''
from_source=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --from-source) from_source=true; shift ;;
    --package) [[ $# -ge 2 ]] || exit 2; package="$2"; shift 2 ;;
    --sha256) [[ $# -ge 2 ]] || exit 2; checksum="$2"; shift 2 ;;
    *) echo 'Usage: bash install-macos.sh --package FILE.zip [--sha256 HASH] | --from-source' >&2; exit 2 ;;
  esac
done
case "$(uname -m)" in arm64) rid=osx-arm64 ;; x86_64) rid=osx-x64 ;; *) exit 1 ;; esac
if $from_source; then
  bash "$repo_root/mac/build.sh"
  package="$repo_root/mac-dist/$rid/CodexTokenGlass-$rid.zip"
fi
[[ -f "$package" ]] || { echo 'Choose the Mac ZIP for your architecture, or use --from-source.' >&2; exit 1; }
if [[ -z "$checksum" ]]; then
  manifest="$(dirname "$package")/SHA256SUMS.txt"
  [[ -f "$manifest" ]] || { echo 'SHA256SUMS.txt is missing; pass a verified --sha256 value.' >&2; exit 1; }
  checksum="$(awk -v name="$(basename "$package")" '$2 == name { print $1 }' "$manifest")"
fi
[[ "$checksum" =~ ^[a-fA-F0-9]{64}$ ]] || { echo 'Invalid SHA256 manifest entry.' >&2; exit 1; }
actual="$(shasum -a 256 "$package" | awk '{print $1}')"
[[ "$(printf '%s' "$checksum" | tr 'A-F' 'a-f')" == "$actual" ]] || { echo 'Package checksum mismatch.' >&2; exit 1; }
install_stage="$(mktemp -d "${TMPDIR:-/tmp}/codex-token-glass-install.XXXXXX")"
trap 'rm -rf -- "$install_stage"' EXIT
ditto -x -k "$package" "$install_stage"
app="$install_stage/Codex Token Glass.app"
[[ -x "$app/Contents/MacOS/CodexTokenGlass" && -x "$app/Contents/Helpers/CodexTokenMetrics" ]] || { echo 'Incomplete application bundle.' >&2; exit 1; }
lipo -verify_arch "$(uname -m)" "$app/Contents/MacOS/CodexTokenGlass"
codesign --verify --deep --strict "$app"
"$app/Contents/Helpers/CodexTokenMetrics" --self-test "$app/Contents/Resources/fixtures"
destination="$HOME/Applications/Codex Token Glass.app"
if pgrep -f "$destination/Contents/MacOS/CodexTokenGlass" >/dev/null; then
  echo 'Quit Codex Token Glass from its menu before updating. Codex itself can keep running.' >&2; exit 1
fi
mkdir -p "$HOME/Applications"
if [[ -e "$destination" ]]; then mv "$destination" "$destination.backup-$(date +%Y%m%d-%H%M%S)"; fi
ditto "$app" "$destination"
printf 'Installed: %s\n' "$destination"
printf 'Preview is not notarized. If macOS blocks opening it, review System Settings → Privacy & Security → Open Anyway.\n'
printf 'No Gatekeeper, quarantine, Codex configuration, or login-item settings were changed.\n'
open "$destination"
