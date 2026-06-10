// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Large Enum

/// Large enum (50+ cases) testing DestructiveInjectEnumTag scalability.
public enum DeviceModel {
    case phone1, phone2, phone3, phone4, phone5
    case phone6, phone7, phone8, phone9, phone10
    case tablet1, tablet2, tablet3, tablet4, tablet5
    case tablet6, tablet7, tablet8, tablet9, tablet10
    case watch1, watch2, watch3, watch4, watch5
    case laptop1, laptop2, laptop3, laptop4, laptop5
    case desktop1, desktop2, desktop3, desktop4, desktop5
    case tv1, tv2, tv3, tv4, tv5
    case speaker1, speaker2, speaker3, speaker4, speaker5
    case accessory1, accessory2, accessory3, accessory4, accessory5
    // Payload cases
    case unknown(identifier: String)
    case custom(name: String, year: Int32)
}

/// Describe a device model.
public func deviceDescription(_ model: DeviceModel) -> String {
    switch model {
    case .phone1: return "Phone 1"
    case .tablet1: return "Tablet 1"
    case .watch1: return "Watch 1"
    case .laptop1: return "Laptop 1"
    case .desktop1: return "Desktop 1"
    case .tv1: return "TV 1"
    case .speaker1: return "Speaker 1"
    case .accessory1: return "Accessory 1"
    case .unknown(let id): return "Unknown: \(id)"
    case .custom(let name, let year): return "\(name) (\(year))"
    default: return "Device"
    }
}
