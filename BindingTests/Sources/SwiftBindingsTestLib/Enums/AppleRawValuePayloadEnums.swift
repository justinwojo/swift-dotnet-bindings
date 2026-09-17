// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import CoreLocation
#if canImport(UIKit)
import UIKit
#endif

// MARK: - Enum payloads whose type is an imported C enum
//
// An imported C enum's raw value is its C storage type (`CLAuthorizationStatus` is `Int32`), which
// the wrapper converts through when it lifts the payload across the boundary. A raw type recorded
// wrongly surfaces as an Int/Int32 mismatch in the wrapper. Imported C enums are open, so
// `init(rawValue:)` accepts a value no case names; only a negative status selects the payloadless case.

public enum LocationPermissionEvent {
    case changed(CLAuthorizationStatus)
    case unknown

    public static func make(rawStatus: Int32) -> LocationPermissionEvent {
        guard rawStatus >= 0, let status = CLAuthorizationStatus(rawValue: rawStatus) else { return .unknown }
        return .changed(status)
    }

    public var rawStatus: Int32 {
        if case .changed(let status) = self { return status.rawValue }
        return -1
    }
}

#if canImport(UIKit)
public enum BarItemEvent {
    case tapped(UIBarButtonItem.SystemItem)
    case none

    public static func tapping(rawItem: Int) -> BarItemEvent {
        guard rawItem >= 0, let item = UIBarButtonItem.SystemItem(rawValue: rawItem) else { return .none }
        return .tapped(item)
    }

    public var rawItem: Int {
        if case .tapped(let item) = self { return item.rawValue }
        return -1
    }
}
#endif
