// swift-tools-version: 5.10
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import PackageDescription

// Pinned swift-syntax tag.
//
// 604.0.x understands the Module::Type spelling emitted by Swift 6.4. Keep it
// buildable with our Swift 6.2 (Xcode 26) host and use one deliberate pin across
// toolchains: using the host's bundled SwiftSyntax would change fact extraction
// whenever Xcode changes. Fact text canonicalizes module selectors to ABI dots.
let swiftSyntaxVersion: Version = "604.0.0"

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
