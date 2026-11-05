// Click-through output window above the game (also in full-screen Spaces),
// excluded from screen capture (sharingType = .none).
import AppKit
import QuartzCore
import Metal

final class Overlay {
    let window: NSWindow
    let layer: CAMetalLayer
    private(set) var pixelSize = CGSize.zero

    init(device: MTLDevice) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 16, height: 16),
                          styleMask: .borderless, backing: .buffered, defer: false)
        window.isOpaque = true
        window.backgroundColor = .black
        window.hasShadow = false
        window.ignoresMouseEvents = true          // all input goes to the game
        window.sharingType = .none                // not captured by us or by recorders
        window.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.overlayWindow)))
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        layer = CAMetalLayer()
        layer.device = device
        layer.pixelFormat = .bgra8Unorm
        layer.framebufferOnly = false
        layer.displaySyncEnabled = true
        layer.maximumDrawableCount = 2
        layer.isOpaque = true
        let view = NSView(frame: window.contentLayoutRect)
        view.wantsLayer = true
        view.layer = layer
        window.contentView = view
    }

    /// rect in CoreGraphics global coordinates (top-left origin), scale = backing scale.
    func setRect(_ cg: CGRect, scale: CGFloat) {
        let primaryH = NSScreen.screens.first?.frame.height ?? cg.maxY
        let cocoa = NSRect(x: cg.minX, y: primaryH - cg.maxY, width: cg.width, height: cg.height)
        window.setFrame(cocoa, display: false)
        layer.contentsScale = scale
        pixelSize = CGSize(width: cg.width * scale, height: cg.height * scale)
        layer.drawableSize = pixelSize
    }

    func show(_ on: Bool) {
        if on { window.orderFrontRegardless() } else { window.orderOut(nil) }
    }
}
