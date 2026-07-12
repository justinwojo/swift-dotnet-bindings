// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @objc delegate protocol that inherits NSCoding, routed through EveryObjCProtocol
//
// Minimum repro for the RoomPlan.RoomCaptureViewDelegate shape: an @objc delegate
// protocol whose declared inheritance includes NSCoding. Historically NSCoding
// disqualified a protocol from the EveryObjCProtocol carrier entirely (alongside
// NSSecureCoding/NSCopying/NSMutableCopying), so the delegate's real dispatch
// members were suppressed and every consumer of `any RenderProgressDelegate`
// degraded.
//
// NSCoding's two requirements — encode(with:) and init?(coder:) — are satisfiable
// no-op on the synthetic carrier: the carrier never archives (real encode/decode
// happens on the C# side via vtable dispatch), so a no-op stub conformance
// (EmitObjCCodingStubIfNeeded) lets the synthesized
// `extension EveryObjCProtocol: RenderProgressDelegate` type-check. The delegate's
// OWN members then reverse-dispatch normally.
//
// This fixture is the minimum repro: an @objc protocol inheriting NSCoding with two
// witness methods, plus free functions that take the existential and invoke each
// witness. The C# side implements the generated interface as a plain managed class —
// auto-wrap must construct an `EveryObjCProtocol`-backed proxy so the Swift call site
// round-trips into the managed implementation. If NSCoding regressed to disqualifying,
// the proxy would be suppressed and these members would not emit at all.

/// @objc delegate protocol that inherits NSCoding (RoomCaptureViewDelegate shape).
@objc public protocol RenderProgressDelegate: NSCoding {
    func reportProgress(_ percent: Int32)
    func currentStage() -> Int32
}

/// Forwards a progress value into the delegate's witness method. Reaching this call
/// already proves NSCoding no longer disqualifies the carrier (else the wrapper module
/// fails to compile); the delegate observing the value proves reverse dispatch works.
public func driveRenderProgress(_ delegate: RenderProgressDelegate, percent: Int32) {
    delegate.reportProgress(percent)
}

/// Reads the delegate's current stage through the existential witness table.
public func readRenderStage(_ delegate: RenderProgressDelegate) -> Int32 {
    return delegate.currentStage()
}
