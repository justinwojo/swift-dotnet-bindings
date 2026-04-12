// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - UnsafeRawBufferParam (fix #11)
//
// Synthetic fixture for commit 26f764f1's "UnsafeRawBufferPointer parameter
// deferral". The generator must:
//
//   (a) skip `readBuffer(_:)` with `Reason=UnsupportedSignature` in
//       binding-report.json — asserted by the Nuke build-side step
//       `AssertBindingReportConstraints` in build/Build.BindingTests.cs.
//   (b) keep the rest of the struct intact — asserted at runtime by the C#
//       test calling `multiplier(_:)`.
//
// The hazard fix #11 protects against is an emitter-side failure mode where
// a single unsupported parameter type propagates up and drops the entire
// type. If that regresses, the build-side assertion can still pass (the
// skip entry is recorded) while the runtime test fails to compile because
// the type is missing from the generated C#. Two-layer coverage catches
// either direction of regression.

/// Struct that exercises the `UnsafeRawBufferPointer` parameter deferral
/// path. One member is deliberately unsupported; the other must still
/// compile and run.
public struct UnsafeRawBufferHolder {
    public let scale: Int32

    public init(scale: Int32) {
        self.scale = scale
    }

    /// Plain method that must survive the deferral of `readBuffer(_:)`. The
    /// runtime test calls this to prove the enclosing type is still intact.
    public func multiplier(_ value: Int32) -> Int32 {
        return value * scale
    }

    /// Method that will be skipped by fix #11 with reason
    /// `UnsupportedSignature`. The Nuke build-side assertion reads
    /// binding-report.json to confirm the skip reason. If this regresses —
    /// i.e., the generator starts emitting a wrapper — the assertion will
    /// fail and remind the fixer to update both layers at once.
    public func readBuffer(_ buffer: UnsafeRawBufferPointer) -> Int32 {
        return Int32(buffer.count)
    }
}
