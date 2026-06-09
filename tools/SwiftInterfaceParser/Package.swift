// swift-tools-version: 5.10
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import PackageDescription

// Pinned swift-syntax tag.
//
// 601.0.x is the swift-syntax line that ships with the Swift 6.1 compiler and remains
// source-compatible against newer toolchains. Bump only deliberately when adopting new
// node shapes. Do NOT track the host toolchain's bundled SwiftSyntax: that pulls in
// node-shape changes mid-stream and breaks the regex-vs-SwiftSyntax parity gate.
let swiftSyntaxVersion: Version = "601.0.1"

let package = Package(
    name: "SwiftInterfaceParser",
    platforms: [
        // Host-tool only — runs on developer / CI macs that build .NET bindings. Never
        // ships into iOS/tvOS/Catalyst app bundles. The .swiftinterface text we parse
        // is platform-agnostic; the host platform here is just where the CLI process runs.
        .macOS(.v13),
    ],
    products: [
        .executable(name: "SwiftInterfaceParser", targets: ["SwiftInterfaceParser"]),
    ],
    dependencies: [
        .package(url: "https://github.com/swiftlang/swift-syntax", exact: swiftSyntaxVersion),
    ],
    targets: [
        .executableTarget(
            name: "SwiftInterfaceParser",
            dependencies: [
                .product(name: "SwiftSyntax", package: "swift-syntax"),
                .product(name: "SwiftParser", package: "swift-syntax"),
            ]
        ),
    ]
)
