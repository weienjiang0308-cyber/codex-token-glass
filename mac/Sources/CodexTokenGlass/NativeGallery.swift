import AppKit
import SwiftUI

@MainActor
private final class DenseGalleryWindow: NSWindow {
    override var backingScaleFactor: CGFloat { 4 }
}

/// Native UI export only. SwiftUI text and AppKit control layers need separate
/// backing passes on a non-Retina CI display. Both passes use the same views,
/// layout and synthetic model; pixels are composed 1:1, never upsampled.
@MainActor
final class NativeGallery {
    private let model: MetricsModel
    private let directory: URL
    private let sharp = DenseGalleryWindow(contentRect: .zero, styleMask: [.titled], backing: .buffered, defer: false)
    private let controls = NSWindow(contentRect: .zero, styleMask: [.titled], backing: .buffered, defer: false)
    private let variants = ["zh", "en"].flatMap { language in
        ["light", "dark"].flatMap { theme in
            (["details", "settings", "strip"] + (language == "zh" ? ["waiting"] : [])).map { ($0, theme, language) }
        }
    }

    init(model: MetricsModel, directory: URL) {
        self.model = model; self.directory = directory
        sharp.title = "Synthetic native text pass"; controls.title = "Synthetic native control pass"
    }

    func run(_ index: Int = 0) {
        guard index < variants.count else {
            print("PASS: 14 synthetic native views exported at 4x without desktop capture")
            sharp.orderOut(nil); controls.orderOut(nil); NSApplication.shared.terminate(nil); return
        }
        let (kind, theme, language) = variants[index]
        model.settingsVisible = kind == "settings"; model.theme = theme; model.language = language
        model.snapshot = kind == "waiting" ? MetricsSnapshot() : .demo
        let base = kind == "strip" ? NSSize(width: 310, height: 44) : NSSize(width: 410, height: 760)
        let denseView = host(kind, base: base, physical: false)
        install(denseView, in: sharp, size: base, theme: theme)
        denseView.viewDidChangeBackingProperties(); denseView.layer?.contentsScale = 4
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [self] in
            denseView.layoutSubtreeIfNeeded(); denseView.displayIfNeeded()
            let text = capture(denseView, scale: 4)
            let native = nativeFrames(denseView)
            let pixelSize = NSSize(width: base.width * 4, height: base.height * 4)
            let nativeView = host(kind, base: base, physical: true)
            install(nativeView, in: controls, size: pixelSize, theme: theme)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [self] in
                nativeView.layoutSubtreeIfNeeded(); nativeView.displayIfNeeded()
                let widgets = capture(nativeView, scale: 1)
                guard let textImage = text.cgImage, let widgetImage = widgets.cgImage,
                    let context = CGContext(data: nil, width: textImage.width, height: textImage.height,
                        bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { fatalError("Gallery context unavailable") }
                context.interpolationQuality = .none
                context.draw(textImage, in: CGRect(origin: .zero, size: pixelSize))
                for frame in native {
                    let pixels = CGRect(x: frame.minX * 4, y: frame.minY * 4, width: frame.width * 4, height: frame.height * 4).integral
                    guard let region = widgetImage.cropping(to: pixels) else { continue }
                    context.draw(region, in: CGRect(x: pixels.minX, y: pixelSize.height - pixels.maxY, width: pixels.width, height: pixels.height))
                }
                guard let image = context.makeImage(), let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else { fatalError("Gallery PNG failed") }
                do {
                    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                    try png.write(to: directory.appendingPathComponent("macos-\(kind)-\(theme)-\(language).png"))
                } catch { fatalError("Gallery export failed") }
                print("Gallery \(kind)-\(theme)-\(language): \(textImage.width)x\(textImage.height), native controls=\(native.count)")
                run(index + 1)
            }
        }
    }

    private func host(_ kind: String, base: NSSize, physical: Bool) -> NSHostingView<AnyView> {
        let content = kind == "strip" ? AnyView(FloatingStrip(model: model).frame(width: base.width, height: base.height)) : AnyView(DetailView(model: model))
        let factor: CGFloat = physical ? 4 : 1
        let root = physical ? AnyView(content.scaleEffect(4, anchor: .topLeading)
            .frame(width: base.width * 4, height: base.height * 4, alignment: .topLeading)) : AnyView(content.environment(\.displayScale, 4))
        let view = NSHostingView(rootView: root)
        view.sizingOptions = []
        view.frame = NSRect(origin: .zero, size: NSSize(width: base.width * factor, height: base.height * factor))
        return view
    }

    private func install(_ view: NSView, in window: NSWindow, size: NSSize, theme: String) {
        window.appearance = NSAppearance(named: theme == "dark" ? .darkAqua : .aqua)
        window.contentView = view; window.setContentSize(size); view.setFrameSize(size)
        window.orderFrontRegardless()
    }

    private func capture(_ view: NSView, scale: CGFloat) -> NSBitmapImageRep {
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(view.bounds.width * scale),
            pixelsHigh: Int(view.bounds.height * scale), bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
            isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { fatalError("Gallery bitmap unavailable") }
        bitmap.size = view.bounds.size
        view.cacheDisplay(in: view.bounds, to: bitmap)
        return bitmap
    }

    private func nativeFrames(_ root: NSView) -> [CGRect] {
        var frames: [CGRect] = []
        func walk(_ view: NSView) {
            guard !view.isHidden else { return }
            let name = NSStringFromClass(type(of: view)).lowercased()
            if view is NSSwitch || view is NSPopUpButton || view is NSSegmentedControl || view is NSSlider || name.contains("switch") {
                var frame = root.convert(view.bounds, from: view).insetBy(dx: -2, dy: -2).intersection(root.bounds)
                if !root.isFlipped { frame.origin.y = root.bounds.height - frame.maxY }
                if !frame.isEmpty { frames.append(frame) }
                return
            }
            view.subviews.forEach(walk)
        }
        walk(root)
        return frames
    }
}
