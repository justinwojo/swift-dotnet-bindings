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
            exclude: [
                // Temporarily disabled directories (generator bugs)
                "EdgeCases.disabled",
                "Initializers.disabled",
                "Lifetime.disabled",
                "MemoryManagement.disabled",
                "ObjCInterop.disabled",
                "Parameters.disabled",
                "PropertyWrappers.disabled",
                "Foundation",  // Foundation types not fully supported
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
