import AppKit
import SwiftUI

@MainActor func render(dark: Bool, query: String, selected: Int, file: String, store: ClipStore, settings: Settings) {
    let panel = Panel(store: store)
    panel.query = query
    panel.selected = selected
    let view = HistoryView(store: store, panel: panel, settings: settings, onPick: { _ in }, onCopyAll: { _ in }, onClear: {})
    let bg = dark ? Color(red: 0.165, green: 0.165, blue: 0.18) : Color(red: 0.965, green: 0.965, blue: 0.97)
    let root = view.background(bg).environment(\.colorScheme, dark ? .dark : .light)
    let host = NSHostingView(rootView: root)
    host.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
    host.frame = NSRect(x: 0, y: 0, width: 380, height: 480)
    let win = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
    win.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
    win.backgroundColor = dark ? NSColor(white: 0.16, alpha: 1) : NSColor(white: 0.97, alpha: 1)
    win.contentView = host
    win.setFrameOrigin(NSPoint(x: -4000, y: -4000))
    win.orderFrontRegardless()
    for _ in 0..<15 { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
    panel.selected = selected
    for _ in 0..<6 { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
    host.layoutSubtreeIfNeeded()
    let rect = host.bounds
    guard let rep = host.bitmapImageRepForCachingDisplay(in: rect) else { return }
    rep.size = rect.size
    host.cacheDisplay(in: rect, to: rep)
    let png = rep.representation(using: .png, properties: [:])!
    try! png.write(to: URL(fileURLWithPath: file))
    win.orderOut(nil)
}

@main enum Shots {
    @MainActor static func main() {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.accessory)
        UserDefaults.standard.set(true, forKey: "didFirstLaunch")
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        print("appsupport:", base.path)
        guard !base.path.hasPrefix("/Users/") else { print("HOME not honoured; abort"); exit(1) }
        let store = ClipStore()
        let settings = Settings()
        let imgDir = base.appendingPathComponent("ClipboardHistory/images")
        try? FileManager.default.createDirectory(at: imgDir, withIntermediateDirectories: true)
        let args = CommandLine.arguments
        let icon = URL(fileURLWithPath: args[1])
        try? FileManager.default.removeItem(at: imgDir.appendingPathComponent("icon.png"))
        try! FileManager.default.copyItem(at: icon, to: imgDir.appendingPathComponent("icon.png"))
        let shot = URL(fileURLWithPath: args[2])
        try? FileManager.default.removeItem(at: imgDir.appendingPathComponent("shot.png"))
        try! FileManager.default.copyItem(at: shot, to: imgDir.appendingPathComponent("shot.png"))
        let now = Date()
        func t(_ s: String, _ ago: Double, pin: Bool = false) -> ClipItem { ClipItem(kind: .text, text: s, date: now.addingTimeInterval(-ago), pinned: pin) }
        func i(_ f: String, _ ago: Double) -> ClipItem { ClipItem(kind: .image, imageFile: f, date: now.addingTimeInterval(-ago)) }
        store.items = [
            t("Kind regards,\nGopinath Thandapani\nSenior Product Designer", 86400 * 6, pin: true),
            t("https://tgopinath.github.io/Portfolio/", 86400 * 3, pin: true),
            t("Spacing tokens: 4, 8, 12, 16, 24, 32, 48", 4),
            i("shot.png", 95),
            t("git commit -m \"Panel: keep picked items in place\"", 240),
            t("Can we move the design review to Thursday at 3 pm?", 780),
            t("#1F2128", 1500),
            i("icon.png", 3600 * 2),
            t("Menu bar apps should open in under 100 ms and close on Escape.", 3600 * 3),
            t("brew install --cask figma", 3600 * 5),
            t("Chennai 600 096", 3600 * 7),
        ]
        let o = args[3]
        render(dark: false, query: "", selected: 2, file: o + "/panel-light.png", store: store, settings: settings)
        render(dark: true, query: "", selected: 2, file: o + "/panel-dark.png", store: store, settings: settings)
        render(dark: false, query: "design", selected: 0, file: o + "/search-light.png", store: store, settings: settings)
        render(dark: true, query: "design", selected: 0, file: o + "/search-dark.png", store: store, settings: settings)
        print("done")
    }
}
