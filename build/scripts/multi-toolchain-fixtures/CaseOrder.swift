// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The multi-toolchain package gate's library fixture (build/scripts/multi-toolchain-package.py).
// Each pairing builds it with its own Xcode, and the Swift 6.4 compiler of Xcode 27 also sees the
// cases below marked "inserted": a newer build inserts cases ahead of existing ones, as an Apple SDK
// does between releases (RealityFoundation 27.2 adds VisualizationMode.baseColorTexture before
// textureCoordinates). The enums are resilient (built with library evolution) and not @frozen, so
// inserting a case is a compatible change for Swift clients.

/// A no-payload enum with a String raw value: projected as a class with a nested CaseTag.
public enum Shade: String {
    case first
#if compiler(>=6.4)
    case inserted
#endif
    case late
}

/// A payload enum: payload cases take the first tags, no-payload cases follow.
public enum Paint {
#if compiler(>=6.4)
    case gradient(Int)
#endif
    case solid(Int)
    case clear
#if compiler(>=6.4)
    case insertedClear
#endif
    case lateClear
}

/// Makes and reads values on the Swift side, so a C# reader sees what Swift wrote and the other way round.
public final class Palette {
    public init() {}

    public func late() -> Shade { .late }

    public func name(of shade: Shade) -> String { shade.rawValue }

    public func solid(_ value: Int) -> Paint { .solid(value) }

    public func lateClear() -> Paint { .lateClear }

    public func describe(_ paint: Paint) -> String {
        switch paint {
        case .solid(let value): return "solid \(value)"
        case .clear: return "clear"
        case .lateClear: return "lateClear"
        default: return "other"
        }
    }
}
