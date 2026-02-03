// swift-tools-version:6.0
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import PackageDescription

let package = Package(
    name: "SwiftBindingsTestLib",
    platforms: [
        .iOS(.v15),
        .macOS(.v13),
    ],
    products: [
        .library(
            name: "SwiftBindingsTestLib",
            type: .dynamic,
            targets: ["SwiftBindingsTestLib"]
        ),
    ],
    targets: [
        .target(
            name: "SwiftBindingsTestLib",
            path: "Sources/SwiftBindingsTestLib",
            swiftSettings: [
                .swiftLanguageMode(.v5),
            ]
        ),
    ]
)
