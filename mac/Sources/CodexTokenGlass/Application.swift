import AppKit
import SwiftUI
import Combine
import CoreGraphics

@main
enum Application {
    @MainActor static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        application.setActivationPolicy(.accessory)
        application.run()
        withExtendedLifetime(delegate) {}
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let model = MetricsModel()
    private var statusItem: NSStatusItem!
    private let popover = NSPopover()
    private var panel: NSPanel?
    private var observer: AnyCancellable?
    private var timer: Timer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.target = self; statusItem.button?.action = #selector(togglePopover)
        statusItem.button?.font = .monospacedDigitSystemFont(ofSize: 11, weight: .medium)
        popover.behavior = .transient
        popover.contentSize = NSSize(width: 410, height: 570)
        popover.contentViewController = NSHostingController(rootView: DetailView(model: model))
        model.showDetails = { [weak self] in self?.showPopover() }
        model.closeDetails = { [weak self] in self?.popover.performClose(nil) }
        observer = model.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async { self?.updateStatus() }
        }
        let floating = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 310, height: 44),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        floating.isOpaque = false; floating.backgroundColor = .clear; floating.hasShadow = true
        floating.level = .floating; floating.hidesOnDeactivate = false
        floating.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        floating.contentView = NSHostingView(rootView: FloatingStrip(model: model))
        panel = floating
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.positionStrip() }
        }
        model.start(); updateStatus()
        if CommandLine.arguments.contains("--enable-login") { model.setLogin(true) }
        if CommandLine.arguments.contains("--smoke-test") {
            // Layout both screens with synthetic data; never inspect runner accounts.
            let host = NSHostingView(rootView: DetailView(model: model))
            host.frame = NSRect(x: 0, y: 0, width: 410, height: 570)
            host.layoutSubtreeIfNeeded()
            if let directory = ProcessInfo.processInfo.environment["CODEX_TOKEN_GLASS_PREVIEW_DIR"] {
                let window = NSWindow(contentRect: host.frame, styleMask: [.titled], backing: .buffered, defer: false)
                window.title = "Synthetic preview"; window.contentView = host
                window.center(); window.orderFrontRegardless()
                exportPreview(index: 0, host: host, window: window, directory: URL(fileURLWithPath: directory))
            } else {
                model.settingsVisible = true; host.layoutSubtreeIfNeeded()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    print("PASS: macOS native views initialized with synthetic data")
                    NSApplication.shared.terminate(nil)
                }
            }
        } else if CommandLine.arguments.contains("--demo") { showPopover() }
    }

    private func exportPreview(index: Int, host: NSView, window: NSWindow, directory: URL) {
        let variants = [("light-details-zh", false, "light", "zh"), ("light-settings-zh", true, "light", "zh"),
                        ("dark-details-en", false, "dark", "en"), ("dark-settings-en", true, "dark", "en")]
        guard index < variants.count else {
            print("PASS: native light/dark details and settings rendered with synthetic data")
            window.orderOut(nil); NSApplication.shared.terminate(nil); return
        }
        let variant = variants[index]
        model.settingsVisible = variant.1; model.theme = variant.2; model.language = variant.3
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [self] in
            host.layoutSubtreeIfNeeded(); host.displayIfNeeded()
            guard let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { fatalError("Preview bitmap unavailable") }
            host.cacheDisplay(in: host.bounds, to: bitmap)
            guard let png = bitmap.representation(using: .png, properties: [:]) else { fatalError("Preview encoding failed") }
            do {
                try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                try png.write(to: directory.appendingPathComponent(variant.0 + ".png"))
            } catch { fatalError("Preview export failed") }
            exportPreview(index: index + 1, host: host, window: window, directory: directory)
        }
    }

    @objc private func togglePopover() { popover.isShown ? popover.performClose(nil) : showPopover() }
    private func showPopover() {
        guard let button = statusItem.button else { return }
        NSApp.activate(ignoringOtherApps: true)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
    }
    private func updateStatus() {
        statusItem.button?.title = "\(model.speedText) tok/s  ·  \(model.totalText)  ·  \(model.cacheText)"
        statusItem.button?.toolTip = model.tr("Codex Token Glass · 点击查看详情", "Codex Token Glass · Click for details")
        statusItem.button?.setAccessibilityLabel(model.tr("Codex Token 用量与速度", "Codex token usage and speed"))
        positionStrip()
    }

    private func positionStrip() {
        guard model.floating, let front = NSWorkspace.shared.frontmostApplication,
              front.bundleIdentifier?.lowercased().contains("codex") == true,
              let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]],
              let entry = windows.first(where: {
                  ($0[kCGWindowOwnerPID as String] as? Int32) == front.processIdentifier && ($0[kCGWindowLayer as String] as? Int) == 0
              }),
              let rawBounds = entry[kCGWindowBounds as String] as? [String: Any],
              let bounds = CGRect(dictionaryRepresentation: rawBounds as CFDictionary), bounds.width >= 350, bounds.height >= 180,
              let main = NSScreen.screens.first else { panel?.orderOut(nil); return }
        // Quartz uses a top-left origin; AppKit uses the main display bottom-left.
        let size = NSSize(width: min(310, bounds.width - 32), height: 44)
        let origin = NSPoint(x: bounds.maxX - size.width - 16, y: main.frame.maxY - bounds.maxY + 16)
        let proposed = NSRect(origin: origin, size: size)
        guard NSScreen.screens.contains(where: { $0.visibleFrame.intersects(proposed) }) else { panel?.orderOut(nil); return }
        panel?.setFrame(proposed, display: true)
        panel?.orderFrontRegardless()
    }

    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate(); model.stop(); panel?.close(); observer = nil
    }
}
