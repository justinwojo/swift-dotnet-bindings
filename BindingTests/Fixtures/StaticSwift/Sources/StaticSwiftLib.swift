// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Static-Swift framework regression fixture: an xcframework whose binary is a static archive.
//
// The xcframework built from this source has:
//   - A static `ar` archive binary (NOT a dylib) — `lipo -info` reports an
//     `ar archive`, NOT a Mach-O dylib.
//   - A complete `Modules/StaticSwiftLib.swiftmodule/<arch>.swiftinterface`
//     that the SDK's binding-generator should detect and route through the
//     Swift code path. The fix swaps the SDK's detect order so the
//     `.swiftinterface` presence is checked BEFORE the binary-kind probe
//     falls back to ObjC.

public struct StaticGreeting {
    public let message: String

    public init(message: String) {
        self.message = message
    }

    public func greet(name: String) -> String {
        return "\(message), \(name)!"
    }
}

@_cdecl("static_swift_greet")
public func staticSwiftGreet() -> Int32 {
    // Cheap symbol the consumer can probe with `nm` to confirm the archive
    // wires up correctly through the linker.
    return 42
}
