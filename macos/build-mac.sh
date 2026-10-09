#!/bin/bash
# Builds "Tbot Ultra.app" for macOS into artifacts/macos/.
# Requires: .NET 8 SDK (brew install dotnet@8) and the Xcode command line tools (clang, iconutil).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
if ! command -v dotnet >/dev/null 2>&1 && [ -x /opt/homebrew/opt/dotnet@8/bin/dotnet ]; then
  export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
  export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

case "$(uname -m)" in
  arm64) RID="osx-arm64"; CLANG_TARGET="arm64-apple-macos12.0" ;;
  *)     RID="osx-x64";   CLANG_TARGET="x86_64-apple-macos12.0" ;;
esac

VERSION="$(tr -d ' \n\r' < "$REPO/VERSION" | sed 's/^v//')"
OUT="$REPO/artifacts/macos"
APP="$OUT/Tbot Ultra.app"
WORK="$REPO/temp_build_out/macos"

echo "==> Publishing engine ($RID, v$VERSION)"
rm -rf "$APP" "$WORK"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/server" "$WORK"
dotnet publish "$REPO/src/TbotUltra.Mac/TbotUltra.Mac.csproj" -c Release -r "$RID" --self-contained true \
  --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:DebugType=none \
  -o "$APP/Contents/Resources/server" | tail -3

echo "==> Compiling native window shell"
SDK="$(xcrun --show-sdk-path)"
# Search the SDK explicitly instead of the default /usr/local/include, which on some machines holds
# stale copies of system headers that break the build.
clang -w -O2 -fobjc-arc -target "$CLANG_TARGET" -nostdlibinc -isystem "$SDK/usr/include" \
  -iframework "$SDK/System/Library/Frameworks" "$REPO/macos/TbotUltraApp.m" \
  -framework Cocoa -framework WebKit -o "$APP/Contents/MacOS/TbotUltra"

echo "==> Creating icon"
ICONSET="$WORK/TbotUltra.iconset"
mkdir -p "$ICONSET"
SOURCE_ICON="$WORK/icon-square.png"
# The source artwork is not square; pad it so iconutil accepts every size.
sips -s format png --padToHeightWidth 640 640 "$REPO/assets/icons/Tbot_ultra_appicon_2.png" --out "$SOURCE_ICON" >/dev/null
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$SOURCE_ICON" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$SOURCE_ICON" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/TbotUltra.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Tbot Ultra</string>
  <key>CFBundleDisplayName</key><string>Tbot Ultra</string>
  <key>CFBundleIdentifier</key><string>com.tbotultra.mac</string>
  <key>CFBundleExecutable</key><string>TbotUltra</string>
  <key>CFBundleIconFile</key><string>TbotUltra</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppTransportSecurity</key>
  <dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict>
</plist>
PLIST

echo "==> Signing (ad-hoc)"
codesign --force --deep --sign - "$APP" 2>&1 | tail -2

echo "==> Done: $APP"
