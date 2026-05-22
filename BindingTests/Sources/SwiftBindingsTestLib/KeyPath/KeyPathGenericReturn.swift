// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - KeyPath returns parameterized by an outer generic
//
// Regression coverage for the AppIntents 0.12.0 EntityQuerySort<Entity>.by shape:
// a generic struct/class whose instance accessor returns PartialKeyPath<TParam>,
// where TParam is the host type's own generic parameter. The bound-generic-class
// return branch in WrapperEmitter previously emitted
// `SwiftMarshal.MarshalFromSwift<IntPtr>(...)` because KeyPathProjection inherited
// the default ContainerTypeName (= PInvokeType = IntPtr). Other container
// projections (Array, Dictionary, Optional, Set, Result) override
// ContainerTypeName to their public C# wrapper type; KeyPath must do the same so
// the accessor's declared return type matches the marshalling generic argument.
//
// Two host shapes here so the fixture covers both projections:
//   - KeyPathGenericSort<TElement>: non-frozen struct → C# class with SafeHandle
//     (mirrors EntityQuerySort exactly).
//   - KeyPathGenericContainer<TElement>: class → C# class.
// Both expose `by` as a stored PartialKeyPath<TElement> and `lookup` as a
// computed accessor returning the same. The accessors are the bug surface; the
// stored property is exercised through the synthesized memberwise init.

public struct KeyPathGenericSort<TElement> {
    public let by: PartialKeyPath<TElement>
    public init(by: PartialKeyPath<TElement>) { self.by = by }
    public var lookup: PartialKeyPath<TElement> { by }
}

public class KeyPathGenericContainer<TElement> {
    public let by: PartialKeyPath<TElement>
    public init(by: PartialKeyPath<TElement>) { self.by = by }
    public var lookup: PartialKeyPath<TElement> { by }
}

// MARK: - 2-arity KeyPath family on generic-host constructors
//
// Phase 4 coverage for the widened gate (IsKeyPathFamilyOfParentGeneric admits
// KeyPath<T,V>, WritableKeyPath<T,V>, ReferenceWritableKeyPath<T,V> when Root
// is a bare parent generic). The 1-arity PartialKeyPath<T> proof above does not
// exercise the 2-arity Render path through RenderSwiftTypeSpecWithSugaredNames
// nor the SafeHandle layout of the typed KeyPath subclasses. Each fixture below
// adds one constructor whose param is the named arity so the static-factory
// shim is emitted and the @_cdecl round-trip is run end-to-end.

public struct KeyPathGenericTypedSort<TElement> {
    public let kp: KeyPath<TElement, Int>
    public init(kp: KeyPath<TElement, Int>) { self.kp = kp }
}

public struct KeyPathGenericWritableSort<TElement> {
    public let kp: WritableKeyPath<TElement, Int>
    public init(kp: WritableKeyPath<TElement, Int>) { self.kp = kp }
}

public struct KeyPathGenericRefSort<TElement> {
    public let kp: ReferenceWritableKeyPath<TElement, Int>
    public init(kp: ReferenceWritableKeyPath<TElement, Int>) { self.kp = kp }
}
