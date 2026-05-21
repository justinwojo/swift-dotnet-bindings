// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Pins the Foundation Swift-overlay → Foundation.NS* typed remap end-to-end.
// FoundationDatabase.xml routes these names to typed Foundation.NS*; without
// the typed remap the generated C# would compile against Foundation.NSObject
// and the property/return type assertions in the matching C# test would not
// type-check.
public class FoundationOverlayTypedRemapHelper {
    public let byteCountFormatter: ByteCountFormatter
    public let valueTransformer: ValueTransformer

    public init() {
        let f = ByteCountFormatter()
        f.allowedUnits = .useKB
        f.countStyle = .file
        self.byteCountFormatter = f
        self.valueTransformer = ValueTransformer()
    }

    public func formatBytes(_ count: Int64) -> String {
        return byteCountFormatter.string(fromByteCount: count)
    }
}
