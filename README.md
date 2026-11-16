# Clipboard History

A small macOS menu bar app that keeps your clipboard history (200 items by default, up to 1000).

## Download

[Download ClipboardHistory.dmg](https://github.com/tgopinath/ClipboardHistory/releases/latest/download/ClipboardHistory.dmg) (macOS 14+). Open it, drag the app onto Applications, then right-click the app and choose Open the first time (it isn't notarized).

## Build and install

```bash
./build.sh --install
```

This builds `Clipboard History.app`, copies it to `~/Applications` and launches it. It needs macOS 14 or later and the Xcode command line tools.

To make a disk image instead, run `./build.sh --dmg`. It creates `build/ClipboardHistory.dmg`; open it and drag the app onto Applications. The app isn't notarized, so on another Mac the first launch needs right-click → Open.

## Use

- Click the clipboard icon in the menu bar, or press **⌃⌘V** anywhere.
- Type to search. **↑ ↓** to move, **↩** to copy the item, **⌘1–9** to pick one of the first nine.
- Hover an item and click the copy icon to copy just that one. **Copy All** (top right, or **⇧⌘C**) copies every item in the list — or only the search matches — with text joined line by line.
- **⌘P** pins the selected item; pinned items stay at the top and never drop off. **⌘⌫** deletes it.
- Right-click the menu bar icon to pause recording, clear history or quit.

The gear menu sets how many items to keep, launch at login (turned on the first time you run it), pause, and "Paste automatically after picking". That last option needs Accessibility permission, which macOS asks for when you turn it on.

Text and images are saved. Items that password managers mark as secret are skipped. History is stored in `~/Library/Application Support/ClipboardHistory/`.

## Screenshots

`tools/make-shots.sh` renders `prototype/shots/` from the app's own SwiftUI views with sample data in a throwaway home folder, so your real history is never read. `prototype/index.html` is the screens page used by the portfolio.
