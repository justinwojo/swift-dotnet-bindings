// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async tuple results with a simple-enum element
//
// An async tuple result reaches C# through a completion callback whose parameters are the
// tuple's elements. A simple-enum element must cross as the scalar its synchronous return
// crosses as (the C# constant of its case, or its raw value), never as a pointer to a heap copy.
// The integer after the enum checks that the elements did not shift.

/// A plain enum: no raw value, no hidden case.
public enum AsyncTuplePlain {
    case north, east, south, west
}

/// An integral raw-value enum whose raw values differ from its case indices.
public enum AsyncTupleRaw: Int {
    case low = 3
    case mid = 7
    case high = 11
}

/// A one-byte raw-value enum, so the enum element is narrower than the integer after it.
public enum AsyncTupleSmall: UInt8 {
    case tiny = 1
    case small = 2
    case big = 200
}

public func caseShiftPairAsync(_ count: Int) async -> (CaseShift, Int) { (count == 0 ? .hidden : .second, count) }
public func asyncTuplePlainPair(_ count: Int) async -> (AsyncTuplePlain, Int) { (count == 0 ? .west : .east, count) }
public func asyncTupleRawPair(_ count: Int) async -> (AsyncTupleRaw, Int) { (count == 0 ? .high : .mid, count) }
public func asyncTupleSmallTriple(_ count: Int) async -> (Int, AsyncTupleSmall, Int) { (count, .big, count + 1) }
public func asyncTupleObjCPair(_ count: Int) async -> (AuthErrorCodeLike, Int) { (.networkError, count) }
