// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "CodexTokenGlass",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "CodexTokenGlass", targets: ["CodexTokenGlass"])],
    targets: [.executableTarget(name: "CodexTokenGlass")],
    swiftLanguageModes: [.v5]
)
