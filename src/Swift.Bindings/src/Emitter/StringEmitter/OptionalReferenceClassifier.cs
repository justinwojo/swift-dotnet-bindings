// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// ABI predicate for an <c>Optional&lt;reference&gt;</c> in a <b>producer</b> position — one where the
/// generated Swift wrapper explicitly bridges the inner to a nullable object pointer before C# reads it
/// (a witness-table property getter that returns
/// <c>Unmanaged.passRetained(result as AnyObject).toOpaque()</c>, or an <c>@_cdecl</c> wrapper return).
/// In those positions the question is "can wrapper code present this <c>Optional&lt;T&gt;</c> as a
/// nullable reference?", which the canonical <see cref="WrapperValidation.IsOptionalWithReferenceInner"/>
/// oracle answers — including for ObjC-bridgeable value types (<c>URL</c> → <c>NSURL</c>) and the
/// Apple-module / concrete-class fallbacks, because the wrapper materialises the pointer via
/// <c>as AnyObject</c>.
///
/// This is NOT the right question for a closure's native argument/return slot: on the direct
/// CallConvSwift closure path there is no <c>as AnyObject</c> bridge, so Swift passes an
/// <c>Optional&lt;value-type&gt;</c> by its value representation, and the slot is not an object pointer.
/// Those consumer positions use the narrower <see cref="ClosureHandler.IsOptionalReferenceArg"/>
/// (true reference inners only) instead.
/// </summary>
internal static class OptionalReferenceClassifier
{
    /// <summary>
    /// Does this <c>Optional&lt;T&gt;</c> present as a nullable pointer once wrapper code bridges it?
    /// Delegates to the canonical oracle. Use only at producer positions (witness getter / <c>@_cdecl</c>
    /// return) where a Swift-side <c>as AnyObject</c> / <c>passRetained</c> bridge materialises the
    /// pointer — never for a closure's native argument/return slot.
    /// </summary>
    /// <param name="optionalType">The whole <c>Optional&lt;T&gt;</c> type (not the inner T); the
    /// oracle extracts and classifies the inner.</param>
    internal static bool UsesNullablePointerAbi(TypeSpec optionalType, ITypeDatabase typeDatabase)
        => WrapperValidation.IsOptionalWithReferenceInner(optionalType, typeDatabase);
}
