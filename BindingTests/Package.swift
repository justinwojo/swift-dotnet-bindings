// swift-tools-version:6.0
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import PackageDescription

let package = Package(
    name: "SwiftBindingsTestLib",
    platforms: [
        .iOS(.v15),
        .macOS(.v13),
        .tvOS(.v15),
    ],
    products: [
        .library(
            name: "SwiftBindingsTestLib",
            type: .dynamic,
            targets: ["SwiftBindingsTestLib"]
        ),
        .library(
            name: "SwiftBindingsTestLibDependency",
            type: .dynamic,
            targets: ["SwiftBindingsTestLibDependency"]
        ),
    ],
    targets: [
        .target(
            name: "SwiftBindingsTestLibDependency",
            path: "Sources/SwiftBindingsTestLibDependency",
            swiftSettings: [
                .swiftLanguageMode(.v5),
            ]
        ),
        .target(
            name: "SwiftBindingsTestLib",
            dependencies: ["SwiftBindingsTestLibDependency"],
            path: "Sources/SwiftBindingsTestLib",
            exclude: [
                // Genuinely unsupported — no generator support for @propertyWrapper
                "PropertyWrappers.disabled",
                "Foundation/Date.swift",  // Date tests separate — not part of URL bridge scope
                // Individual unsupported files within enabled directories
                "Closures/Autoclosures.swift",  // @autoclosure not supported
                "UnsafeTypes/Span.swift",  // Span<T> not supported
                "UnsafeTypes/PointerGenerics.swift",  // Generic<PointerType> emits ISwiftObject constraint violation (CS0315)
            ],
            swiftSettings: [
                .swiftLanguageMode(.v5),
            ]
        ),
    ]
)
