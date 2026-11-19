// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// A public declaration in an <c>extension X { ... }</c> block that the interface reader saw but did
/// not turn into an <see cref="ExtensionMemberCandidate"/>: an initializer, subscript, operator,
/// typealias or nested type, a member whose modifiers or effects the extension path does not read,
/// or every member of an extension whose header it does not read. Recorded so the member gets a
/// skip row where no other path reports it (<see cref="ForeignTypeExtensionEmitter"/>), instead of
/// vanishing from the binding with no record.
/// </summary>
public sealed class DroppedExtensionCandidate
{
    /// <summary>The extension target with module selectors rendered as dots, generic arguments removed
    /// (<c>UIKit.UIView</c>, <c>Mod.Box</c>).</summary>
    public required string ExtendedTypeName { get; init; }

    /// <summary>What the declaration is: <c>method</c>, <c>property</c>, <c>initializer</c>,
    /// <c>subscript</c>, <c>operator</c>, <c>typealias</c> or <c>type</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The declaration's own name (<c>init</c>, <c>subscript</c>, an operator's symbol).</summary>
    public required string Name { get; init; }

    /// <summary>The name with its argument labels (<c>init(x:)</c>), or the name alone.</summary>
    public required string PrintedName { get; init; }

    /// <summary>The declaration's source text, whitespace collapsed; keeps overloads apart.</summary>
    public required string RawSignature { get; init; }

    public bool IsStatic { get; init; }

    /// <summary>Why the reader did not take it, in words for the skip row.</summary>
    public required string Reason { get; init; }
}
