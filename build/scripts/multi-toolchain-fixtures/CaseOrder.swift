// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The multi-toolchain package gate's library fixture (build/scripts/multi-toolchain-package.py).
// Each pairing builds it with its own Xcode, and the Swift 6.4 compiler of Xcode 27 also sees the
// cases below marked "inserted": a newer build inserts cases ahead of existing ones, as an Apple SDK
// does between releases (RealityFoundation 27.2 adds VisualizationMode.baseColorTexture before
// textureCoordinates), and gives a caseless namespace enum its first static member (RealityFoundation
// 27.2 does so to Audio), and gives a member-less struct used the same way its first initializer. The
// enums are resilient (built with library evolution) and not @frozen, so all are compatible changes for
// Swift clients.

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

/// A plain no-payload enum without a raw value: projected as a C# enum whose constants are keyed by
/// case name, so the inserted case moves none of them.
public enum Mood {
    case calm
#if compiler(>=6.4)
    case inserted
#endif
    case bright
}

/// A caseless enum used as a namespace: projected as a static class in every copy, so its nested type
/// keeps one CLR name when the newer build adds a member.
public enum Studio {
#if compiler(>=6.4)
    public static var revision: Int { 2 }
#endif

    public final class Easel {
        public init() {}

        public var label: String { "easel" }
    }
}

/// A member-less struct used as a namespace: projected as a static class while it has no member, and as
/// an ordinary struct type once the newer build gives it an initializer, so its nested type keeps one CLR
/// name in both copies.
public struct Workshop {
#if compiler(>=6.4)
    public init() {}
#endif

    public final class Bench {
        public init() {}

        public var label: String { "bench" }
    }
}

/// Makes and reads values on the Swift side, so a C# reader sees what Swift wrote and the other way round.
public final class Palette {
    public init() {}

    public func late() -> Shade { .late }

    public func name(of shade: Shade) -> String { shade.rawValue }

    public func solid(_ value: Int) -> Paint { .solid(value) }

    public func lateClear() -> Paint { .lateClear }

    public func bright() -> Mood { .bright }

    public func moodName(_ mood: Mood) -> String {
        switch mood {
        case .calm: return "calm"
        case .bright: return "bright"
        default: return "other"
        }
    }

    /// Which compiler built this library, so a run can tell the newer build from the older one.
#if compiler(>=6.4)
    public var compiler: String { "swift-6.4+" }
#else
    public var compiler: String { "swift-pre-6.4" }
#endif

    public func describe(_ paint: Paint) -> String {
        switch paint {
        case .solid(let value): return "solid \(value)"
        case .clear: return "clear"
        case .lateClear: return "lateClear"
        default: return "other"
        }
    }
}
