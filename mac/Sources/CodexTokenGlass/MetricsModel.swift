import AppKit
import SwiftUI
import ServiceManagement

struct SessionChoice: Decodable, Identifiable {
    let id: String
    let title: String
}

struct MetricsSnapshot: Decodable {
    var schemaVersion: Int? = 1
    var status: String = "waiting"
    var selectionMode: String? = "desktopDisconnected"
    var sessionId: String?
    var title: String? = ""
    var updatedAt: String?
    var inputTokens: Int64?
    var cachedInputTokens: Int64?
    var uncachedInputTokens: Int64?
    var outputTokens: Int64?
    var reasoningTokens: Int64?
    var totalTokens: Int64?
    var cacheHitPercent: Double?
    var contextUsedTokens: Int64?
    var contextWindowTokens: Int64?
    var speed: Double?
    var averageSpeed: Double?
    var peakSpeed: Double?
    var taskMs: Int64?
    var generationMs: Int64?
    var streaming: Bool? = false
    var held: Bool? = false
    var timingSource: String?
    var carrier: String?
    var themeKind: String?
    var warnings: [String]?
    var sessions: [SessionChoice]?
    var desktopConnected: Bool?
    var desktopLocalCount: Int?
    var desktopRemoteCount: Int?
    var routingError: String?

    static var demo: MetricsSnapshot {
        var value = MetricsSnapshot()
        value.status = "ready"; value.sessionId = "synthetic-demo"; value.title = "Codex Token Glass · Demo"
        value.selectionMode = "desktopObserved"; value.desktopConnected = true; value.desktopLocalCount = 1
        value.inputTokens = 220000; value.cachedInputTokens = 204000; value.uncachedInputTokens = 16000
        value.outputTokens = 1700; value.reasoningTokens = 250; value.totalTokens = 221700
        value.cacheHitPercent = 92.7; value.speed = 100; value.averageSpeed = 100; value.peakSpeed = 200
        value.taskMs = 12000; value.generationMs = 3000; value.carrier = "synthetic"
        value.sessions = [SessionChoice(id: "synthetic-demo", title: "Codex Token Glass · Demo")]
        return value
    }
}

@MainActor
final class MetricsModel: ObservableObject {
    @Published var snapshot = MetricsSnapshot()
    @Published var error: String?
    @Published var settingsVisible = false
    @Published var language: String { didSet { defaults.set(language, forKey: "language") } }
    @Published var transparency: Double { didSet { defaults.set(transparency, forKey: "transparency") } }
    @Published var theme: String { didSet { defaults.set(theme, forKey: "theme") } }
    @Published var floating: Bool { didSet { defaults.set(floating, forKey: "floating") } }
    @Published var selectedThread: String { didSet { defaults.set(selectedThread, forKey: "thread"); restart() } }
    @Published var home: String { didSet { defaults.set(home, forKey: "home"); restart() } }
    @Published var loginEnabled = false
    var showDetails: (() -> Void)?
    var closeDetails: (() -> Void)?
    private let defaults: UserDefaults
    private var pending = Data()
    private var process: Process?
    private var output: Pipe?
    private var errors: Pipe?
    private var generation = UUID()
    private var retry: DispatchWorkItem?
    private var stopping = false
    let isDemo: Bool

    init() {
        let demo = CommandLine.arguments.contains("--demo") || CommandLine.arguments.contains("--smoke-test")
        let preferences = demo ? UserDefaults(suiteName: "CodexTokenGlass.SyntheticPreview")! : UserDefaults.standard
        if demo { preferences.removePersistentDomain(forName: "CodexTokenGlass.SyntheticPreview") }
        defaults = preferences
        language = preferences.string(forKey: "language") ?? "zh"
        transparency = preferences.object(forKey: "transparency") == nil ? 80 : preferences.double(forKey: "transparency")
        theme = preferences.string(forKey: "theme") ?? "auto"
        floating = preferences.object(forKey: "floating") == nil ? true : preferences.bool(forKey: "floating")
        selectedThread = preferences.string(forKey: "thread") ?? ""
        home = demo ? "/Users/demo/.codex" : (preferences.string(forKey: "home") ?? ProcessInfo.processInfo.environment["CODEX_HOME"]
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex").path)
        isDemo = demo
        loginEnabled = demo ? false : SMAppService.mainApp.status == .enabled
        if isDemo { snapshot = .demo }
    }

    func tr(_ chinese: String, _ english: String) -> String { language == "en" ? english : chinese }
    var colorScheme: ColorScheme? {
        let effective = theme == "auto" ? snapshot.themeKind : theme
        return effective == "dark" ? .dark : effective == "light" ? .light : nil
    }
    var speedText: String { number(snapshot.speed) }
    var totalText: String { compact(snapshot.totalTokens) }
    var cacheText: String { snapshot.cacheHitPercent.map { String(format: "%.1f%%", $0) } ?? "—" }
    func number(_ value: Double?) -> String { value.flatMap { $0.isFinite ? String(format: $0 >= 10 ? "%.0f" : "%.1f", $0) : nil } ?? "—" }
    func count(_ value: Int64?) -> String { value.map { $0.formatted(.number.locale(language == "en" ? Locale(identifier: "en_US") : Locale(identifier: "zh_CN"))) } ?? "—" }
    func compact(_ value: Int64?) -> String {
        guard let value else { return "—" }
        if value >= 1_000_000 { return String(format: "%.2fM", Double(value) / 1_000_000) }
        if value >= 1_000 { return String(format: "%.1fK", Double(value) / 1_000) }
        return String(value)
    }
    func seconds(_ value: Int64?) -> String { value.map { String(format: "%.1f s", Double($0) / 1000) } ?? "—" }
    static let recentActivitySelection = "@recent"
    var sourceHint: String {
        switch snapshot.selectionMode {
        case "pinned":
            return tr("已固定会话，切换 Codex 窗口不会自动切换。", "Pinned conversation; does not follow window changes.")
        case "desktopObserved":
            return tr("已跟随桌面报告的唯一会话；多窗口焦点不作保证。", "Following the one conversation reported by Desktop; window focus is not guaranteed.")
        case "desktopAmbiguous":
            return tr("桌面报告了多个会话，请在上方固定一个目标。", "Desktop reports multiple conversations. Pin one above.")
        case "desktopRemote":
            return tr("当前订阅来自远程主机，本机无法读取其记录。可固定一个本地会话。", "The subscription is remote; its logs are not available locally. You can pin a local conversation.")
        case "desktopWaiting":
            if snapshot.routingError == "unsupported-following-version" {
                return tr("当前桌面通知版本暂不兼容。可固定会话或选择“最近活动”。", "This Desktop notification version is not supported. Pin a conversation or choose Recent activity.")
            }
            return tr("正在等待桌面会话通知。没有明确目标时不显示其他会话的数据。", "Waiting for Desktop conversation metadata. Other conversations are not substituted.")
        case "desktopDisconnected":
            if snapshot.routingError == "untrusted-local-peer" {
                return tr("本地桌面连接校验未通过。可固定会话或选择“最近活动”。", "The local Desktop connection could not be verified. Pin a conversation or choose Recent activity.")
            }
            return tr("尚未连接 Codex 桌面。可打开 Codex，或主动选择“最近活动”。", "Desktop is not connected. Open Codex or explicitly choose Recent activity.")
        default:
            return tr("最近活动模式，可能与前台对话不同。", "Recent activity may differ from the foreground conversation.")
        }
    }

    func start() {
        guard !isDemo else { return }
        stopping = false
        error = nil
        let helper = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/CodexTokenMetrics")
        guard FileManager.default.isExecutableFile(atPath: helper.path) else {
            error = tr("缺少数据读取组件，请重新安装完整应用。", "Metrics helper is missing. Reinstall the complete app.")
            return
        }
        let current = UUID(); generation = current
        let child = Process(); child.executableURL = helper
        let selection: [String] = selectedThread == Self.recentActivitySelection ? ["--recent-activity"]
            : selectedThread.isEmpty ? [] : ["--thread", selectedThread]
        child.arguments = ["--watch", "--home", home] + selection
        let pipe = Pipe(); let stderr = Pipe(); child.standardOutput = pipe; child.standardError = stderr
        // stderr is drained without retaining messages, paths, or session contents.
        stderr.fileHandleForReading.readabilityHandler = { handle in
            if handle.availableData.isEmpty { handle.readabilityHandler = nil }
        }
        child.terminationHandler = { [weak self] child in
            DispatchQueue.main.async {
                guard let self, self.generation == current, !self.stopping else { return }
                self.output?.fileHandleForReading.readabilityHandler = nil
                self.errors?.fileHandleForReading.readabilityHandler = nil
                self.snapshot = MetricsSnapshot()
                self.error = self.tr("读取组件已停止，正在重连…", "Metrics helper stopped. Reconnecting…")
                let retry = DispatchWorkItem { [weak self] in self?.start() }
                self.retry = retry; DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: retry)
            }
        }
        pending.removeAll()
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let bytes = handle.availableData
            guard !bytes.isEmpty else { handle.readabilityHandler = nil; return }
            DispatchQueue.main.async {
                guard let self, self.generation == current else { return }
                self.receive(bytes)
            }
        }
        do { try child.run(); process = child; output = pipe; errors = stderr }
        catch {
            pipe.fileHandleForReading.readabilityHandler = nil; stderr.fileHandleForReading.readabilityHandler = nil
            self.error = tr("无法启动数据读取组件。", "Could not start the metrics helper.")
        }
    }

    private func receive(_ bytes: Data) {
        // Accumulate and decode only on the main actor; FileHandle callbacks can
        // arrive concurrently and must not mutate a captured buffer.
        pending.append(bytes)
        if pending.count > 2 * 1024 * 1024 { pending.removeAll(); return }
        while let end = pending.firstIndex(of: 10) {
            let line = Data(pending.prefix(upTo: end)); pending.removeSubrange(...end)
            guard let value = try? JSONDecoder().decode(MetricsSnapshot.self, from: line) else { continue }
            snapshot = value; error = nil
        }
    }

    func stop() {
        stopping = true; generation = UUID(); retry?.cancel(); retry = nil
        output?.fileHandleForReading.readabilityHandler = nil
        errors?.fileHandleForReading.readabilityHandler = nil
        if process?.isRunning == true { process?.terminate() }
        process = nil; output = nil; errors = nil
    }
    func restart() { stop(); snapshot = isDemo ? .demo : MetricsSnapshot(); start() }
    func chooseHome() {
        let chooser = NSOpenPanel(); chooser.canChooseDirectories = true; chooser.canChooseFiles = false
        chooser.showsHiddenFiles = true; chooser.allowsMultipleSelection = false
        chooser.message = tr("选择 Codex 数据目录（通常为 ~/.codex）", "Choose the Codex data directory (usually ~/.codex)")
        chooser.directoryURL = URL(fileURLWithPath: home)
        if chooser.runModal() == .OK, let url = chooser.url { home = url.path }
    }
    func setLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginEnabled = SMAppService.mainApp.status == .enabled
            if enabled && !loginEnabled { error = tr("请在系统设置的登录项中允许启动。", "Allow this app in System Settings → Login Items.") }
        } catch { self.error = tr("无法更改登录项，请检查系统设置。", "Could not update Login Items. Check System Settings.") }
    }
}
