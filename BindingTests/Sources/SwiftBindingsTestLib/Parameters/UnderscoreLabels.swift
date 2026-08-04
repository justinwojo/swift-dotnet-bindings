// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Fixtures for the Swift `_:` (no-external-label) argument shape. These
// surfaces all collapse the external label at the swiftinterface boundary,
// so the C# emitter has to invent a name. Pre-fix the emitter would fall
// through to one of three shapes — the typealias name lowercased
// (`cGFloat`), a positional placeholder (`value0`), or a literal underscore
// — none of which are ergonomic for named-argument calls in C#. The
// matching C# regression tests pin reflection-based assertions on the
// generated parameter names so a future regression to any of those shapes
// trips at unit-test time, not on consumer audit.

import Foundation

// Typealias for the `cGFloat` shape — a typealias for CGFloat; the emitter
// projecting the typealias name lowercased was one of the three failure modes.
public typealias UnderscoreLabelAnimationProgress = Double

@frozen
public enum UnderscoreLabelPlaybackMode {
    case progress(_: UnderscoreLabelAnimationProgress)
    case frame(_: Int32)
    case marker(_: String, playEndMarker: Bool)
}

public func underscoreLabel_progressValue(_: UnderscoreLabelAnimationProgress) -> Double {
    return 0.5
}

public class UnderscoreLabelTarget {
    public init() {}

    public func contentsGravity(for _: Int32) -> Int32 {
        return 0
    }
}

// The opposite shape: an external label that GENUINELY begins with an
// underscore. A leading `_` is also how the C#-side name escapes a reserved
// keyword (`default` surfaces as `_default`), so any recovery that undoes the
// escape by unconditionally trimming a leading underscore rewrites `_boxOffset:`
// to `boxOffset:` at the Swift call site — a label the callee does not declare,
// which fails to compile in the generated wrapper rather than misbehaving at
// runtime. Both shapes appear here so the wrapper compile covers each: the
// keyword label must lose its escape, the genuine one must keep its underscore.
public struct UnderscoreLabelBox {
    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func offset(_boxOffset delta: Int32) -> Int32 {
        return value + delta
    }

    public func fallback(default fallbackValue: Int32) -> Int32 {
        return value == 0 ? fallbackValue : value
    }

    // String members take the @_cdecl wrapper path rather than a native thunk, so these two are
    // the ones whose labels are re-rendered at a Swift call site inside the generated wrapper.
    public func describeBox(_boxLabel label: String) -> String {
        return "\(label)=\(value)"
    }

    public func describeFallback(default label: String) -> String {
        return "default:\(label)=\(value)"
    }

    public subscript(_boxIndex index: Int32) -> Int32 {
        return value + index
    }
}
