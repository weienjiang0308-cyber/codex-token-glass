import SwiftUI
import AppKit

// Apple's material is drawn by the OS; no screenshot sampling or custom shader.
struct GlassSurface: ViewModifier {
    var radius: CGFloat = 22
    var transparency: Double
    var interactive = false
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    func body(content: Content) -> some View {
        content.background {
            let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)
            if reduceTransparency {
                shape.fill(Color(nsColor: .windowBackgroundColor))
            } else if #available(macOS 26.0, *) {
                // Apply transparency to the backing material, never to labels.
                let glass: Glass = transparency >= 60 ? .clear : .regular
                shape.fill(Color(nsColor: .windowBackgroundColor).opacity((100 - transparency) / 160))
                    .glassEffect(interactive ? glass.interactive() : glass, in: shape)
            } else {
                shape.fill(.ultraThinMaterial)
                    .overlay(shape.fill(Color(nsColor: .windowBackgroundColor).opacity((100 - transparency) / 130)))
            }
        }
    }
}

struct GlassButton: ButtonStyle {
    var transparency: Double
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .frame(minWidth: 32, minHeight: 32)
            .contentShape(RoundedRectangle(cornerRadius: 12))
            .modifier(GlassSurface(radius: 12, transparency: transparency, interactive: true))
            .scaleEffect(configuration.isPressed && !reduceMotion ? 0.96 : 1)
            .animation(reduceMotion ? nil : .spring(response: 0.22, dampingFraction: 0.8), value: configuration.isPressed)
    }
}

struct FloatingStrip: View {
    @ObservedObject var model: MetricsModel
    var body: some View {
        Button { model.showDetails?() } label: {
            HStack(spacing: 14) {
                Label(model.speedText + " tok/s", systemImage: "speedometer")
                Text(model.totalText)
                Label(model.cacheText, systemImage: "memorychip")
            }
            .font(.system(size: 12, weight: .medium, design: .rounded))
            .monospacedDigit()
            .padding(.horizontal, 16).padding(.vertical, 11)
        }
        .buttonStyle(.plain)
        .modifier(GlassSurface(radius: 20, transparency: model.transparency, interactive: true))
        .padding(4)
        .preferredColorScheme(model.colorScheme)
        .accessibilityLabel(model.tr("打开 Token 用量详情", "Open token usage details"))
    }
}

struct DetailView: View {
    @ObservedObject var model: MetricsModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text(model.settingsVisible ? model.tr("设置", "Settings") : "Codex Token Glass")
                        .font(.system(size: 18, weight: .semibold))
                    Text(model.tr("本地读取 · 仅在你的设备上", "Local metrics · On your device"))
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                }
                Spacer()
                Button {
                    withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.18)) { model.settingsVisible.toggle() }
                } label: { Image(systemName: model.settingsVisible ? "chevron.left" : "gearshape").font(.system(size: 14, weight: .medium)) }
                    .buttonStyle(GlassButton(transparency: model.transparency))
                    .accessibilityLabel(model.settingsVisible ? model.tr("返回详情", "Back to details") : model.tr("设置", "Settings"))
                Button { model.closeDetails?() } label: { Image(systemName: "xmark").font(.system(size: 12, weight: .semibold)) }
                    .buttonStyle(GlassButton(transparency: model.transparency))
                    .accessibilityLabel(model.tr("关闭", "Close"))
                    .keyboardShortcut(.cancelAction)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    if model.settingsVisible { settings } else { details }
                    if let error = model.error {
                        Label(error, systemImage: "exclamationmark.circle")
                            .font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(.vertical, 2)
            }.scrollIndicators(.hidden)
            HStack {
                Text(model.isDemo ? model.tr("合成演示数据", "Synthetic demo data") : model.tr("日志时间统计，包含推理输出", "Log timing · Includes reasoning output"))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                Spacer()
                Button(model.tr("退出", "Quit")) { NSApplication.shared.terminate(nil) }
                    .buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
        .padding(22)
        .frame(width: 410, height: ProcessInfo.processInfo.environment["CODEX_TOKEN_GLASS_PREVIEW_DIR"] == nil ? 640 : 760)
        .modifier(GlassSurface(radius: 26, transparency: model.transparency))
        .preferredColorScheme(model.colorScheme)
        .tint(.primary)
    }

    private var details: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 8) {
                Text(model.snapshot.title?.isEmpty == false ? model.snapshot.title! : model.tr("等待 Codex 使用记录", "Waiting for Codex usage"))
                    .font(.system(size: 13, weight: .medium)).lineLimit(2).textSelection(.enabled)
                sessionPicker
                Text(model.selectedThread.isEmpty
                    ? model.tr("最近活动模式，未验证当前窗口的会话", "Recent activity; foreground conversation is not verified")
                    : model.tr("已固定会话，切换 Codex 窗口不会自动切换", "Pinned conversation; does not follow window changes"))
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            HStack(alignment: .firstTextBaseline, spacing: 20) {
                metric(model.tr("输出速度", "Output speed"), value: model.speedText, unit: "tok/s")
                Spacer(minLength: 0)
                metric(model.tr("总 Token", "Total tokens"), value: model.totalText)
                Spacer(minLength: 0)
                metric(model.tr("缓存命中", "Cache hit"), value: model.cacheText)
            }
            Divider().opacity(0.6)
            VStack(spacing: 12) {
                row(model.tr("输入 Token", "Input tokens"), model.count(model.snapshot.inputTokens))
                row(model.tr("缓存输入", "Cached input"), model.count(model.snapshot.cachedInputTokens))
                row(model.tr("未缓存输入", "Uncached input"), model.count(model.snapshot.uncachedInputTokens))
                row(model.tr("输出 Token", "Output tokens"), model.count(model.snapshot.outputTokens))
                row(model.tr("其中推理 Token", "Reasoning tokens"), model.count(model.snapshot.reasoningTokens))
            }
            Divider().opacity(0.6)
            VStack(spacing: 12) {
                row(model.tr("本轮平均速度", "Turn average"), unitSpeed(model.snapshot.averageSpeed))
                row(model.tr("响应峰值均速", "Peak response average"), unitSpeed(model.snapshot.peakSpeed))
                row(model.tr("任务用时", "Task duration"), model.seconds(model.snapshot.taskMs))
                row(model.tr("模型生成时间", "Model generation time"), model.seconds(model.snapshot.generationMs))
            }
            if model.snapshot.status != "ready" {
                Text(model.tr("尚无可显示的用量。请在 Codex 中完成一次响应，或在设置中选择正确的数据目录。", "No usage is available yet. Complete a response in Codex or choose its data directory in Settings."))
                    .font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }
    private var settings: some View {
        VStack(alignment: .leading, spacing: 22) {
            VStack(alignment: .leading, spacing: 12) {
                Text(model.tr("外观", "Appearance")).font(.system(size: 13, weight: .semibold))
                Picker(model.tr("语言", "Language"), selection: $model.language) {
                    Text("中文").tag("zh"); Text("English").tag("en")
                }.pickerStyle(.segmented)
                Picker(model.tr("配色", "Theme"), selection: $model.theme) {
                    Text(model.tr("跟随 Codex", "Follow Codex")).tag("auto")
                    Text(model.tr("浅色", "Light")).tag("light")
                    Text(model.tr("深色", "Dark")).tag("dark")
                }
                Text(model.tr("读取 Codex 的明暗设置；不可用时跟随 macOS。", "Uses Codex light/dark preference; falls back to macOS."))
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                HStack {
                    Text(model.tr("玻璃清透度", "Glass clarity"))
                    Spacer(); Text("\(Int(model.transparency))%").monospacedDigit().foregroundStyle(.secondary)
                }.font(.system(size: 12))
                Slider(value: $model.transparency, in: 0...100, step: 1) { Text(model.tr("玻璃清透度", "Glass clarity")) }
                    .labelsHidden().accessibilityLabel(model.tr("玻璃清透度", "Glass clarity"))
                Text(model.tr("macOS 26 使用原生 Liquid Glass；较早系统使用系统模糊材质。系统的减少透明度设置优先。", "Native Liquid Glass on macOS 26; system blur material on earlier versions. Reduce Transparency takes priority."))
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Divider().opacity(0.6)
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text(model.tr("在 Codex 窗口旁显示浮条", "Show strip beside the Codex window"))
                    Spacer()
                    Toggle("", isOn: $model.floating).labelsHidden()
                        .accessibilityLabel(model.tr("在 Codex 窗口旁显示浮条", "Show strip beside the Codex window"))
                }
                HStack {
                    Text(model.tr("登录时启动", "Launch at login"))
                    Spacer()
                    Toggle("", isOn: Binding(get: { model.loginEnabled }, set: { model.setLogin($0) })).labelsHidden()
                        .accessibilityLabel(model.tr("登录时启动", "Launch at login"))
                }
                Text(model.tr("浮条跟随 Codex 窗口外框，第一版不定位输入框工具栏。", "The strip follows the Codex window frame. Composer toolbar docking is not included yet."))
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }.font(.system(size: 12)).toggleStyle(.switch).controlSize(.small)
            Divider().opacity(0.6)
            VStack(alignment: .leading, spacing: 10) {
                Text(model.tr("数据目录", "Data directory")).font(.system(size: 13, weight: .semibold))
                Text(model.home).font(.system(size: 11)).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(2)
                Button(model.tr("选择 Codex 数据目录…", "Choose Codex data directory…")) { model.chooseHome() }
                Text(model.tr("仅读取会话与用量，不修改模型、密钥、上下文或工具缓冲。", "Reads usage only. Model, credentials, context, and tool headroom stay untouched."))
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var sessionPicker: some View {
        Picker(model.tr("会话", "Conversation"), selection: $model.selectedThread) {
            Text(model.tr("最近活动", "Recent activity")).tag("")
            ForEach(model.snapshot.sessions ?? []) { session in
                Text(session.title).tag(session.id)
            }
            if !model.selectedThread.isEmpty && !(model.snapshot.sessions ?? []).contains(where: { $0.id == model.selectedThread }) {
                Text(model.tr("固定会话（暂不可用）", "Pinned conversation (unavailable)")).tag(model.selectedThread)
            }
        }.font(.system(size: 12))
    }
    private func unitSpeed(_ value: Double?) -> String { value == nil ? "—" : model.number(value) + " tok/s" }
    private func row(_ title: String, _ value: String) -> some View {
        HStack { Text(title).foregroundStyle(.secondary); Spacer(); Text(value).monospacedDigit().textSelection(.enabled) }
            .font(.system(size: 12))
    }
    private func metric(_ title: String, value: String, unit: String? = nil) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 11)).foregroundStyle(.secondary)
            Text(value).font(.system(size: 26, weight: .semibold, design: .rounded)).monospacedDigit().minimumScaleFactor(0.7).lineLimit(1)
            if let unit { Text(unit).font(.system(size: 10)).foregroundStyle(.secondary) }
        }.accessibilityElement(children: .combine)
    }
}
