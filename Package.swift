// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "Morphy",
    platforms: [.macOS(.v14)],
    targets: [.executableTarget(name: "Morphy", path: "Sources/Morphy")]
)
