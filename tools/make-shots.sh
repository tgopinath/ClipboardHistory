#!/bin/zsh
# Renders the portfolio screenshots from the app's own SwiftUI views, with sample data in a throwaway HOME
# (your real clipboard history is never read). Output: prototype/shots/*.jpg
set -euo pipefail
cd "$(dirname "$0")/.."
T=$(mktemp -d)
sed -e 's/^@main$//' -e 's/@Published private(set) var items/@Published var items/' Sources/ClipboardHistory.swift > "$T/app.swift"
swiftc -O -parse-as-library -swift-version 5 -target "$(uname -m)-apple-macos14.0" "$T/app.swift" tools/shots.swift -o "$T/shots"
mkdir -p "$T/home" "$T/out" prototype/shots
# The image item in the sample list: any screenshot works; pass one as $1 or the app icon is used twice.
SAMPLE=${1:-Icon/AppIcon-1024.png}
HOME="$T/home" CFFIXED_USER_HOME="$T/home" "$T/shots" Icon/AppIcon-1024.png "$SAMPLE" "$T/out"
for f in panel-light panel-dark search-light search-dark; do
  sips -s format jpeg -s formatOptions 88 --resampleWidth 600 "$T/out/$f.png" --out "prototype/shots/$f.jpg" >/dev/null
done
sips -s format png --resampleWidth 256 Icon/AppIcon-1024.png --out prototype/shots/icon-256.png >/dev/null
cp tools/desk.html "$T/desk.html"; cp Icon/AppIcon-1024.png "$T/icon.png"; cp "$T/out/panel-light.png" "$T/desk-panel.png"
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --disable-gpu --hide-scrollbars \
  --window-size=1440,900 --screenshot="$T/desk.png" "file://$T/desk.html" 2>/dev/null
sips -s format jpeg -s formatOptions 86 "$T/desk.png" --out prototype/shots/desktop.jpg >/dev/null
rm -rf "$T"
echo "Wrote prototype/shots/"
