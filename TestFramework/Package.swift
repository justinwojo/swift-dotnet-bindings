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
                "Async.disabled",
                "Closures.disabled",
                "EdgeCases.disabled",
                "ErrorHandling.disabled",
                "Generics",  // Has unbound generic issues
                "Initializers.disabled",
                "Lifetime.disabled",
                "MemoryManagement.disabled",
                "ObjCInterop.disabled",
                "Operators",  // Disabled operators
                "Parameters.disabled",
                "PropertyWrappers.disabled",
                "Tuples",  // Tuple type conversion issues
                "UnsafeTypes.disabled",
                "Foundation",  // Foundation types not fully supported
                "Protocols",  // Protocol issues
            ],
            swiftSettings: [
                .swiftLanguageMode(.v5),
            ]
        ),
    ]
)
