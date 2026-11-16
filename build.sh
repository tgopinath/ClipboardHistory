#!/bin/zsh
# Builds "Clipboard History.app" and installs it to ~/Applications.
set -euo pipefail
cd "$(dirname "$0")"

APP="build/Clipboard History.app"
rm -rf build && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

swiftc -O -parse-as-library -swift-version 5 \
  -target "$(uname -m)-apple-macos14.0" \
  Sources/ClipboardHistory.swift \
  -o "$APP/Contents/MacOS/ClipboardHistory"

if [[ ! -f Icon/AppIcon.icns ]]; then
  swift Icon/make_icon.swift Icon
  iconutil -c icns Icon/AppIcon.iconset -o Icon/AppIcon.icns
  rm -rf Icon/AppIcon.iconset
fi
cp Icon/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Clipboard History</string>
  <key>CFBundleDisplayName</key><string>Clipboard History</string>
  <key>CFBundleIdentifier</key><string>com.gopinath.clipboardhistory</string>
  <key>CFBundleExecutable</key><string>ClipboardHistory</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST

codesign --force --sign - "$APP"

if [[ "${1:-}" == "--dmg" ]]; then
  STAGE=build/dmg
  mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  hdiutil create -volname "Clipboard History" -srcfolder "$STAGE" -ov -format UDZO "build/ClipboardHistory.dmg" >/dev/null
  rm -rf "$STAGE"
  echo "Created build/ClipboardHistory.dmg"
elif [[ "${1:-}" == "--install" ]]; then
  mkdir -p ~/Applications
  pkill -x ClipboardHistory 2>/dev/null || true
  rm -rf ~/Applications/"Clipboard History.app"
  cp -R "$APP" ~/Applications/
  open ~/Applications/"Clipboard History.app"
  echo "Installed and launched ~/Applications/Clipboard History.app"
else
  echo "Built $APP  (./build.sh --install to install and launch, ./build.sh --dmg for a disk image)"
fi
