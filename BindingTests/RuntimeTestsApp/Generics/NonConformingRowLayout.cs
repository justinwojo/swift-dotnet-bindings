// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace SwiftBindingsTestLib;

/// <summary>
/// Makes the projection of the Swift struct <c>NotARowLayout</c> — which deliberately does NOT
/// conform to <c>RowLayout</c> — satisfy the managed <see cref="IRowLayout"/> constraint on
/// <c>SimpleRowAdapter.LayoutedAdapter&lt;T&gt;</c>. Nothing on the Swift side changes: the type's
/// runtime metadata still carries no conformance record.
///
/// <para>
/// This is the adversarial input for the method-level-generic opening wrapper. The wrapper casts
/// the type-argument metadata to <c>any RowLayout.Type</c> before it binds the payload pointer, so
/// a type that gets this far must be refused rather than read at a type it is not. The struct's
/// generated <c>ColumnCount</c> property satisfies the interface implicitly.
/// </para>
/// </summary>
public partial struct NotARowLayout : IRowLayout
{
}
