// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "MusicRecorderMac",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "MusicRecorderMac", targets: ["MusicRecorderMac"])
    ],
    targets: [
        .executableTarget(
            name: "MusicRecorderMac",
            path: "Sources/MusicRecorderMac"
        ),
        .testTarget(
            name: "MusicRecorderMacTests",
            dependencies: ["MusicRecorderMac"],
            path: "Tests/MusicRecorderMacTests"
        )
    ]
)
