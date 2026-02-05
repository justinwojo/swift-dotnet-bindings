// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Migrated from FunctionalTests/MemoryTests/MemoryTests.swift.
// Tests deinit tracking and struct-with-ref-field patterns that exercise
// the generator's Buffer vs SafeHandle emission paths.

// MARK: - Deinit Tracking

/// Reference type that tracks deinitialization via an unsafe pointer.
/// When the object is deallocated, `deinit` sets the pointee to 1,
/// allowing tests to verify that ARC cleanup occurred.
public class DeinitTracker {
    public var test: UnsafeMutablePointer<Int64>

    public init(test: UnsafeMutablePointer<Int64>) {
        self.test = test
    }

    deinit {
        test.pointee = 1
    }
}

// MARK: - Non-Frozen Struct with Ref at Offset 0

/// Non-frozen struct containing a reference type at offset 0.
/// The binding generator must emit ClassWithOpaquePayload (SafeHandle)
/// and properly invoke InitWithCopy/Destroy value witness functions.
public struct StructWithRefAtZero {
    public var refType: DeinitTracker
    private var refTypeTestPtr: UnsafeMutablePointer<Int64>

    public init() {
        refTypeTestPtr = UnsafeMutablePointer<Int64>.allocate(capacity: 1)
        refTypeTestPtr.initialize(to: 0)
        refType = DeinitTracker(test: refTypeTestPtr)
    }

    public init(refType: DeinitTracker) {
        self.refType = refType
        refTypeTestPtr = refType.test
    }

    public var refTypeTest: Int64 {
        get { return refTypeTestPtr.pointee }
    }

    public func cleanup() {
        refTypeTestPtr.deinitialize(count: 1)
        refTypeTestPtr.deallocate()
    }
}

// MARK: - Nested Non-Frozen Struct with Refs at Offsets 0, 16

/// Non-frozen struct with reference types at offsets 0 and 16.
/// Tests nested value witness operations across multiple ref fields.
public struct NestedStructWithRefs {
    public var refType: DeinitTracker
    private var refTypeTest1Ptr: UnsafeMutablePointer<Int64>
    public var inner: StructWithRefAtZero

    public init() {
        refTypeTest1Ptr = UnsafeMutablePointer<Int64>.allocate(capacity: 1)
        refTypeTest1Ptr.initialize(to: 0)
        refType = DeinitTracker(test: refTypeTest1Ptr)

        let refTypeTest2Ptr = UnsafeMutablePointer<Int64>.allocate(capacity: 1)
        refTypeTest2Ptr.initialize(to: 0)
        inner = StructWithRefAtZero(refType: DeinitTracker(test: refTypeTest2Ptr))
    }

    public init(refType1: DeinitTracker, refType2: DeinitTracker) {
        refTypeTest1Ptr = refType1.test
        refType = refType1
        inner = StructWithRefAtZero(refType: refType2)
    }

    public var refTypeTest1: Int64 {
        get { return refTypeTest1Ptr.pointee }
    }

    public var refTypeTest2: Int64 {
        get { return inner.refTypeTest }
    }

    public func cleanup() {
        inner.cleanup()
        refTypeTest1Ptr.deinitialize(count: 1)
        refTypeTest1Ptr.deallocate()
    }
}

// MARK: - Frozen Struct with Ref Field (ClassWithBufferStruct emission)

/// Frozen struct containing a reference type field.
/// The binding generator must emit ClassWithBufferStruct — a C# class
/// wrapping a Buffer inner struct that includes the ref field as IntPtr.
@frozen
public struct FrozenStructWithRef {
    public var a: DeinitTracker
    public var b: Int32

    public init(b: Int32) {
        self.a = DeinitTracker(test: UnsafeMutablePointer<Int64>.allocate(capacity: 1))
        self.b = b
    }

    public func getValue() -> Int32 {
        return b
    }

    public func callDispose(callback: @escaping () -> Void) {
        callback()
    }
}

// MARK: - Nested Frozen Struct with Ref

/// Frozen struct nesting another frozen struct that contains a ref field.
/// Tests that ClassWithBufferStruct emission propagates through nesting.
@frozen
public struct NestedFrozenStructWithRef {
    public var a: FrozenStructWithRef
    public var b: Int32

    public init(b: Int32) {
        self.a = FrozenStructWithRef(b: b)
        self.b = b
    }

    public func getValue() -> Int32 {
        return b
    }
}

// MARK: - Inner Layout Helper

/// Simple frozen struct with two primitive fields, used as a layout component
/// in EmbeddedStructWithRefAtOffset.
@frozen
public struct InnerFrozenLayout {
    public var x: Int32
    public var y: UInt8

    public init() {
        self.x = 1
        self.y = 2
    }
}

// MARK: - Embedded Struct with Ref at Non-Zero Offset

/// Frozen struct where the reference type field is at offset 8 (after
/// InnerFrozenLayout which occupies bytes 0-7). Tests that the binding
/// generator correctly handles ref fields at non-zero offsets.
@frozen
public struct EmbeddedStructWithRefAtOffset {
    public var x: InnerFrozenLayout
    public var y: UInt8
    public var z: DeinitTracker // offset is 8

    public init() {
        self.x = InnerFrozenLayout()
        self.y = 3
        self.z = DeinitTracker(test: UnsafeMutablePointer<Int64>.allocate(capacity: 1))
    }
}

// MARK: - Pass-Through Functions

/// Pass-through for frozen struct with ref field (ClassWithBufferStruct path).
public func passThroughFrozenWithRef(a: FrozenStructWithRef) -> FrozenStructWithRef {
    return a
}

/// Pass-through for nested frozen struct with ref field.
public func passThroughNestedFrozenWithRef(a: NestedFrozenStructWithRef) -> NestedFrozenStructWithRef {
    return a
}

/// Pass-through for non-frozen struct with ref field (SafeHandle path).
public func passThroughNonFrozenWithRef(a: StructWithRefAtZero) -> StructWithRefAtZero {
    return a
}

/// Pass-through for embedded struct with ref at non-zero offset.
public func passThroughEmbeddedStruct(a: EmbeddedStructWithRefAtOffset) -> EmbeddedStructWithRefAtOffset {
    return a
}

/// Generic pass-through function.
public func passThroughGenericValue<T>(a: T) -> T {
    return a
}
