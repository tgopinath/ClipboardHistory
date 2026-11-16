// Draws the app icon and writes Icon/AppIcon.icns.
// Run: swift Icon/make_icon.swift
import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Icon")
let iconset = out.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

func rgb(_ hex: Int, _ a: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

/// Draws on a 1024pt canvas; scaled for each size.
func draw(in ctx: CGContext) {
    // macOS icon grid: 824pt rounded square centred in 1024, with a soft drop shadow.
    let tile = NSRect(x: 100, y: 100, width: 824, height: 824)
    let tilePath = NSBezierPath(roundedRect: tile, xRadius: 185, yRadius: 185)

    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: NSColor.black.withAlphaComponent(0.3).cgColor)
    rgb(0x3B2FD9).setFill()
    tilePath.fill()
    ctx.restoreGState()

    NSGradient(colors: [rgb(0x7B6CFF), rgb(0x4A3AF0), rgb(0x2E22B8)],
               atLocations: [0, 0.55, 1], colorSpace: .sRGB)!.draw(in: tilePath, angle: -90)

    // Subtle top highlight.
    NSGraphicsContext.saveGraphicsState()
    tilePath.addClip()
    NSGradient(colors: [NSColor.white.withAlphaComponent(0.18), NSColor.white.withAlphaComponent(0)])!
        .draw(in: NSRect(x: 100, y: 560, width: 824, height: 364), angle: -90)
    NSGraphicsContext.restoreGraphicsState()

    // Centre the clipboard group vertically in the tile.
    ctx.translateBy(x: 0, y: 58)

    // Stack of earlier clips behind the main card — the "history".
    func card(_ r: NSRect, _ color: NSColor, shadow: CGFloat) {
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: shadow, color: NSColor.black.withAlphaComponent(0.25).cgColor)
        color.setFill()
        NSBezierPath(roundedRect: r, xRadius: 44, yRadius: 44).fill()
        ctx.restoreGState()
    }
    card(NSRect(x: 352, y: 262, width: 400, height: 470), NSColor.white.withAlphaComponent(0.35), shadow: 10)
    card(NSRect(x: 322, y: 232, width: 400, height: 470), NSColor.white.withAlphaComponent(0.6), shadow: 14)

    // Main clipboard board.
    let board = NSRect(x: 272, y: 180, width: 420, height: 500)
    card(board, rgb(0xFFFFFF), shadow: 24)

    // Clip at the top.
    let clip = NSRect(x: 392, y: 640, width: 180, height: 84)
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 10, color: NSColor.black.withAlphaComponent(0.25).cgColor)
    NSGradient(colors: [rgb(0x2A2F45), rgb(0x161A2B)])!.draw(in: NSBezierPath(roundedRect: clip, xRadius: 26, yRadius: 26), angle: -90)
    ctx.restoreGState()
    rgb(0xFFFFFF).setFill()
    NSBezierPath(roundedRect: NSRect(x: 452, y: 688, width: 60, height: 20), xRadius: 10, yRadius: 10).fill()

    // Lines of text on the board, the first one highlighted as the picked clip.
    let rows: [(CGFloat, CGFloat, NSColor)] = [
        (560, 300, rgb(0x5B4CF5)),
        (480, 260, rgb(0xC9CCD9)),
        (400, 300, rgb(0xC9CCD9)),
        (320, 210, rgb(0xC9CCD9)),
    ]
    for (y, w, c) in rows {
        c.setFill()
        NSBezierPath(roundedRect: NSRect(x: 332, y: y, width: w, height: 34), xRadius: 17, yRadius: 17).fill()
    }
}

func png(size: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    let g = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.current = g
    g.cgContext.scaleBy(x: CGFloat(size) / 1024, y: CGFloat(size) / 1024)
    draw(in: g.cgContext)
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for base in [16, 32, 128, 256, 512] {
    try! png(size: base).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try! png(size: base * 2).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
try! png(size: 1024).write(to: out.appendingPathComponent("AppIcon-1024.png"))
print("Wrote \(iconset.path)")
