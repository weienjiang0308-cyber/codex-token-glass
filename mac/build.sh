#!/bin/bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd -P)"
cd "$repo_root"
if [[ "$(uname -s)" != Darwin ]]; then echo 'Build this target on macOS with Xcode 26+ and .NET 10 SDK.' >&2; exit 1; fi
command -v dotnet >/dev/null || { echo '.NET 10 SDK is required for the shared metrics helper.' >&2; exit 1; }
swift_major="$(xcrun swift --version | sed -nE 's/.*Swift version ([0-9]+)\.([0-9]+).*/\1/p' | head -1)"
swift_minor="$(xcrun swift --version | sed -nE 's/.*Swift version ([0-9]+)\.([0-9]+).*/\2/p' | head -1)"
if [[ "${swift_major:-0}" -lt 6 || ( "${swift_major:-0}" -eq 6 && "${swift_minor:-0}" -lt 2 ) ]]; then
  echo 'Select Xcode 26+ (Swift 6.2+) to compile the native Liquid Glass API.' >&2; exit 1
fi
case "$(uname -m)" in arm64) rid=osx-arm64 ;; x86_64) rid=osx-x64 ;; *) echo 'Unsupported Mac architecture.' >&2; exit 1 ;; esac
build_stage="$(mktemp -d "${TMPDIR:-/tmp}/codex-token-glass.XXXXXX")"
trap 'rm -rf -- "$build_stage"' EXIT
app="$build_stage/Codex Token Glass.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Helpers" "$app/Contents/Resources/licenses" "$repo_root/mac-dist/$rid"
dotnet publish src/CodexTokenMetrics/CodexTokenMetrics.csproj -c Release -r "$rid" --self-contained true -o "$app/Contents/Helpers"
"$app/Contents/Helpers/CodexTokenMetrics" --self-test "$repo_root/fixtures"
xcrun swift build --package-path mac -c release
swift_bin="$(xcrun swift build --package-path mac -c release --show-bin-path)"
cp "$swift_bin/CodexTokenGlass" "$app/Contents/MacOS/CodexTokenGlass"
cp mac/Info.plist "$app/Contents/Info.plist"
cp LICENSE LICENSE-codex-token-overlay THIRD-PARTY-NOTICES.md "$app/Contents/Resources/licenses/"
cp -R fixtures "$app/Contents/Resources/fixtures"
codesign --force --sign - --entitlements mac/helper-entitlements.plist "$app/Contents/Helpers/CodexTokenMetrics"
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"
CODEX_TOKEN_GLASS_PREVIEW_DIR="$repo_root/mac-dist/$rid/preview" "$app/Contents/MacOS/CodexTokenGlass" --smoke-test
archive="$repo_root/mac-dist/$rid/CodexTokenGlass-$rid.zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$archive"
(cd "$repo_root/mac-dist/$rid" && shasum -a 256 "CodexTokenGlass-$rid.zip" > SHA256SUMS.txt)
printf 'Built and smoke-tested: %s\n' "$archive"
printf 'Preview is ad-hoc signed, not Developer ID signed or notarized.\n'
