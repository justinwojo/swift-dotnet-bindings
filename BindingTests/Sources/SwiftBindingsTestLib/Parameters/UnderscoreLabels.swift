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

// Typealias for the `cGFloat` shape — Lottie's AnimationProgressTime is a
// typealias for CGFloat; the emitter projecting the typealias name
// lowercased was one of the three failure modes.
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
