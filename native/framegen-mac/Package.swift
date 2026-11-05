// swift-tools-version:5.9
// SystemBoost FrameGen for macOS (same algorithm and JSON protocol as the Windows
// sb_framegen.exe). Build: swift build -c release  ->  .build/release/sb-framegen-mac
import PackageDescription

let package = Package(
    name: "SBFrameGenMac",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "sb-framegen-mac", targets: ["SBFrameGenMac"]),
    ],
    targets: [
        .executableTarget(
            name: "SBFrameGenMac",
            path: "Sources/SBFrameGenMac",
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Metal"),
                .linkedFramework("QuartzCore"),
                .linkedFramework("AppKit"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("CoreMedia"),
            ]
        ),
    ]
)
