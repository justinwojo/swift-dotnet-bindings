// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Execution-lane witness for the .NET 11 runtime lanes. The runner calls this through the
// generated binding before each test class. It returns the return address of the managed code
// that made the call, so the host can resolve which code executed the Swift transition: the
// CoreCLR interpreter's call stub in libcoreclr, a ReadyToRun composite image, or a NativeAOT
// executable. The process banner cannot tell the interpreter from ReadyToRun; this can.
//
// The signature is primitive-only on purpose: every runtime places it correctly, so the witness
// cannot itself be disturbed by the interpreter defects the lanes measure.

import Darwin

/// The return address of the first frame outside this library and its generated wrapper module,
/// or 0 when the stack holds no such frame. Frame 0 lies inside this function.
@inline(never)
public func laneProbeCallerAddress() -> UInt {
    let capacity: Int32 = 16
    let frames = UnsafeMutablePointer<UnsafeMutableRawPointer?>.allocate(capacity: Int(capacity))
    defer { frames.deallocate() }
    let total = Int(backtrace(frames, capacity))
    guard total > 1, let own = frames[0], let ownImage = laneProbeImagePath(own) else { return 0 }
    for index in 1..<total {
        guard let frame = frames[index] else { continue }
        let image = laneProbeImagePath(frame)
        if image == ownImage || image.map(laneProbeIsWrapperImage) == true { continue }
        return UInt(bitPattern: frame)
    }
    return 0
}

// A return address can sit one past the end of its caller; resolve the byte before it.
private func laneProbeImagePath(_ frame: UnsafeMutableRawPointer) -> String? {
    var info = Dl_info()
    guard dladdr(frame - 1, &info) != 0, let file = info.dli_fname else { return nil }
    return String(cString: file)
}

// The generator's @_cdecl wrapper framework, should it ever route this call.
private func laneProbeIsWrapperImage(_ path: String) -> Bool {
    path.hasSuffix("/SwiftBindings.framework/SwiftBindings")
}
