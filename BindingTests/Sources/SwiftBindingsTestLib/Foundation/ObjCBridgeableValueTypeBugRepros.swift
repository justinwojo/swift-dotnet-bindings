// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - objcBridgeable Value-Type Struct Params/Returns (bug a-1)
//
// IndexPath/Calendar/CharacterSet/Locale bridge to an NSObject-family ObjC class
// (NSIndexPath/NSCalendar/NSCharacterSet/NSLocale) but are themselves genuine Swift
// VALUE TYPES (structs). `FoundationDatabase.xml` must register them with
// `kind="struct"` + `objcBridgeable="true"`; a `kind="class"` misregistration makes
// the generator treat them as reference types, corrupting P/Invoke marshalling for
// by-value params/returns (CoreStore/JTAppleCalendar's `Unmanaged` symptom).
//
// New file rather than an addition to `Foundation/Date.swift`: that file is
// deliberately excluded from the compiled test lib
// (`Build.BindingTests.GetMainSourceFiles`'s `Foundation/Date.swift` exclusion,
// pre-existing and unrelated to this fixture — none of its content is exercised
// by any gate), so anything added there would silently never compile or bind.

/// Builds an IndexPath from a section/row pair and returns it by value.
public func makeIndexPath(section: Int32, row: Int32) -> IndexPath {
    return IndexPath(indexes: [Int(section), Int(row)])
}

/// Reads the last index from an IndexPath value parameter.
public func lastIndexPathComponent(_ path: IndexPath) -> Int32 {
    return Int32(path.last ?? -1)
}

/// Returns a short identifier string for the given Calendar value parameter.
public func calendarIdentifierName(_ calendar: Calendar) -> String {
    switch calendar.identifier {
    case .gregorian: return "gregorian"
    case .iso8601: return "iso8601"
    default: return "other"
    }
}

/// Returns the Gregorian calendar by value.
public func gregorianCalendar() -> Calendar {
    return Calendar(identifier: .gregorian)
}

/// Returns true if the given CharacterSet value parameter contains the scalar.
public func characterSetContains(_ set: CharacterSet, scalarValue: Int32) -> Bool {
    guard let scalar = Unicode.Scalar(UInt32(scalarValue)) else { return false }
    return set.contains(scalar)
}

/// Returns the alphanumerics CharacterSet by value.
public func alphanumericCharacterSet() -> CharacterSet {
    return CharacterSet.alphanumerics
}

/// Returns the identifier string of the given Locale value parameter.
public func localeIdentifierName(_ locale: Locale) -> String {
    return locale.identifier
}

/// Returns the en_US_POSIX locale by value.
public func posixLocale() -> Locale {
    return Locale(identifier: "en_US_POSIX")
}
