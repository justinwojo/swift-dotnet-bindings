// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Refined-Return Protocol Inheritance (CS0738 covariant-return forwarder)
//
// Two pairs cover both branches of `TryEmitCovariantReturnForwarder` in
// `ProtocolProxyEmitter.InterfaceImpl.cs`:
//
//   1. Subclass case: refined return type IS a C# subclass of the base return type.
//      Generator emits a real cast forwarder via `IsSwiftClassAssignableTo`, so
//      calling through the base interface dispatches to the refined method and
//      returns the up-cast instance.
//
//   2. Sibling case: refined return type and base return type are unrelated classes
//      (both inherit from a common ancestor but neither is the other's superclass).
//      No safe static cast exists, so the generator emits a throwing
//      `NotSupportedException` stub. Calling through the base interface throws.
//
// WCDB's PropertyConvertible/ColumnConvertible pair is the real-world driver for
// the sibling case (Property and Column are siblings despite the protocol-level
// refinement). The subclass case is the more common, well-behaved shape.

// MARK: - Subclass case shapes

/// Base class for the subclass-case refinement.
public class CRTBaseShape {
    public let name: String
    public init(name: String) { self.name = name }
}

/// Subclass of CRTBaseShape — refined protocol returns this; cast to base is safe.
public class CRTRefinedShape: CRTBaseShape {
    public let refinedTag: String
    public init(name: String, refinedTag: String) {
        self.refinedTag = refinedTag
        super.init(name: name)
    }
}

/// Base protocol — declares `makeShape() -> CRTBaseShape`.
public protocol CRTBaseShapeProvider {
    func makeShape() -> CRTBaseShape
}

/// Refined protocol — narrows the return type to the SUBCLASS `CRTRefinedShape`.
/// Cast forwarder path: `CRTBaseShape ICRTBaseShapeProvider.MakeShape() => (CRTBaseShape)this.MakeShape();`
public protocol CRTRefinedShapeProvider: CRTBaseShapeProvider {
    func makeShape() -> CRTRefinedShape
}

/// Concrete type backing the existential returned to C#.
/// Swift requires both overloads explicitly — covariant return on protocol witnesses
/// is not implicit, so the conformer must satisfy both the refined requirement
/// (`makeShape() -> CRTRefinedShape`) and the inherited base requirement
/// (`makeShape() -> CRTBaseShape`). The base overload returns the same refined
/// instance up-cast, mirroring what the C# cast forwarder does at the proxy layer.
public class CRTRefinedShapeImpl: CRTRefinedShapeProvider {
    public init() {}
    public func makeShape() -> CRTRefinedShape {
        CRTRefinedShape(name: "refined-shape", refinedTag: "TAG")
    }
    public func makeShape() -> CRTBaseShape {
        CRTRefinedShape(name: "refined-shape", refinedTag: "TAG")
    }
}

/// Factory returning an existential of the refined protocol — exercises the proxy class.
public func crtMakeRefinedShapeExistential() -> any CRTRefinedShapeProvider {
    CRTRefinedShapeImpl()
}

// MARK: - Sibling case shapes

/// Sibling class A — declared as the base protocol's return type.
public class CRTColumnLike {
    public let columnName: String
    public init(columnName: String) { self.columnName = columnName }
}

/// Sibling class B — declared as the refined protocol's return type.
/// CRTPropertyLike is NOT a subclass of CRTColumnLike; the static cast
/// `(CRTColumnLike)CRTPropertyLike` would not compile.
public class CRTPropertyLike {
    public let propertyName: String
    public init(propertyName: String) { self.propertyName = propertyName }
}

/// Base protocol — returns CRTColumnLike.
public protocol CRTColumnProvider {
    func makeColumn() -> CRTColumnLike
}

/// Refined protocol — refines the return type to the SIBLING `CRTPropertyLike`.
/// Throwing-stub path: `CRTColumnLike ICRTColumnProvider.MakeColumn() => throw new NotSupportedException(...)`
public protocol CRTPropertyProvider: CRTColumnProvider {
    func makeColumn() -> CRTPropertyLike
}

/// Concrete type backing the existential returned to C#.
/// Conforms to BOTH protocols by overload — the refined `makeColumn()` returns
/// CRTPropertyLike, and the base `makeColumn()` (required by CRTColumnProvider)
/// must also be implemented. We satisfy both by using a different return per overload.
public class CRTPropertyImpl: CRTPropertyProvider {
    public init() {}
    public func makeColumn() -> CRTPropertyLike { CRTPropertyLike(propertyName: "prop-1") }
    public func makeColumn() -> CRTColumnLike { CRTColumnLike(columnName: "col-via-base") }
}

/// Factory returning an existential of the refined protocol — exercises the proxy class.
public func crtMakePropertyExistential() -> any CRTPropertyProvider {
    CRTPropertyImpl()
}
