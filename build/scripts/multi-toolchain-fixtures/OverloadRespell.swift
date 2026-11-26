// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// A control fixture of the multi-toolchain package gate (build/scripts/multi-toolchain-package.py,
// control A1): a valid copy the merge must accept. `load(a:b:)` projects to `Load(string?, int?)`; the
// newer build adds `load(c:d:)`, which projects to `Load(string, int)`, another CLR signature of the
// same shape. The control respells the newer copy's record key of `load(a:b:)` as `Load(string,int?)`,
// as an SDK whose nullability annotation changed spells it (a Swift library cannot change a
// parameter's optionality without changing its exports), so the merge must tell the two newer keys
// apart by their compiled signatures.

public final class Loader {
    public init() {}

    public func load(a: String?, b: Int32?) -> Int32 { (b ?? 0) + Int32(a?.count ?? 0) }

#if compiler(>=6.4)
    public func load(c: String, d: Int32) -> Int32 { d * 2 + Int32(c.count) }
#endif
}
